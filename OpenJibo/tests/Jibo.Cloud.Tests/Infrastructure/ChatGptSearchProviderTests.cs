using System.Net;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class ChatGptSearchProviderTests
{
    [Fact]
    public async Task SearchAsync_ReturnsAnswer_WhenOpenAiResponds()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""
                                {
                                  "choices": [
                                    {
                                      "message": {
                                        "role": "assistant",
                                        "content": "Donald Trump was born on June 14, 1946. As of June 19, 2026, he is 80 years old."
                                      }
                                    }
                                  ]
                                }
                                """);
        });
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "How old is Donald Trump",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        Assert.NotNull(result);
        Assert.Equal(SearchBackendKind.ChatGPT, result!.BackendKind);
        Assert.Contains("80 years old", result.AnswerText);
        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        // The endpoint can be overridden (e.g. OPENJIBO_SEARCH_CHATGPT_ENDPOINT for Claude), so compare
        // against whatever the resolver reports instead of hard-coding OpenAI.
        Assert.Equal(
            SearchBackendSettingsResolver.ResolveEndpoint(
                new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")),
            capturedRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization.Parameter);

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal("gpt-5.4-nano", json.RootElement.GetProperty("model").GetString());
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("You ARE Jibo", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("family-friendly", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("How old is Donald Trump", messages[1].GetProperty("content").GetString());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task SearchAsync_UsesDefaultModel_WhenModelOmitted()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"Hi"}}]}""");
        });
        var provider = CreateProvider(handler, model: null);

        await provider.SearchAsync(new KnowledgeSearchRequest(
            "Hello",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", null)));

        using var json = JsonDocument.Parse(capturedBody!);
        Assert.Equal(SearchBackendSettingsResolver.DefaultChatGptModel,
            json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task SearchAsync_StripsMarkdownBold_ForSpeech()
    {
        var handler = new RecordingHttpMessageHandler(_ => JsonResponse("""
                                                                        {
                                                                          "choices": [
                                                                            {
                                                                              "message": {
                                                                                "role": "assistant",
                                                                                "content": "Donald Trump was born on **June 14, 1946**."
                                                                              }
                                                                            }
                                                                          ]
                                                                        }
                                                                        """));
        var provider = CreateProvider(handler);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "How old is Donald Trump",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", null)));

        Assert.NotNull(result);
        Assert.Equal("Donald Trump was born on June 14, 1946.", result!.AnswerText);
    }

    [Fact]
    public async Task SearchAsync_ReturnsNull_WhenApiKeyMissing()
    {
        var provider = CreateProvider(
            new RecordingHttpMessageHandler(_ => JsonResponse("""{"choices":[]}""")),
            null);

        var result = await provider.SearchAsync(new KnowledgeSearchRequest(
            "Hello",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, null, null)));

        Assert.Null(result);
    }

    [Fact]
    public async Task SearchAsync_UsesCustomInstructions_WhenConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var handler = new RecordingHttpMessageHandler(request =>
        {
            capturedRequest = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"choices":[{"message":{"role":"assistant","content":"Hi"}}]}""");
        });
        var provider = CreateProvider(handler, llmInstructions: "Custom assistant instructions.");

        await provider.SearchAsync(new KnowledgeSearchRequest(
            "Hello",
            new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano")));

        using var json = JsonDocument.Parse(capturedBody!);
        var messages = json.RootElement.GetProperty("messages");
        Assert.StartsWith("Custom assistant instructions.", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Contains("Right now it is", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("You ARE Jibo", messages[0].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_SendsEarlierTurns_ForFollowUpQuestions()
    {
        var bodies = new List<string>();
        var replies = new Queue<string>(["The president is Donald Trump.", "He is 80 years old."]);
        var handler = new RecordingHttpMessageHandler(request =>
        {
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var reply = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { role = "assistant", content = replies.Dequeue() } } }
            });
            return JsonResponse(reply);
        });
        var provider = CreateProvider(handler);
        var spec = new SearchBackendSpec(SearchBackendKind.ChatGPT, "test-api-key", "gpt-5.4-nano");

        await provider.SearchAsync(new KnowledgeSearchRequest("Who is the president of the USA", spec));
        var second = await provider.SearchAsync(new KnowledgeSearchRequest("How old is he", spec));

        Assert.Equal("He is 80 years old.", second!.AnswerText);
        using var json = JsonDocument.Parse(bodies[1]);
        var messages = json.RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("Who is the president of the USA", messages[1].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.Equal("The president is Donald Trump.", messages[2].GetProperty("content").GetString());
        Assert.Equal("How old is he", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public void IsTimeSensitive_DetectsQuestionsThatMustNotBeCached()
    {
        Assert.True(ChatGptSearchProvider.IsTimeSensitive("What day is it today"));
        Assert.True(ChatGptSearchProvider.IsTimeSensitive("what's the weather tomorrow"));
        Assert.True(ChatGptSearchProvider.IsTimeSensitive("latest news"));
        Assert.False(ChatGptSearchProvider.IsTimeSensitive("what is fifteen plus fifteen"));
        Assert.False(ChatGptSearchProvider.IsTimeSensitive("who wrote Hamlet"));
        Assert.True(ChatGptSearchProvider.IsTimeSensitive("τι ώρα είναι στην ελλάδα"));
        Assert.False(ChatGptSearchProvider.IsTimeSensitive("πες μου ένα αστείο"));
    }

    [Theory]
    [InlineData("What's in the news today", true)]
    [InlineData("Who won the Leafs game last night", true)]
    [InlineData("What's the weather this weekend", true)]
    [InlineData("When is the new iPhone coming out", true)]
    [InlineData("What is fifteen plus fifteen", false)]
    [InlineData("Who wrote Hamlet", false)]
    [InlineData("How old is he", false)]
    [InlineData("τι ειδήσεις έχει σήμερα", true)]
    [InlineData("ποιος κέρδισε τον αγώνα", true)]
    [InlineData("πες μου ένα αστείο", false)]
    public void NeedsWebSearch_OnlyForCurrentEvents(string query, bool expected)
    {
        Assert.Equal(expected, ChatGptSearchProvider.NeedsWebSearch(query));
    }

    [Theory]
    [InlineData("https://api.anthropic.com/v1/chat/completions", true)]
    [InlineData("https://API.ANTHROPIC.COM/v1/chat/completions", true)]
    [InlineData("https://api.openai.com/v1/chat/completions", false)]
    [InlineData("not a url", false)]
    public void IsAnthropicEndpoint_DetectsClaude(string endpoint, bool expected)
    {
        Assert.Equal(expected, ChatGptSearchProvider.IsAnthropicEndpoint(endpoint));
    }

    [Fact]
    public void BuildWebSearchRequestBody_IncludesToolHistoryAndLocation()
    {
        var location = new System.Text.Json.Nodes.JsonObject { ["type"] = "approximate", ["city"] = "Toronto" };
        var body = ChatGptSearchProvider.BuildWebSearchRequestBody(
            "claude-haiku-5-5",
            "system text",
            [("Who is the president", "Donald Trump.")],
            "Any news about him today",
            2,
            location);

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal("system text", root.GetProperty("system").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(3, messages.GetArrayLength());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("Any news about him today", messages[2].GetProperty("content").GetString());
        var tool = root.GetProperty("tools")[0];
        Assert.Equal("web_search_20250305", tool.GetProperty("type").GetString());
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.Equal(2, tool.GetProperty("max_uses").GetInt32());
        Assert.Equal("Toronto", tool.GetProperty("user_location").GetProperty("city").GetString());
    }

    [Fact]
    public void BuildWebSearchRequestBody_OmitsLocationWhenNotConfigured()
    {
        var body = ChatGptSearchProvider.BuildWebSearchRequestBody("m", "s", [], "q today", 1, null);

        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("tools")[0].TryGetProperty("user_location", out _));
    }

    [Fact]
    public void ExtractWebSearchAnswer_UsesOnlyTextAfterLastSearchResult()
    {
        const string response = """
            {
              "content": [
                { "type": "text", "text": "Let me check the latest score." },
                { "type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": { "query": "Leafs score" } },
                { "type": "web_search_tool_result", "tool_use_id": "srvtoolu_1", "content": [] },
                { "type": "text", "text": "The Leafs won ", "citations": [ { "type": "web_search_result_location" } ] },
                { "type": "text", "text": "four to two last night." }
              ],
              "usage": { "input_tokens": 9000, "output_tokens": 40, "server_tool_use": { "web_search_requests": 1 } }
            }
            """;

        var (answer, searches) = ChatGptSearchProvider.ExtractWebSearchAnswer(response);

        Assert.Equal("The Leafs won four to two last night.", answer);
        Assert.Equal(1, searches);
    }

    [Fact]
    public void ExtractWebSearchAnswer_UsesAllTextWhenNoSearchHappened()
    {
        const string response = """{"content":[{"type":"text","text":"Thirty."}],"usage":{"input_tokens":10,"output_tokens":2}}""";

        var (answer, searches) = ChatGptSearchProvider.ExtractWebSearchAnswer(response);

        Assert.Equal("Thirty.", answer);
        Assert.Equal(0, searches);
    }

    [Theory]
    [InlineData("[happy] Here is a fun one!", "happy", "Here is a fun one!")]
    [InlineData("  [Neutral]  Paris is the capital.", "neutral", "Paris is the capital.")]
    [InlineData("Hello [sad] there", null, "Hello there")]
    [InlineData("No tag at all.", null, "No tag at all.")]
    public void ParseMood_SplitsLeadingTagAndRemovesStrayTags(string input, string? mood, string text)
    {
        var (parsedMood, parsedText) = ChatGptSearchProvider.ParseMood(input);

        Assert.Equal(mood, parsedMood);
        Assert.Equal(text, parsedText);
    }

    [Fact]
    public void FindFreshVisionPhoto_PicksNewestRecentImageFromMediaFolder()
    {
        var root = Directory.CreateTempSubdirectory("openjibo-vision-").FullName;
        try
        {
            var now = DateTime.UtcNow;
            var jpeg = new byte[2052];
            jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF; jpeg[3] = 0xE0;
            var old = Path.Combine(root, "a", "old.bin");
            var fresh = Path.Combine(root, "b", "fresh.bin");
            var notImage = Path.Combine(root, "b", "log.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
            File.WriteAllBytes(old, jpeg);
            File.SetLastWriteTimeUtc(old, now.AddMinutes(-10));
            File.WriteAllBytes(fresh, jpeg);
            File.SetLastWriteTimeUtc(fresh, now.AddSeconds(-30));
            File.WriteAllBytes(notImage, new byte[4096]);
            File.SetLastWriteTimeUtc(notImage, now.AddSeconds(-5));

            Assert.Equal(fresh, ChatGptSearchProvider.FindFreshVisionPhoto([root], now, TimeSpan.FromMinutes(3)));
            Assert.Null(ChatGptSearchProvider.FindFreshVisionPhoto([old], now, TimeSpan.FromMinutes(3)));
            Assert.Null(ChatGptSearchProvider.FindFreshVisionPhoto(
                [Path.Combine(root, "missing")], now, TimeSpan.FromMinutes(3)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("{\"id\":\"ea0fdb82-e542-11f1-88e3-f0c77f960846\"}", "ea0fdb82-e542-11f1-88e3-f0c77f960846")]
    [InlineData("{\"id\":\"\"}", null)]
    [InlineData("{\"error\":\"busy\"}", null)]
    [InlineData("not json", null)]
    public void ReadPhotoId_ReadsMediaServiceAnswer(string json, string? expected)
    {
        Assert.Equal(expected, ChatGptSearchProvider.ReadPhotoId(json));
    }

    [Theory]
    [InlineData("learn my face i'm makis", false, "Makis")]
    [InlineData("hey jibo learn my face, my name is mary please", false, "Mary")]
    [InlineData("i'm nick, remember my face", false, "Nick")]
    [InlineData("learn emma's face", false, "Emma")]
    [InlineData("forget my face i'm makis", true, "Makis")]
    [InlineData("learn my face", false, "")]
    public void ParseFaceCommand_ReadsLearnAndForget(string query, bool forget, string name)
    {
        var command = ChatGptSearchProvider.ParseFaceCommand(query);

        Assert.NotNull(command);
        Assert.Equal(forget, command!.Forget);
        Assert.Equal(name, command.Name);
    }

    [Theory]
    [InlineData("what do you see")]
    [InlineData("remember to buy milk")]
    [InlineData("who am i")]
    public void ParseFaceCommand_IgnoresOtherSentences(string query)
    {
        Assert.Null(ChatGptSearchProvider.ParseFaceCommand(query));
    }

    [Fact]
    public void BuildFaceNote_NamesRecognisedPeopleAndWarnsAgainstGuessing()
    {
        Assert.Contains("recognised: Makis, Mary",
            ChatGptSearchProvider.BuildFaceNote(new ChatGptSearchProvider.FaceIdentification(2, ["Makis", "Mary"])),
            StringComparison.Ordinal);
        Assert.Contains("Do not guess names",
            ChatGptSearchProvider.BuildFaceNote(new ChatGptSearchProvider.FaceIdentification(1, [])),
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, ChatGptSearchProvider.BuildFaceNote(null));
        Assert.Equal(string.Empty,
            ChatGptSearchProvider.BuildFaceNote(new ChatGptSearchProvider.FaceIdentification(0, [])));
    }

    [Theory]
    [InlineData("hey jibo what do you see", true)]
    [InlineData("what's in front of you", true)]
    [InlineData("who am i", true)]
    [InlineData("what's the weather", false)]
    public void IsVisionQuestion_DetectsCameraQuestions(string query, bool expected)
    {
        Assert.Equal(expected, ChatGptSearchProvider.IsVisionQuestion(query));
    }

    [Fact]
    public void DetectImageMediaType_RecognisesJpegAndPng()
    {
        Assert.Equal("image/jpeg", ChatGptSearchProvider.DetectImageMediaType([0xFF, 0xD8, 0xFF, 0xDB]));
        Assert.Equal(
            "image/png",
            ChatGptSearchProvider.DetectImageMediaType([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Null(ChatGptSearchProvider.DetectImageMediaType([0x00, 0x01, 0x02]));
    }

    [Theory]
    [InlineData("Pizza is great! [anim:pizza]", "pizza", "Pizza is great!")]
    [InlineData("[anim: Coin-Flip ] Heads!", "coin-flip", "Heads!")]
    [InlineData("Hi [anim:greeting] there [anim:heart]", "greeting", "Hi there")]
    [InlineData("No animation here.", null, "No animation here.")]
    public void ParseAnimation_TakesFirstTagAndStripsAll(string input, string? animation, string text)
    {
        var (parsedAnimation, parsedText) = ChatGptSearchProvider.ParseAnimation(input);

        Assert.Equal(animation, parsedAnimation);
        Assert.Equal(text, parsedText);
    }

    [Fact]
    public void ExtractClaudeReply_ReadsJiboCommandToolCall()
    {
        const string response = """
            {
              "content": [
                { "type": "text", "text": "Sure!" },
                { "type": "tool_use", "id": "toolu_1", "name": "jibo_command", "input": { "command": "set a timer for 5 minutes" } }
              ],
              "stop_reason": "tool_use",
              "usage": { "input_tokens": 900, "output_tokens": 30 }
            }
            """;

        var (_, command, searches) = ChatGptSearchProvider.ExtractClaudeReply(response);

        Assert.Equal("set a timer for 5 minutes", command);
        Assert.Equal(0, searches);
    }

    [Fact]
    public void ExtractClaudeReply_NoCommandForPlainAnswer()
    {
        const string response = """{"content":[{"type":"text","text":"[happy] Thirty!"}],"usage":{"input_tokens":10,"output_tokens":4}}""";

        var (answer, command, _) = ChatGptSearchProvider.ExtractClaudeReply(response);

        Assert.Equal("[happy] Thirty!", answer);
        Assert.Null(command);
    }

    [Fact]
    public void BuildClaudeRequestBody_AddsJiboCommandTool_AndWebSearchOnlyWhenAsked()
    {
        var withBoth = ChatGptSearchProvider.BuildClaudeRequestBody("m", "s", [], "q", true, true, 2, null);
        var commandOnly = ChatGptSearchProvider.BuildClaudeRequestBody("m", "s", [], "q", true, false, 2, null);
        var none = ChatGptSearchProvider.BuildClaudeRequestBody("m", "s", [], "q", false, false, 2, null);

        using var both = JsonDocument.Parse(withBoth);
        Assert.Equal(2, both.RootElement.GetProperty("tools").GetArrayLength());

        using var cmd = JsonDocument.Parse(commandOnly);
        var tool = cmd.RootElement.GetProperty("tools")[0];
        Assert.Equal("jibo_command", tool.GetProperty("name").GetString());
        Assert.Equal("object", tool.GetProperty("input_schema").GetProperty("type").GetString());
        Assert.Equal("command", tool.GetProperty("input_schema").GetProperty("required")[0].GetString());

        using var plain = JsonDocument.Parse(none);
        Assert.False(plain.RootElement.TryGetProperty("tools", out _));
    }

    private static ChatGptSearchProvider CreateProvider(
        HttpMessageHandler handler,
        string? apiKey = "test-api-key",
        string? model = "gpt-5.4-nano",
        string? llmInstructions = null)
    {
        var primary = model is null
            ? $"ChatGPT!{apiKey}"
            : $"ChatGPT!{apiKey}!{model}";

        // These tests fake the OpenAI chat-completions format; keep that path even when the
        // machine's .env points the endpoint at Claude.
        return new ChatGptSearchProvider(
            new HttpClient(handler),
            SearchBackendOptions.Create(primary, null, 300, 45, llmInstructions),
            NullLogger<ChatGptSearchProvider>.Instance)
        {
            UseChatCompletionsOnly = true
        };
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responseFactory(request));
        }
    }
}