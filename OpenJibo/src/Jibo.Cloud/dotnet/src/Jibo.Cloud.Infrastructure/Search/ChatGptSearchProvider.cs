using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Search;

public sealed class ChatGptSearchProvider(
    HttpClient httpClient,
    SearchBackendOptions options,
    ILogger<ChatGptSearchProvider> logger)
    : IKnowledgeSearchProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ConcurrentDictionary<string, CacheEntry> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Short-term conversation memory so follow-ups ("how old is he?") work.
    // The provider is a singleton and Jibo is one robot, so one rolling history is enough.
    private const int MaxHistoryTurns = 6;
    private static readonly TimeSpan HistoryIdleTimeout = TimeSpan.FromMinutes(5);
    private readonly object _historyLock = new();
    private readonly List<ConversationTurn> _history = [];

    public SearchBackendKind Kind => SearchBackendKind.ChatGPT;

    /// <summary>
    /// Forces the OpenAI-compatible chat-completions path even for a Claude endpoint
    /// (no skills tool, mood or web search). Used by tests so a local .env can't change the path.
    /// </summary>
    internal bool UseChatCompletionsOnly { get; init; }

    public async Task<KnowledgeSearchResult?> SearchAsync(
        KnowledgeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.BackendSpec.Kind != SearchBackendKind.ChatGPT ||
            string.IsNullOrWhiteSpace(request.BackendSpec.Credential) ||
            string.IsNullOrWhiteSpace(request.Query))
            return null;

        var endpoint = SearchBackendSettingsResolver.ResolveEndpoint(request.BackendSpec);
        if (string.IsNullOrWhiteSpace(endpoint)) return null;

        var model = SearchBackendSettingsResolver.ResolveModel(request.BackendSpec);
        var query = request.Query.Trim();

        // "Learn my face, I'm Makis" / "forget my face, I'm Makis": local face recogniser.
        // Also the two-step form: "learn my face" -> "What's your name?" -> "Makis".
        var faceCommand = ParseFaceCommand(query) ?? TakePendingFaceName(query);
        if (faceCommand is not null)
            return await HandleFaceCommandAsync(faceCommand, cancellationToken);

        var cacheKey = BuildCacheKey(request.BackendSpec, model, query);
        var visionPhoto = IsVisionQuestion(query)
            ? await TakeLiveCameraPhotoAsync(cancellationToken) ?? await WaitForFreshVisionPhotoAsync(cancellationToken)
            : null;
        var attachVision = visionPhoto is not null;
        var faces = visionPhoto is null ? null : await IdentifyFacesAsync(visionPhoto, cancellationToken);
        if (visionPhoto is not null) JiboDailyStats.RecordPhoto(faces?.Names.Count ?? 0);
        var history = GetActiveHistory();

        // Claude-first brain (Claude only): one native Messages API call that can answer with
        // a mood, ask Jibo to run a built-in skill (jibo_command tool), or search the web.
        var isClaude = !UseChatCompletionsOnly && !attachVision && IsAnthropicEndpoint(endpoint);
        var useBrain = isClaude && IsClaudeBrainEnabled();

        // Live web search: only for current-events questions (one search fee per question).
        var useWebSearch = isClaude && IsWebSearchEnabled() && NeedsWebSearch(query);

        // A cached answer is only safe when it cannot depend on a photo, on earlier turns,
        // on live web results, or on the current time/date.
        var useCache = !useWebSearch && !attachVision && history.Count == 0 && !IsTimeSensitive(query) &&
                       RobotPerception.Current is null;
        if (useCache && TryGetCachedValue(cacheKey, out var cachedResult))
        {
            RememberResult(query, cachedResult);
            CountAiForStats(cachedResult);
            return cachedResult;
        }

        if (useBrain || useWebSearch)
        {
            var claudeResult = await TryClaudeMessagesAsync(
                request.BackendSpec.Credential,
                model,
                query,
                history,
                includeCommandTool: useBrain,
                includeWebSearch: useWebSearch,
                cancellationToken);
            if (claudeResult is not null)
            {
                RememberResult(query, claudeResult);
                if (useCache && string.IsNullOrEmpty(claudeResult.CommandPhrase))
                    SetCachedValue(cacheKey, claudeResult, options.CacheTtlSeconds);
                CountAiForStats(claudeResult);
                return claudeResult;
            }

            // Claude call failed or was refused: fall through to the plain answer path.
        }

        var result = await TryCompleteAsync(
            endpoint,
            request.BackendSpec.Credential,
            model,
            query,
            visionPhoto,
            faces,
            history,
            cancellationToken);

        CountAiForStats(result);

        // Camera "recording light": glow the base light ring while Jibo describes the photo.
        if (attachVision && result.Outcome == KnowledgeSearchOutcome.Found && IsLightRingOnPhotoEnabled())
            result = result with { Animation = result.Animation ?? JiboAnimationCatalog.LightRing };

        RememberResult(query, result);

        if (useCache)
        {
            SetCachedValue(
                cacheKey,
                result,
                result.Outcome == KnowledgeSearchOutcome.Found
                    ? options.CacheTtlSeconds
                    : options.FailureCacheTtlSeconds);
        }

        return result;
    }

    private async Task<KnowledgeSearchResult> TryCompleteAsync(
        string endpoint,
        string apiKey,
        string model,
        string prompt,
        string? visionPhotoPath,
        FaceIdentification? faces,
        IReadOnlyList<ConversationTurn> history,
        CancellationToken cancellationToken)
    {
        try
        {
            var (systemMessage, userMessage) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(
                prompt,
                options.LlmInstructions);

            userMessage += BuildRobotPerceptionNote(RobotPerception.Current);
            object userContent = userMessage;
            if (visionPhotoPath is not null && System.IO.File.Exists(visionPhotoPath))
            {
                try
                {
                    var imageBytes = await System.IO.File.ReadAllBytesAsync(visionPhotoPath, cancellationToken);
                    var imageBase64 = Convert.ToBase64String(imageBytes);
                    var mediaType = DetectImageMediaType(imageBytes) ?? "image/jpeg";
                    userContent = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = userMessage + "\n\n(The attached photo was just taken with your own camera. " +
                                   "Describe what you see as Jibo, in one to three short spoken sentences.)" +
                                   BuildFaceNote(faces)
                        },
                        new { type = "image_url", image_url = new { url = $"data:{mediaType};base64,{imageBase64}" } }
                    };
                    logger.LogInformation(
                        "Attached vision photo {Path} to ChatGPT/Claude request ({Bytes} bytes).",
                        visionPhotoPath,
                        imageBytes.Length);
                }
                catch (Exception imageEx)
                {
                    logger.LogWarning(imageEx, "Failed to attach vision photo, falling back to text-only.");
                }
            }

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Content = JsonContent.Create(
                new ChatCompletionRequest(
                    model,
                    BuildMessages(systemMessage, history, userContent)));

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("ChatGPT lookup failed with status {StatusCode}.", response.StatusCode);
                return KnowledgeSearchResult.Unavailable(SearchBackendKind.ChatGPT);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
            var answerText = KnowledgeSearchResponseFormatter.NormalizeForSpeech(
                parsed?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty);
            return string.IsNullOrWhiteSpace(answerText)
                ? KnowledgeSearchResult.NotFound(SearchBackendKind.ChatGPT)
                : new KnowledgeSearchResult(answerText, SearchBackendKind.ChatGPT);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "ChatGPT lookup failed for model {Model}.", model);
            return KnowledgeSearchResult.Unavailable(SearchBackendKind.ChatGPT);
        }
    }

    private static readonly string[] VisionTriggerPhrases =
    [
        "what do you see", "what can you see", "do you see", "can you see", "look at", "looking at",
        "what am i wearing", "am i wearing", "what is this", "what's this", "in front of you",
        "what am i holding", "describe me", "describe the photo", "describe the picture",
        "how do i look", "what color is", "who is this", "read this", "in the photo", "in the picture",
        "who am i", "do you know me", "do you know who i am", "do you recognize me", "do you recognise me",
        "who's this", "who is that", "who's that", "who is here"
    ];

    // A photo counts as "what Jibo sees right now" for a few minutes after it was taken
    // (OPENJIBO_VISION_PHOTO_MAX_AGE_SECONDS, default 300).
    private static TimeSpan VisionPhotoMaxAge =>
        TimeSpan.FromSeconds(ReadPositiveIntSetting("OPENJIBO_VISION_PHOTO_MAX_AGE_SECONDS", 300));

    // When asked "what do you see" right after "take a picture", the photo may still be on its
    // way from the robot: wait for it a little (OPENJIBO_VISION_PHOTO_WAIT_SECONDS, default 10).
    private static TimeSpan VisionPhotoWait =>
        TimeSpan.FromSeconds(ReadPositiveIntSetting("OPENJIBO_VISION_PHOTO_WAIT_SECONDS", 10, allowZero: true));

    // Jibo's on-robot media service (jibo-lps-service, port 8486) takes a photo on request:
    // POST /media/photo {"camera":1,"type":2,"filters":[]} -> {"id":...}, then
    // GET /media/photo?id=... returns the JPEG. Set OPENJIBO_JIBO_CAMERA_URL to
    // http://<jibo-ip>:8486 to use it; "what do you see" then needs no "take a picture".
    internal static Uri? LiveCameraBaseUri()
    {
        var raw = Environment.GetEnvironmentVariable("OPENJIBO_JIBO_CAMERA_URL")?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(raw) &&
               Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    internal static string? ReadPhotoId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("id", out var id) &&
                   id.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(id.GetString())
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> TakeLiveCameraPhotoAsync(CancellationToken cancellationToken)
    {
        var baseUri = LiveCameraBaseUri();
        if (baseUri is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var takeRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/media/photo"))
            {
                Content = new StringContent(
                    """{"camera":1,"type":2,"filters":[]}""",
                    Encoding.UTF8,
                    "application/json")
            };
            using var takeResponse = await httpClient.SendAsync(takeRequest, timeout.Token);
            if (!takeResponse.IsSuccessStatusCode)
            {
                logger.LogWarning("Jibo camera did not take a photo (HTTP {Status}).", (int)takeResponse.StatusCode);
                return null;
            }

            var photoId = ReadPhotoId(await takeResponse.Content.ReadAsStringAsync(timeout.Token));
            if (photoId is null) return null;

            var imageBytes = await httpClient.GetByteArrayAsync(
                new Uri(baseUri, "/media/photo?id=" + Uri.EscapeDataString(photoId)),
                timeout.Token);
            if (DetectImageMediaType(imageBytes) is null) return null;

            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openjibo-vision-live.jpg");
            await System.IO.File.WriteAllBytesAsync(path, imageBytes, timeout.Token);
            logger.LogInformation("Took a live photo with Jibo's camera ({Bytes} bytes).", imageBytes.Length);
            return path;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Jibo camera photo failed; falling back to the latest stored photo.");
            return null;
        }
    }

    private async Task<string?> WaitForFreshVisionPhotoAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + VisionPhotoWait;
        while (true)
        {
            var photo = FindFreshVisionPhoto(VisionPhotoRoots(), DateTime.UtcNow, VisionPhotoMaxAge);
            if (photo is not null || DateTime.UtcNow >= deadline) return photo;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static int ReadPositiveIntSetting(string name, int fallback, bool allowZero = false) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) &&
        (value > 0 || (allowZero && value == 0))
            ? value
            : fallback;

    internal static bool IsVisionQuestion(string query) =>
        !string.IsNullOrWhiteSpace(query) &&
        VisionTriggerPhrases.Any(t => query.Contains(t, StringComparison.OrdinalIgnoreCase));

    // Where fresh photos can be found: the hand-copied file, and the media store where the
    // robot uploads every picture it takes ("Hey Jibo, take a picture").
    private static IEnumerable<string> VisionPhotoRoots()
    {
        yield return Environment.GetEnvironmentVariable("OPENJIBO_VISION_PHOTO_PATH") is { Length: > 0 } custom
            ? custom
            : "/data/vision-latest.jpg";
        yield return Environment.GetEnvironmentVariable("OpenJibo__Media__DirectoryPath") is { Length: > 0 } media
            ? media
            : "/data/media";
    }

    /// <summary>
    /// Newest JPEG/PNG among the given files and folders (searched recursively) that is no older
    /// than <paramref name="maxAge"/>; files are checked by content, since the media store saves
    /// uploads as .bin. Returns null when there is no fresh photo.
    /// </summary>
    internal static string? FindFreshVisionPhoto(IEnumerable<string> roots, DateTime utcNow, TimeSpan maxAge)
    {
        var candidates = new List<System.IO.FileInfo>();
        foreach (var root in roots)
        {
            try
            {
                if (System.IO.File.Exists(root))
                    candidates.Add(new System.IO.FileInfo(root));
                else if (System.IO.Directory.Exists(root))
                    candidates.AddRange(new System.IO.DirectoryInfo(root)
                        .EnumerateFiles("*", System.IO.SearchOption.AllDirectories)
                        .Where(f => !f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception)
            {
                // Unreadable folder: skip it.
            }
        }

        foreach (var file in candidates
                     .Where(f => utcNow - f.LastWriteTimeUtc <= maxAge && f.Length is > 1024 and < 20_000_000)
                     .OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                var header = new byte[8];
                using var stream = file.OpenRead();
                if (stream.Read(header, 0, header.Length) == header.Length && DetectImageMediaType(header) is not null)
                    return file.FullName;
            }
            catch (System.IO.IOException)
            {
                // File still being written or gone: try the next one.
            }
        }

        return null;
    }

    // ---- Local face recogniser (OpenJibo/faces, OPENJIBO_FACES_URL, default http://faces:8487) ----

    internal sealed record FaceCommand(bool Forget, string Name);

    internal sealed record FaceIdentification(int Faces, IReadOnlyList<string> Names);

    private const string NameWord = @"[a-z][a-z'\-]{1,20}";
    private const string IntroWords = @"(?:i\s*am|i'm|im|my\s+name\s+is|this\s+is|it's|it\s+is|call\s+me)";
    private const string FaceObject = @"(?:my\s+face|me|what\s+i\s+look\s+like)";

    private static readonly Regex LearnThenName = new(
        @"\b(?<verb>learn|remember|memori[sz]e|forget)\s+" + FaceObject + @"[\s,.!-]+(?:please[\s,.!-]+)?" + IntroWords +
        @"\s+(?<name>" + NameWord + @"(?:\s+" + NameWord + @")?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex NameThenLearn = new(
        @"\b" + IntroWords + @"\s+(?<name>" + NameWord + @")[\s,.!-]+(?:please[\s,.!-]+)?(?:jibo[\s,.!-]+)?" +
        @"(?<verb>learn|remember|memori[sz]e|forget)\s+" + FaceObject + @"\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // "learn Makis face", "learn Makis's face", "learn the face of Makis".
    private static readonly Regex LearnPossessive = new(
        @"\b(?<verb>learn|remember|memori[sz]e|forget)\s+(?:the\s+face\s+of\s+(?<name>" + NameWord +
        @")|(?!my\b)(?<name>[a-z][a-z\-]{1,20})(?:'s|')?\s+face)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LearnWithoutName = new(
        @"\b(?:learn|remember|memori[sz]e)\s+my\s+face\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "me", "my", "you", "your", "jibo", "please", "the", "a", "and", "here", "it", "this", "that"
    };

    /// <summary>"Learn my face, I'm Makis", "I'm Mary, remember my face", "learn Nick's face", "forget my face, I'm Makis".</summary>
    internal static FaceCommand? ParseFaceCommand(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var match = LearnThenName.Match(query);
        if (!match.Success) match = NameThenLearn.Match(query);
        if (!match.Success) match = LearnPossessive.Match(query);
        if (!match.Success)
            return LearnWithoutName.IsMatch(query) ? new FaceCommand(false, string.Empty) : null;

        var words = match.Groups["name"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(word => !NotNames.Contains(word))
            .ToArray();
        if (words.Length == 0) return null;

        var name = string.Join(' ', words.Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()));
        return new FaceCommand(
            match.Groups["verb"].Value.Equals("forget", StringComparison.OrdinalIgnoreCase),
            name);
    }

    // After "learn my face" without a name, the next short answer ("Makis", "I'm Makis") is the name.
    private DateTime _pendingFaceNameUntil = DateTime.MinValue;

    private static readonly Regex NameAnswer = new(
        @"^(?:(?:my\s+name\s+is|i\s*am|i'm|im|it's|it\s+is|this\s+is|call\s+me)\s+)?(?<name>" + NameWord +
        @"(?:\s+" + NameWord + @")?)[\s.!]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private FaceCommand? TakePendingFaceName(string query)
    {
        if (DateTime.UtcNow > _pendingFaceNameUntil) return null;
        _pendingFaceNameUntil = DateTime.MinValue;
        return ParseNameAnswer(query);
    }

    /// <summary>A bare name answer: "Makis", "I'm Makis", "my name is Mary Smith".</summary>
    internal static FaceCommand? ParseNameAnswer(string query)
    {
        var match = NameAnswer.Match(query?.Trim() ?? string.Empty);
        if (!match.Success) return null;
        var words = match.Groups["name"].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeWhile(word => !NotNames.Contains(word))
            .ToArray();
        return words.Length == 0
            ? null
            : new FaceCommand(false, string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant())));
    }

    /// <summary>
    /// What Jibo's own on-robot recognition says (speaker / people present). Used first for names;
    /// the server-side face memory on a fresh photo is the tie-breaker when they disagree.
    /// </summary>
    // Daily stats: count answers that really came from the AI.
    private static void CountAiForStats(KnowledgeSearchResult result)
    {
        if (result.Outcome != KnowledgeSearchOutcome.Found) return;
        if (!string.IsNullOrWhiteSpace(result.CommandPhrase)) JiboDailyStats.RecordAiSkill();
        else if (!string.IsNullOrWhiteSpace(result.AnswerText)) JiboDailyStats.RecordAiAnswer();
    }

    internal static string BuildRobotPerceptionNote(RobotPerceptionSnapshot? perception)
    {
        if (perception is null || perception.IsEmpty) return string.Empty;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(perception.SpeakerName))
            parts.Add($"the person talking to you is {perception.SpeakerName}");
        var others = perception.PeoplePresentNames
            .Where(name => !string.Equals(name, perception.SpeakerName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (others.Count > 0)
            parts.Add("also nearby: " + string.Join(", ", others));
        return "\n\n(Your own face and voice recognition says " + string.Join("; ", parts) +
               ". Use their name only when it fits naturally. If a photo's face recognition below names " +
               "someone else, trust the photo.)";
    }

    internal static string BuildFaceNote(FaceIdentification? faces)
    {
        if (faces is null) return string.Empty;
        if (faces.Names.Count > 0)
            return "\n(Face recognition on your own camera recognised: " + string.Join(", ", faces.Names) +
                   ". Use their names naturally, for example to greet them. Never guess names for anyone else.)";
        return faces.Faces > 0
            ? "\n(Face recognition did not recognise anyone you know in this photo. Do not guess names. If asked " +
              "who they are, say you don't know them yet and that they can say: learn my face, I'm and their name.)"
            : string.Empty;
    }

    private static Uri? FacesBaseUri()
    {
        var raw = Environment.GetEnvironmentVariable("OPENJIBO_FACES_URL")?.Trim().TrimEnd('/');
        return !string.IsNullOrEmpty(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) ? uri : null;
    }

    private async Task<FaceIdentification?> IdentifyFacesAsync(string photoPath, CancellationToken cancellationToken)
    {
        var baseUri = FacesBaseUri();
        if (baseUri is null) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(photoPath, timeout.Token);
            using var content = new ByteArrayContent(bytes);
            using var response = await httpClient.PostAsync(new Uri(baseUri, "/identify"), content, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var faceCount = document.RootElement.TryGetProperty("faces", out var f) && f.TryGetInt32(out var n) ? n : 0;
            var names = document.RootElement.TryGetProperty("names", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(e => e.GetString()).OfType<string>().ToList()
                : new List<string>();
            logger.LogInformation("Face recognition: {Faces} face(s), recognised {Names}.", faceCount,
                names.Count == 0 ? "nobody" : string.Join(", ", names));
            return new FaceIdentification(faceCount, names);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Face recognition service not reachable.");
            return null;
        }
    }

    private async Task<KnowledgeSearchResult> HandleFaceCommandAsync(FaceCommand command, CancellationToken cancellationToken)
    {
        string? ring = null;
        KnowledgeSearchResult Say(string text, string mood = "neutral") =>
            new(text, SearchBackendKind.ChatGPT, KnowledgeSearchOutcome.Found, mood, Animation: ring);

        if (string.IsNullOrEmpty(command.Name))
        {
            _pendingFaceNameUntil = DateTime.UtcNow.AddSeconds(45);
            return Say("Sure! Who are you? Say it like this: I'm Makis.");
        }

        var baseUri = FacesBaseUri();
        if (baseUri is null) return Say("My face memory isn't set up on this server yet.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            if (command.Forget)
            {
                using var forget = await httpClient.DeleteAsync(
                    new Uri(baseUri, "/people?name=" + Uri.EscapeDataString(command.Name)), timeout.Token);
                using var forgetDoc = JsonDocument.Parse(await forget.Content.ReadAsStringAsync(timeout.Token));
                var forgotten = forgetDoc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
                logger.LogInformation("Face memory: forget {Name} -> {Result}.", command.Name, forgotten);
                return Say(forgotten
                    ? $"Okay, I've forgotten {command.Name}'s face."
                    : $"I don't have a face saved for {command.Name}.");
            }

            var photoPath = await TakeLiveCameraPhotoAsync(timeout.Token);
            if (photoPath is null)
                return Say("I couldn't use my camera just now. Try again in a moment.");
            if (IsLightRingOnPhotoEnabled()) ring = JiboAnimationCatalog.LightRing;

            var bytes = await System.IO.File.ReadAllBytesAsync(photoPath, timeout.Token);
            using var content = new ByteArrayContent(bytes);
            using var response = await httpClient.PostAsync(
                new Uri(baseUri, "/enroll?name=" + Uri.EscapeDataString(command.Name)), content, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var root = document.RootElement;
            var enrolled = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var samples = root.TryGetProperty("samples", out var s) && s.TryGetInt32(out var count) ? count : 0;
            logger.LogInformation("Face memory: learn {Name} -> ok={Ok} reason={Reason} samples={Samples}.",
                command.Name, enrolled, reason ?? "-", samples);

            if (enrolled)
                return Say(samples < 3
                    ? $"Nice to see you, {command.Name}! I'll remember your face. Say it again once or twice from a slightly different angle so I really learn it."
                    : $"Got it, {command.Name}. I know your face really well now.", "happy");
            return Say(reason switch
            {
                "several_faces" => "I can see more than one face. Please have just one person in front of me and try again.",
                _ => "I couldn't see a face clearly. Look right at me, a little closer, and try again."
            });
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Face memory request failed.");
            return Say("My face memory isn't answering right now. Try again in a moment.");
        }
    }

    internal static string? DetectImageMediaType(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "image/jpeg";
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        return null;
    }

    private const string AnthropicMessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicApiVersion = "2023-06-01";
    private const string WebSearchToolType = "web_search_20250305";

    private const string WebSearchInstructions =
        "You can search the web. Use it for anything current or recent, then answer in one to three " +
        "short spoken sentences. Never read out website names, links or citations.";

    private const string BrainInstructions =
        "Start every spoken answer with exactly one mood tag, [happy], [neutral] or [sad], matching how " +
        "Jibo should sound, for example: [happy] Here is a fun one for you. " +
        "Only when the user asks Jibo to DO something it has a built-in skill for (set a timer, dance, " +
        "check the weather, play the radio and so on), call the jibo_command tool instead of answering, " +
        "in any language the user speaks. " +
        "Answer everything else yourself: general questions, conversation, facts about the world, and " +
        "questions about you, Jibo, such as what you like, your favourites, your feelings and opinions. " +
        "Questions about the time in another city or country are general questions: answer them yourself. " +
        "Requests like flipping a coin, picking a number or saying happy birthday: answer them yourself " +
        "(with a fitting animation). When asked what you see or how something looks and no photo is " +
        "attached, you cannot see right now: say to tell you \"take a picture\" first and then ask again. " +
        "If the words make no sense, they were probably misheard: say briefly " +
        "that you didn't catch that and ask the user to say it again.";

    private static readonly string AnimationInstructions =
        "Jibo can show an animation on its screen while it speaks. When one clearly fits, add exactly one " +
        "tag [anim:NAME] at the end of your answer, using only these names: " +
        JiboAnimationCatalog.BuildPromptMenu() +
        " For example: [happy] Pizza is my favourite too! [anim:pizza]. Leave it out when nothing fits.";

    private const string JiboCommandToolName = "jibo_command";

    private const string JiboCommandToolDescription =
        "Run one of Jibo's built-in skills. Use it when the user wants Jibo to do something it has a skill " +
        "for, or asks about Jibo itself, in any language or wording. Skills: timers and alarms (set, cancel, " +
        "check), the local time and date, weather at home (answer weather in other places yourself), news, calendar, commute, volume (up, down, or a number), " +
        "stop, go to sleep, wake up, dance, turn around, take a picture, show the photo gallery, play the " +
        "radio (optionally a genre), roll dice, spell a word, define a word, word of the day, fun fact, tell " +
        "a joke, tell a story, surprise me, shopping and to-do lists, smart-home lights and thermostat. " +
        "Never use it for questions, conversation, facts about the world, questions about Jibo's likes, " +
        "favourites or feelings, or to show an animation (use an [anim:NAME] tag for that).";

    private async Task<KnowledgeSearchResult?> TryClaudeMessagesAsync(
        string apiKey,
        string model,
        string query,
        IReadOnlyList<ConversationTurn> history,
        bool includeCommandTool,
        bool includeWebSearch,
        CancellationToken cancellationToken)
    {
        try
        {
            var (systemMessage, _) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(
                query,
                options.LlmInstructions);
            if (includeCommandTool)
                systemMessage += "\n\n" + BrainInstructions + "\n\n" + AnimationInstructions;
            if (includeWebSearch)
                systemMessage += "\n\n" + WebSearchInstructions;

            var body = BuildClaudeRequestBody(
                model,
                systemMessage,
                history.Select(turn => (turn.User, turn.Assistant)).ToList(),
                query,
                includeCommandTool,
                includeWebSearch,
                ResolveWebSearchMaxUses(),
                ResolveWebSearchUserLocation());

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, AnthropicMessagesEndpoint);
            httpRequest.Headers.Add("x-api-key", apiKey);
            httpRequest.Headers.Add("anthropic-version", AnthropicApiVersion);
            httpRequest.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Claude request failed with status {StatusCode}: {Body}",
                    response.StatusCode,
                    responseBody.Length > 300 ? responseBody[..300] : responseBody);
                return null;
            }

            var reply = ExtractClaudeReply(responseBody);
            if (!string.IsNullOrWhiteSpace(reply.Command))
            {
                logger.LogInformation("Claude brain chose a Jibo skill: {Command}", reply.Command);
                return new KnowledgeSearchResult(
                    string.Empty,
                    SearchBackendKind.ChatGPT,
                    KnowledgeSearchOutcome.Found,
                    CommandPhrase: reply.Command);
            }

            var (mood, moodless) = ParseMood(reply.Answer);
            var (animation, text) = ParseAnimation(moodless);
            var spoken = KnowledgeSearchResponseFormatter.NormalizeForSpeech(text);
            if (string.IsNullOrWhiteSpace(spoken))
                return null;

            logger.LogInformation(
                "Answered with Claude (mood={Mood}, animation={Animation}, searches={Searches}).",
                mood ?? "neutral",
                animation ?? "none",
                reply.Searches);
            return new KnowledgeSearchResult(
                spoken,
                SearchBackendKind.ChatGPT,
                KnowledgeSearchOutcome.Found,
                mood,
                Animation: animation);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Claude request failed; falling back to a plain answer.");
            return null;
        }
    }

    internal static string BuildWebSearchRequestBody(
        string model,
        string systemMessage,
        IReadOnlyList<(string User, string Assistant)> history,
        string query,
        int maxUses,
        JsonObject? userLocation) =>
        BuildClaudeRequestBody(model, systemMessage, history, query, false, true, maxUses, userLocation);

    internal static string BuildClaudeRequestBody(
        string model,
        string systemMessage,
        IReadOnlyList<(string User, string Assistant)> history,
        string query,
        bool includeCommandTool,
        bool includeWebSearch,
        int maxUses,
        JsonObject? userLocation)
    {
        var messages = new JsonArray();
        foreach (var (user, assistant) in history)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = user });
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistant });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = query + BuildRobotPerceptionNote(RobotPerception.Current) });

        var tools = new JsonArray();
        if (includeWebSearch)
        {
            var webSearch = new JsonObject
            {
                ["type"] = WebSearchToolType,
                ["name"] = "web_search",
                ["max_uses"] = maxUses
            };
            if (userLocation is not null)
                webSearch["user_location"] = userLocation;
            tools.Add(webSearch);
        }

        if (includeCommandTool)
        {
            tools.Add(new JsonObject
            {
                ["name"] = JiboCommandToolName,
                ["description"] = JiboCommandToolDescription,
                ["input_schema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["command"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] =
                                "The request as a short, simple English command or question, for example " +
                                "'set a timer for 5 minutes', 'dance', 'take a picture', 'turn the volume up', " +
                                "'what is your favorite color', 'play jazz radio'."
                        }
                    },
                    ["required"] = new JsonArray { "command" }
                }
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 400,
            ["system"] = systemMessage,
            ["messages"] = messages
        };
        if (tools.Count > 0)
            body["tools"] = tools;
        return body.ToJsonString();
    }

    private static readonly Regex MoodTagPattern = new(
        @"\[(happy|neutral|sad)\]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex AnimationTagPattern = new(
        @"\[anim:\s*([a-z0-9\- ]+?)\s*\]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Takes the first [anim:name] tag out of the answer (validated later against the robot's
    /// catalog) and removes every anim tag so none is ever spoken.
    /// </summary>
    internal static (string? Animation, string Text) ParseAnimation(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return (null, string.Empty);

        var first = AnimationTagPattern.Match(answer);
        var animation = first.Success ? first.Groups[1].Value.Trim().ToLowerInvariant() : null;
        return (animation, AnimationTagPattern.Replace(answer, string.Empty).Trim());
    }

    /// <summary>Splits a leading [happy]/[neutral]/[sad] tag off the answer and removes any stray tags.</summary>
    internal static (string? Mood, string Text) ParseMood(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return (null, string.Empty);

        var trimmed = answer.TrimStart();
        string? mood = null;
        var leading = MoodTagPattern.Match(trimmed);
        if (leading.Success && leading.Index == 0)
            mood = leading.Groups[1].Value.ToLowerInvariant();

        return (mood, MoodTagPattern.Replace(trimmed, string.Empty).Trim());
    }

    /// <summary>
    /// Returns the spoken answer from a Messages API response that may contain web search blocks.
    /// Claude often writes a short "let me check" line before searching, so only the text after
    /// the last search result is used (or all text when no search happened).
    /// </summary>
    internal static (string Answer, int Searches) ExtractWebSearchAnswer(string responseJson)
    {
        var reply = ExtractClaudeReply(responseJson);
        return (reply.Answer, reply.Searches);
    }

    /// <summary>
    /// Reads a Messages API response: the spoken text after the last web search result (or all
    /// text when no search happened), the jibo_command tool call if Claude chose one, and the
    /// number of web searches billed.
    /// </summary>
    internal static (string Answer, string? Command, int Searches) ExtractClaudeReply(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        var searches = 0;
        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("server_tool_use", out var serverToolUse) &&
            serverToolUse.TryGetProperty("web_search_requests", out var requests) &&
            requests.ValueKind == JsonValueKind.Number)
            searches = requests.GetInt32();

        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return (string.Empty, null, searches);

        var blocks = content.EnumerateArray().ToList();

        string? command = null;
        foreach (var block in blocks)
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "tool_use" &&
                block.TryGetProperty("name", out var name) && name.GetString() == JiboCommandToolName &&
                block.TryGetProperty("input", out var input) &&
                input.TryGetProperty("command", out var commandValue) &&
                commandValue.ValueKind == JsonValueKind.String)
            {
                command = commandValue.GetString()?.Trim();
                break;
            }
        }

        var lastSearchResult = blocks.FindLastIndex(block =>
            block.TryGetProperty("type", out var type) && type.GetString() == "web_search_tool_result");

        var answer = new StringBuilder();
        for (var i = lastSearchResult + 1; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                block.TryGetProperty("text", out var text))
                answer.Append(text.GetString());
        }

        return (answer.ToString().Trim(), string.IsNullOrWhiteSpace(command) ? null : command, searches);
    }

    private static readonly Regex WebSearchPattern = new(
        @"\b(today|tonight|yesterday|this (week|weekend|month|year)|latest|recent|recently|news|headlines|current|currently|right now|score|scores|won|winner|game|match|playoffs?|election|stocks?|price of|weather|forecast|released?|coming out|upcoming|σήμερα|απόψε|αύριο|χθες|χτες|τώρα|ειδήσεις|νέα|καιρός|καιρό|πρόγνωση|αποτέλεσμα|σκορ|νίκησε|κέρδισε|αγώνας|αγώνα|τελευταία|πρόσφατα|εκλογές|τιμή)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Questions about current events that need live results.</summary>
    internal static bool NeedsWebSearch(string query) =>
        !string.IsNullOrWhiteSpace(query) && WebSearchPattern.IsMatch(query);

    internal static bool IsAnthropicEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

    // OPENJIBO_CLAUDE_BRAIN=false turns the Claude-first brain (skills tool + mood) off.
    private static bool IsClaudeBrainEnabled() =>
        !string.Equals(
            Environment.GetEnvironmentVariable("OPENJIBO_CLAUDE_BRAIN")?.Trim(),
            "false",
            StringComparison.OrdinalIgnoreCase);

    private void RememberResult(string query, KnowledgeSearchResult? result)
    {
        if (result?.Outcome != KnowledgeSearchOutcome.Found) return;
        if (!string.IsNullOrWhiteSpace(result.CommandPhrase))
            RememberTurn(query, $"(Jibo ran its skill: {result.CommandPhrase})");
        else
            RememberTurn(query, result.AnswerText);
    }

    // OPENJIBO_WEB_SEARCH=false turns live web search off.
    // OPENJIBO_LIGHT_RING_ON_PHOTO=false turns off the light-ring glow after a camera photo.
    internal static bool IsLightRingOnPhotoEnabled() =>
        !string.Equals(
            Environment.GetEnvironmentVariable("OPENJIBO_LIGHT_RING_ON_PHOTO")?.Trim(),
            "false",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsWebSearchEnabled() =>
        !string.Equals(
            Environment.GetEnvironmentVariable("OPENJIBO_WEB_SEARCH")?.Trim(),
            "false",
            StringComparison.OrdinalIgnoreCase);

    // OPENJIBO_WEB_SEARCH_MAX_USES caps searches per question (1-5, default 2) to control cost.
    private static int ResolveWebSearchMaxUses()
    {
        var raw = Environment.GetEnvironmentVariable("OPENJIBO_WEB_SEARCH_MAX_USES");
        return int.TryParse(raw, out var value) ? Math.Clamp(value, 1, 5) : 2;
    }

    // Optional approximate location for local results (weather, local news):
    // OPENJIBO_WEB_SEARCH_CITY / _REGION / _COUNTRY (2-letter) / _TIMEZONE (IANA).
    private static JsonObject? ResolveWebSearchUserLocation()
    {
        var fields = new (string Key, string Env)[]
        {
            ("city", "OPENJIBO_WEB_SEARCH_CITY"),
            ("region", "OPENJIBO_WEB_SEARCH_REGION"),
            ("country", "OPENJIBO_WEB_SEARCH_COUNTRY"),
            ("timezone", "OPENJIBO_WEB_SEARCH_TIMEZONE")
        };

        JsonObject? location = null;
        foreach (var (key, env) in fields)
        {
            var value = Environment.GetEnvironmentVariable(env)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            location ??= new JsonObject { ["type"] = "approximate" };
            location[key] = value;
        }

        return location;
    }

    private static List<ChatCompletionRequestMessage> BuildMessages(
        string systemMessage,
        IReadOnlyList<ConversationTurn> history,
        object userContent)
    {
        var messages = new List<ChatCompletionRequestMessage>(2 + history.Count * 2)
        {
            new ChatCompletionRequestMessage("system", systemMessage)
        };
        foreach (var turn in history)
        {
            messages.Add(new ChatCompletionRequestMessage("user", turn.User));
            messages.Add(new ChatCompletionRequestMessage("assistant", turn.Assistant));
        }

        messages.Add(new ChatCompletionRequestMessage("user", userContent));
        return messages;
    }

    private IReadOnlyList<ConversationTurn> GetActiveHistory()
    {
        lock (_historyLock)
        {
            if (_history.Count > 0 &&
                DateTimeOffset.UtcNow - _history[^1].AtUtc > HistoryIdleTimeout)
                _history.Clear();

            return _history.ToArray();
        }
    }

    private void RememberTurn(string user, string assistant)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(assistant)) return;

        lock (_historyLock)
        {
            _history.Add(new ConversationTurn(user, assistant, DateTimeOffset.UtcNow));
            while (_history.Count > MaxHistoryTurns)
                _history.RemoveAt(0);
        }
    }

    private static readonly Regex TimeSensitivePattern = new(
        @"\b(today|tonight|tomorrow|yesterday|now|current|currently|latest|recent|news|weather|forecast|score|scores|time|date|this (week|month|year|weekend)|ώρα|ημερομηνία|σήμερα|απόψε|αύριο|χθες|χτες|τώρα|ειδήσεις|καιρός|καιρό)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsTimeSensitive(string query) =>
        !string.IsNullOrWhiteSpace(query) && TimeSensitivePattern.IsMatch(query);

    private sealed record ConversationTurn(string User, string Assistant, DateTimeOffset AtUtc);

    private static string BuildCacheKey(SearchBackendSpec spec, string model, string query)
    {
        return $"chatgpt|{spec.Credential}|{model}|{query}";
    }

    private bool TryGetCachedValue(string cacheKey, out KnowledgeSearchResult? result)
    {
        result = null;
        if (!_cache.TryGetValue(cacheKey, out var entry) || entry.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return false;

        result = entry.Result;
        return true;
    }

    private void SetCachedValue(string cacheKey, KnowledgeSearchResult? result, int ttlSeconds)
    {
        _cache[cacheKey] = new CacheEntry(
            result,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, ttlSeconds)));
    }

    private sealed record CacheEntry(KnowledgeSearchResult? Result, DateTimeOffset ExpiresAtUtc);

    private sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")]
        IReadOnlyList<ChatCompletionRequestMessage> Messages,
        [property: JsonPropertyName("stream")] bool Stream = false);

    private sealed record ChatCompletionMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")]
        string Content);

    private sealed record ChatCompletionRequestMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")]
        object Content);

    private sealed record ChatCompletionResponse(
        [property: JsonPropertyName("choices")]
        IReadOnlyList<ChatCompletionChoice>? Choices);

    private sealed record ChatCompletionChoice(
        [property: JsonPropertyName("message")]
        ChatCompletionMessage? Message);
}