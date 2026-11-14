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

        // Live web search (Claude only): used for current-events questions so answers are
        // up to date. Costs one search fee per question, so it is not used for everything.
        var useWebSearch = !attachVision &&
                           IsAnthropicEndpoint(endpoint) &&
                           IsWebSearchEnabled() &&
                           NeedsWebSearch(query);

        // A cached answer is only safe when it cannot depend on a photo, on earlier turns,
        // on live web results, or on the current time/date.
        var useCache = !useWebSearch && !attachVision && history.Count == 0 && !IsTimeSensitive(query);
        if (useCache && TryGetCachedValue(cacheKey, out var cachedResult))
        {
            if (cachedResult?.Outcome == KnowledgeSearchOutcome.Found)
                RememberTurn(query, cachedResult.AnswerText);
            return cachedResult;
        }

        if (useWebSearch)
        {
            var webResult = await TryWebSearchAsync(
                request.BackendSpec.Credential,
                model,
                query,
                history,
                cancellationToken);
            if (webResult is not null)
            {
                RememberTurn(query, webResult.AnswerText);
                return webResult;
            }

            // Web search failed or was refused: fall through to a normal answer.
        }

        var result = await TryCompleteAsync(
            endpoint,
            request.BackendSpec.Credential,
            model,
            query,
            attachVision,
            history,
            cancellationToken);

        if (result.Outcome == KnowledgeSearchOutcome.Found)
            RememberTurn(query, result.AnswerText);

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

    private async Task<KnowledgeSearchResult?> TryWebSearchAsync(
        string apiKey,
        string model,
        string query,
        IReadOnlyList<ConversationTurn> history,
        CancellationToken cancellationToken)
    {
        try
        {
            var (systemMessage, _) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(
                query,
                options.LlmInstructions);
            systemMessage += "\n\n" + WebSearchInstructions;

            var body = BuildWebSearchRequestBody(
                model,
                systemMessage,
                history.Select(turn => (turn.User, turn.Assistant)).ToList(),
                query,
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
                    "Claude web search failed with status {StatusCode}: {Body}",
                    response.StatusCode,
                    responseBody.Length > 300 ? responseBody[..300] : responseBody);
                return null;
            }

            var (answer, searches) = ExtractWebSearchAnswer(responseBody);
            var spoken = KnowledgeSearchResponseFormatter.NormalizeForSpeech(answer);
            if (string.IsNullOrWhiteSpace(spoken))
                return null;

            logger.LogInformation("Answered with Claude web search (searches={Searches}).", searches);
            return new KnowledgeSearchResult(spoken, SearchBackendKind.ChatGPT);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Claude web search failed; falling back to a normal answer.");
            return null;
        }
    }

    internal static string BuildWebSearchRequestBody(
        string model,
        string systemMessage,
        IReadOnlyList<(string User, string Assistant)> history,
        string query,
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

        var tool = new JsonObject
        {
            ["type"] = WebSearchToolType,
            ["name"] = "web_search",
            ["max_uses"] = maxUses
        };
        if (userLocation is not null)
            tool["user_location"] = userLocation;

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 400,
            ["system"] = systemMessage,
            ["messages"] = messages,
            ["tools"] = new JsonArray { tool }
        };
        return body.ToJsonString();
    }

    /// <summary>
    /// Returns the spoken answer from a Messages API response that may contain web search blocks.
    /// Claude often writes a short "let me check" line before searching, so only the text after
    /// the last search result is used (or all text when no search happened).
    /// </summary>
    internal static (string Answer, int Searches) ExtractWebSearchAnswer(string responseJson)
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
            return (string.Empty, searches);

        var blocks = content.EnumerateArray().ToList();
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

        return (answer.ToString().Trim(), searches);
    }

    private static readonly Regex WebSearchPattern = new(
        @"\b(today|tonight|yesterday|this (week|weekend|month|year)|latest|recent|recently|news|headlines|current|currently|right now|score|scores|won|winner|game|match|playoffs?|election|stocks?|price of|weather|forecast|released?|coming out|upcoming)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Questions about current events that need live results.</summary>
    internal static bool NeedsWebSearch(string query) =>
        !string.IsNullOrWhiteSpace(query) && WebSearchPattern.IsMatch(query);

    internal static bool IsAnthropicEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

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
        @"\b(today|tonight|tomorrow|yesterday|now|current|currently|latest|recent|news|weather|forecast|score|scores|time|date|this (week|month|year|weekend))\b",
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