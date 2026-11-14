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
        var cacheKey = BuildCacheKey(request.BackendSpec, model, query);
        var attachVision = ShouldAttachVisionPhoto(query);
        var history = GetActiveHistory();

        // Claude-first brain (Claude only): one native Messages API call that can answer with
        // a mood, ask Jibo to run a built-in skill (jibo_command tool), or search the web.
        var isClaude = !UseChatCompletionsOnly && !attachVision && IsAnthropicEndpoint(endpoint);
        var useBrain = isClaude && IsClaudeBrainEnabled();

        // Live web search: only for current-events questions (one search fee per question).
        var useWebSearch = isClaude && IsWebSearchEnabled() && NeedsWebSearch(query);

        // A cached answer is only safe when it cannot depend on a photo, on earlier turns,
        // on live web results, or on the current time/date.
        var useCache = !useWebSearch && !attachVision && history.Count == 0 && !IsTimeSensitive(query);
        if (useCache && TryGetCachedValue(cacheKey, out var cachedResult))
        {
            RememberResult(query, cachedResult);
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
                return claudeResult;
            }

            // Claude call failed or was refused: fall through to the plain answer path.
        }

        var result = await TryCompleteAsync(
            endpoint,
            request.BackendSpec.Credential,
            model,
            query,
            attachVision,
            history,
            cancellationToken);

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
        bool attachVision,
        IReadOnlyList<ConversationTurn> history,
        CancellationToken cancellationToken)
    {
        try
        {
            var (systemMessage, userMessage) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(
                prompt,
                options.LlmInstructions);

            object userContent = userMessage;
            const string visionPhotoPath = "/data/vision-latest.jpg";
            if (attachVision && System.IO.File.Exists(visionPhotoPath))
            {
                try
                {
                    var imageBytes = await System.IO.File.ReadAllBytesAsync(visionPhotoPath, cancellationToken);
                    var imageBase64 = Convert.ToBase64String(imageBytes);
                    userContent = new object[]
                    {
                        new { type = "text", text = userMessage },
                        new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{imageBase64}" } }
                    };
                    logger.LogInformation("Attached vision photo to ChatGPT/Claude request ({Bytes} bytes).", imageBytes.Length);
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
        "what do you see", "what can you see", "do you see", "look at", "looking at",
        "what am i wearing", "am i wearing", "what is this", "what's this",
        "what am i holding", "describe me", "how do i look", "what color is",
        "who is this", "read this"
    ];

    private static bool ShouldAttachVisionPhoto(string query)
    {
        const string path = "/data/vision-latest.jpg";
        if (string.IsNullOrWhiteSpace(query) || !System.IO.File.Exists(path))
            return false;
        if (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(path) > TimeSpan.FromMinutes(2))
            return false;
        var q = query.ToLowerInvariant();
        return VisionTriggerPhrases.Any(t => q.Contains(t));
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
        "If the user wants Jibo to do something it has a built-in skill for, or asks about Jibo itself, " +
        "call the jibo_command tool instead of answering, in any language the user speaks. " +
        "Answer general questions, conversation and facts about the world yourself. " +
        "Questions about the time in another city or country are general questions: answer them yourself.";

    private static readonly string AnimationInstructions =
        "Jibo can show an animation on its screen while it speaks. When one clearly fits, add exactly one " +
        "tag [anim:NAME] at the end of your answer, using only these names: " +
        JiboAnimationCatalog.BuildPromptMenu() +
        " For example: [happy] Pizza is my favourite too! [anim:pizza]. Leave it out when nothing fits.";

    private const string JiboCommandToolName = "jibo_command";

    private const string JiboCommandToolDescription =
        "Run one of Jibo's built-in skills. Use it when the user wants Jibo to do something it has a skill " +
        "for, or asks about Jibo itself, in any language or wording. Skills: timers and alarms (set, cancel, " +
        "check), the local time and date, weather, news, calendar, commute, volume (up, down, or a number), " +
        "stop, go to sleep, wake up, dance, turn around, take a picture, show the photo gallery, play the " +
        "radio (optionally a genre), roll dice, spell a word, define a word, word of the day, fun fact, tell " +
        "a joke, tell a story, surprise me, shopping and to-do lists, smart-home lights and thermostat, and " +
        "questions about Jibo itself (its name, age, favourites, feelings, where it was born). Do not use it " +
        "for general questions, conversation or facts about the world.";

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

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = query });

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