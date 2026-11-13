using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        var cacheKey = BuildCacheKey(request.BackendSpec, model, request.Query.Trim());
        var attachVision = ShouldAttachVisionPhoto(request.Query);
        if (!attachVision && TryGetCachedValue(cacheKey, out var cachedResult))
            return cachedResult;

        var result = await TryCompleteAsync(
            endpoint,
            request.BackendSpec.Credential,
            model,
            request.Query.Trim(),
            attachVision,
            cancellationToken);
        if (attachVision)
            return result;
        SetCachedValue(
            cacheKey,
            result,
            result.Outcome == KnowledgeSearchOutcome.Found
                ? options.CacheTtlSeconds
                : options.FailureCacheTtlSeconds);
        return result;
    }

    private async Task<KnowledgeSearchResult> TryCompleteAsync(
        string endpoint,
        string apiKey,
        string model,
        string prompt,
        bool attachVision,
        CancellationToken cancellationToken)
    {
        try
        {
            var (systemMessage, userMessage) = JiboKnowledgeSearchPrompts.BuildChatGptMessages(
                prompt,
                options.LlmInstructions);
            systemMessage += $"\n\nToday's date is {DateTime.Now:dddd, MMMM d, yyyy}. Answer for today, briefly, in one or two spoken sentences. Do not mention a knowledge cutoff or training data. Only if you are genuinely unsure about a very recent event, say so in a few words.";

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
                    [
                        new ChatCompletionRequestMessage("system", systemMessage),
                        new ChatCompletionRequestMessage("user", userContent)
                    ]));

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