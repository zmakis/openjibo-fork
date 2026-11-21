using System.Net.Http.Headers;
using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Prefer a warm whisper.cpp <c>whisper-server</c> process so the model stays loaded
/// between turns. Falls behind Synthetic / Azure in the selector but ahead of the
/// per-turn CLI spawn strategy.
/// </summary>
public sealed class WhisperServerBufferedAudioSttStrategy(
    BufferedAudioSttOptions options,
    IExternalProcessRunner processRunner,
    HttpClient httpClient,
    ILogger<WhisperServerBufferedAudioSttStrategy> logger)
    : ISttStrategy
{
    private const int MinimumBufferedAudioBytes = 64;
    private const int ShortAnswerBufferedAudioBytes = 16;
    private const int MinimumTranscribableWavBytes = 1024;

    private const string FfmpegAudioPreprocessFilter =
        "silenceremove=start_periods=1:start_duration=0.03:start_threshold=-45dB:stop_periods=-1:stop_duration=0.5:stop_threshold=-45dB,volume=8dB";

    private readonly BufferedAudioSttOptions _options = BufferedAudioSttPathResolver.Resolve(options);

    public string Name => "whisper-server-buffered-audio";

    public bool CanHandle(TurnContext turn)
    {
        var frames = ReadBufferedAudioFrames(turn);
        var audioBearingPageCount = BufferedAudioPageClassifier.CountAudioBearingPages(frames);

        return _options.EnableWhisperServer &&
               !string.IsNullOrWhiteSpace(_options.WhisperServerUrl) &&
               IsConfiguredPathAvailable(_options.FfmpegPath, false) &&
               frames.Any(ContainsOpusIdentificationHeader) &&
               audioBearingPageCount > 0 &&
               !IsBelowNoiseFloor(turn, ReadBufferedAudioBytes(turn));
    }

    public async Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var frames = ReadBufferedAudioFrames(turn);
        var pageCounts = BufferedAudioPageClassifier.Describe(frames);
        logger.LogDebug(
            "Whisper-server STT transcription start turnId={TurnId} bufferedBytes={BufferedBytes} serverUrl={ServerUrl}",
            turn.TurnId,
            ReadBufferedAudioBytes(turn),
            _options.WhisperServerUrl);

        if (frames.Count == 0)
            throw new InvalidOperationException("Whisper-server STT requires buffered websocket audio frames.");

        if (!frames.Any(ContainsOpusIdentificationHeader))
            throw new InvalidOperationException(
                "Whisper-server STT requires buffered Ogg/Opus audio with an Opus identification header.");

        var tempDirectory = string.IsNullOrWhiteSpace(_options.TempDirectory)
            ? Path.Combine(Path.GetTempPath(), "openjibo-stt")
            : _options.TempDirectory!;
        Directory.CreateDirectory(tempDirectory);

        var baseName = $"turn-{turn.TurnId}";
        var oggPath = Path.Combine(tempDirectory, $"{baseName}.ogg");
        var wavPath = Path.Combine(tempDirectory, $"{baseName}.wav");

        try
        {
            var normalizedOgg = OggOpusAudioNormalizer.Normalize(frames);
            await File.WriteAllBytesAsync(oggPath, normalizedOgg, cancellationToken);

            var ffmpegResult = await processRunner.RunAsync(
                _options.FfmpegPath!,
                [
                    "-y", "-i", oggPath,
                    "-af", FfmpegAudioPreprocessFilter,
                    "-ar", "16000",
                    "-ac", "1",
                    "-f", "wav",
                    wavPath
                ],
                cancellationToken);

            var wavBytes = File.Exists(wavPath) ? new FileInfo(wavPath).Length : 0;
            if (wavBytes < MinimumTranscribableWavBytes)
            {
                if (!_options.CleanupTempFiles)
                {
                    TryDelete(oggPath);
                    TryDelete(wavPath);
                }

                return BuildResult(string.Empty, turn, wavPath, ffmpegResult, string.Empty, pageCounts);
            }

            var rawResponse = await PostInferenceAsync(wavPath, null, cancellationToken);

            // Auto-detect sometimes hears short English as Korean, Spanish, Polish...
            // If the detected language isn't one this household speaks, read it again as English.
            var detected = ExtractDetectedLanguage(rawResponse);
            if (IsAutoLanguage && detected is not null && !IsAllowedLanguage(detected))
            {
                logger.LogInformation(
                    "Whisper detected {Language}, not in OPENJIBO_STT_WHISPER_ALLOWED_LANGUAGES; re-reading as English.",
                    detected);
                rawResponse = await PostInferenceAsync(wavPath, "en", cancellationToken);
            }
            var transcript = AudioTranscriptNormalizer.NormalizeLooseTranscript(ExtractTranscript(rawResponse));

            if (TranscriptHeuristics.IsLikelyRobotSelfAudioTranscript(transcript))
            {
                var embeddedWakePhraseCommand = TranscriptHeuristics.ExtractWakePhraseCommand(transcript);
                if (!string.IsNullOrWhiteSpace(embeddedWakePhraseCommand) &&
                    !string.Equals(embeddedWakePhraseCommand, transcript, StringComparison.Ordinal) &&
                    !TranscriptHeuristics.IsLikelyRobotSelfAudioTranscript(embeddedWakePhraseCommand))
                {
                    transcript = embeddedWakePhraseCommand;
                }
                else
                {
                    var transcriptHint = AudioTranscriptNormalizer.NormalizeLooseTranscript(ReadTranscriptHint(turn));
                    transcript = !string.IsNullOrWhiteSpace(transcriptHint) &&
                                 !TranscriptHeuristics.IsLikelyRobotSelfAudioTranscript(transcriptHint)
                        ? transcriptHint
                        : string.Empty;
                }
            }

            if (string.IsNullOrWhiteSpace(transcript))
                transcript = AudioTranscriptNormalizer.NormalizeLooseTranscript(ReadTranscriptHint(turn));

            if (string.IsNullOrWhiteSpace(transcript) && !_options.CleanupTempFiles)
            {
                TryDelete(oggPath);
                TryDelete(wavPath);
            }

            return BuildResult(transcript, turn, wavPath, ffmpegResult, rawResponse, pageCounts);
        }
        finally
        {
            if (_options.CleanupTempFiles)
            {
                TryDelete(oggPath);
                TryDelete(wavPath);
            }
        }
    }

    private bool IsAutoLanguage =>
        string.Equals(_options.WhisperLanguage?.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    // whisper-server reports full names ("english", "greek"); the setting uses codes ("en,el").
    private static readonly Dictionary<string, string> LanguageNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "english", ["el"] = "greek", ["fr"] = "french", ["es"] = "spanish", ["de"] = "german",
        ["it"] = "italian", ["pt"] = "portuguese", ["nl"] = "dutch", ["pl"] = "polish", ["ru"] = "russian",
        ["tr"] = "turkish", ["ar"] = "arabic", ["zh"] = "chinese", ["ja"] = "japanese", ["ko"] = "korean",
        ["uk"] = "ukrainian", ["ro"] = "romanian", ["bg"] = "bulgarian", ["sq"] = "albanian", ["hi"] = "hindi"
    };

    /// <summary>
    /// OPENJIBO_STT_WHISPER_ALLOWED_LANGUAGES (default "en,el"): languages kept as detected when
    /// the language is "auto". Anything else is re-read as English. Set "any" to keep all.
    /// </summary>
    internal static bool IsAllowedLanguage(string detected, string? allowedSetting = null)
    {
        var setting = allowedSetting ??
                      Environment.GetEnvironmentVariable("OPENJIBO_STT_WHISPER_ALLOWED_LANGUAGES") ?? "en,el";
        if (string.Equals(setting.Trim(), "any", StringComparison.OrdinalIgnoreCase)) return true;

        var value = detected.Trim();
        foreach (var code in setting.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(code, value, StringComparison.OrdinalIgnoreCase)) return true;
            if (LanguageNames.TryGetValue(code, out var name) &&
                string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    internal static string? ExtractDetectedLanguage(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return null;
        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("language", out var language) &&
                   language.ValueKind == JsonValueKind.String
                ? language.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string> PostInferenceAsync(string wavPath, string? languageOverride, CancellationToken cancellationToken)
    {
        var baseUrl = _options.WhisperServerUrl!.TrimEnd('/');
        var endpoint = $"{baseUrl}/inference";
        await using var fileStream = File.OpenRead(wavPath);
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", Path.GetFileName(wavPath));
        content.Add(new StringContent("0.0"), "temperature");
        content.Add(new StringContent("0.2"), "temperature_inc");
        // verbose_json also reports the detected language, used for the auto-detect fallback.
        content.Add(new StringContent(IsAutoLanguage ? "verbose_json" : "json"), "response_format");
        content.Add(new StringContent(languageOverride ?? _options.WhisperLanguage), "language");
        if (_options.WhisperTranslate)
            content.Add(new StringContent("true"), "translate");
        if (_options.WhisperAudioContext > 0)
            content.Add(new StringContent(_options.WhisperAudioContext.ToString()), "audio_ctx");

        using var response = await httpClient.PostAsync(endpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Whisper-server inference failed with {(int)response.StatusCode}: {body}");

        return body;
    }

    private static string ExtractTranscript(string rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(rawResponse);
            if (document.RootElement.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? string.Empty;

            if (document.RootElement.TryGetProperty("transcription", out var transcription) &&
                transcription.ValueKind == JsonValueKind.String)
                return transcription.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Fall through to plain-text parsing.
        }

        var lines = rawResponse
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var timecoded = lines
            .Where(static line => line.StartsWith('[') && line.Contains("-->", StringComparison.Ordinal))
            .Select(static line =>
            {
                var closingBracket = line.IndexOf(']');
                return closingBracket >= 0 ? line[(closingBracket + 1)..].Trim() : line.Trim();
            })
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return timecoded.Length > 0 ? string.Join(" ", timecoded).Trim() : rawResponse.Trim();
    }

    private SttResult BuildResult(
        string transcript,
        TurnContext turn,
        string wavPath,
        ExternalProcessResult ffmpegResult,
        string rawResponse,
        BufferedAudioPageCounts pageCounts)
    {
        return new SttResult
        {
            Text = transcript,
            Provider = Name,
            Locale = turn.Locale,
            Metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["bufferedAudioBytes"] = ReadBufferedAudioBytes(turn),
                ["bufferedAudioChunks"] = pageCounts.RawFrameCount,
                ["bufferedAudioRawFrames"] = pageCounts.RawFrameCount,
                ["bufferedAudioMetadataPages"] = pageCounts.MetadataPageCount,
                ["bufferedAudioAudioBearingPages"] = pageCounts.AudioBearingPageCount,
                ["ffmpegPath"] = _options.FfmpegPath,
                ["whisperServerUrl"] = _options.WhisperServerUrl,
                ["wavPath"] = wavPath,
                ["ffmpegAudioFilter"] = FfmpegAudioPreprocessFilter,
                ["ffmpegStdOut"] = ffmpegResult.StdOut,
                ["ffmpegStdErr"] = ffmpegResult.StdErr,
                ["whisperServerResponse"] = rawResponse
            }
        };
    }

    private static string? ReadTranscriptHint(TurnContext turn)
    {
        return turn.Attributes.TryGetValue("audioTranscriptHint", out var transcriptHint)
            ? transcriptHint?.ToString()
            : null;
    }

    private static IReadOnlyList<byte[]> ReadBufferedAudioFrames(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("bufferedAudioFrames", out var value) || value is null) return [];

        return value switch
        {
            byte[][] jagged => jagged,
            IReadOnlyList<byte[]> typed => typed,
            IEnumerable<byte[]> enumerable => enumerable.ToArray(),
            JsonElement { ValueKind: JsonValueKind.Array } jsonElement => jsonElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.Array)
                .Select(static item => item.EnumerateArray().Select(static b => (byte)b.GetInt32()).ToArray())
                .ToArray(),
            _ => []
        };
    }

    private static int ReadBufferedAudioBytes(TurnContext turn)
    {
        return turn.Attributes.TryGetValue("bufferedAudioBytes", out var bufferedAudioBytes) &&
               bufferedAudioBytes is not null
            ? bufferedAudioBytes switch
            {
                int value => value,
                long value => (int)value,
                string value when int.TryParse(value, out var parsed) => parsed,
                _ => 0
            }
            : 0;
    }

    private static bool IsBelowNoiseFloor(TurnContext turn, int bufferedAudioBytes)
    {
        if (bufferedAudioBytes <= 0) return false;

        var minimumBufferedAudioBytes = IsShortAnswerTurn(turn)
            ? ShortAnswerBufferedAudioBytes
            : MinimumBufferedAudioBytes;

        return bufferedAudioBytes < minimumBufferedAudioBytes;
    }

    private static bool IsShortAnswerTurn(TurnContext turn)
    {
        var rules = ReadRules(turn, "listenRules")
            .Concat(ReadRules(turn, "clientRules"))
            .Concat(ReadRules(turn, "listenAsrHints"));

        return rules.Any(IsShortAnswerRule);
    }

    private static bool IsShortAnswerRule(string rule)
    {
        return string.Equals(rule, "$YESNO", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "clock/alarm_timer_change", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "clock/alarm_timer_none_set", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "create/is_it_a_keeper", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "settings/download_now_later", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "shared/yes_no", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-date/offer_date_fact", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "surprises-ota/want_to_download_now", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "word-of-the-day/surprise", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "word-of-the-day/right_word", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(rule, "word-of-the-day/puzzle", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadRules(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null) return [];

        return value switch
        {
            IReadOnlyList<string> typed => typed,
            IEnumerable<string> enumerable => enumerable,
            JsonElement { ValueKind: JsonValueKind.Array } jsonElement => jsonElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty),
            _ => []
        };
    }

    private static bool ContainsOpusIdentificationHeader(byte[] frame)
    {
        return frame.AsSpan().IndexOf("OpusHead"u8) >= 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static bool IsConfiguredPathAvailable(string? path, bool checkFileExists)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        if (!Path.IsPathRooted(path)) return true;

        return !checkFileExists || File.Exists(path);
    }
}
