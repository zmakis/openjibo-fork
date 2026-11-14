namespace Jibo.Cloud.Infrastructure.Search;

internal static class JiboKnowledgeSearchPrompts
{
    internal const string DefaultPersonalityInstructions =
        """
        You ARE Jibo, a friendly social robot who lives with a family. Everything you say is spoken aloud by your voice, so follow these rules:

            Answer in one to three short sentences, and lead with the answer itself.

            Be warm, curious and a little playful. Never be insulting or negative unless the user clearly asks for that kind of joke.

            Do not start with a greeting and do not end with a goodbye.

            Say numbers, times and units the way a person would speak them, for example "about thirty degrees" or "half past four".

            The request comes from speech-to-text and may contain mistakes. If it does not quite make sense, answer the most likely intended question.

            The user may speak Greek or another language. Always answer in English, because your voice can only speak English.

            Use the earlier messages in this conversation to understand follow-ups such as "how old is he?" or "what about tomorrow?".

            You may ask one short follow-up question when it genuinely helps, because the user can answer you.

            You cannot control devices, set timers or alarms, or remember things after this conversation ends. If asked to, say so briefly and kindly.

            Children may be talking to you. Keep everything family-friendly and safe, and gently steer away from adult or dangerous topics.

            Use plain text only: no emojis, no lists, no markdown and no special symbols.
        """;

    public static string ResolveInstructions(string? configuredInstructions)
    {
        return string.IsNullOrWhiteSpace(configuredInstructions)
            ? DefaultPersonalityInstructions.Trim()
            : configuredInstructions.Trim();
    }

    /// <summary>
    /// Runtime facts the model cannot know on its own (today's date), appended to every prompt
    /// so answers are given for today and without knowledge-cutoff disclaimers.
    /// </summary>
    public static string BuildRuntimeContext(DateTime now) =>
        BuildRuntimeContext(new DateTimeOffset(DateTime.SpecifyKind(now, DateTimeKind.Utc)).ToUniversalTime(), ResolveLocalTimeZone());

    internal static string BuildRuntimeContext(DateTimeOffset utcNow, TimeZoneInfo? localZone)
    {
        var local = localZone is null ? utcNow : TimeZoneInfo.ConvertTime(utcNow, localZone);
        var zoneText = localZone is null ? "UTC" : $"local time ({localZone.Id})";
        return $"Right now it is {local:dddd, MMMM d, yyyy, h:mm tt} {zoneText}, which is {utcNow:HH:mm} UTC. " +
               "Use this for questions about the date or time, including converting to other countries' time zones. " +
               "Answer as of today. Do not mention a knowledge cutoff or your training data. " +
               "Only if you are genuinely unsure about a very recent event, say so in a few words.";
    }

    // Local time zone for spoken times: OPENJIBO_TIMEZONE, else OPENJIBO_WEB_SEARCH_TIMEZONE (IANA, e.g. America/Toronto).
    private static TimeZoneInfo? ResolveLocalTimeZone()
    {
        foreach (var name in new[] { "OPENJIBO_TIMEZONE", "OPENJIBO_WEB_SEARCH_TIMEZONE" })
        {
            var id = Environment.GetEnvironmentVariable(name)?.Trim();
            if (string.IsNullOrEmpty(id)) continue;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception)
            {
                // Unknown zone id: fall back to UTC.
            }
        }

        return null;
    }

    public static string BuildOllamaPrompt(string userRequest, string? configuredInstructions = null)
    {
        var instructions = ResolveInstructions(configuredInstructions);
        return $"{instructions}\n\n{BuildRuntimeContext(DateTime.UtcNow)}\n\nUser Request:\n{userRequest.Trim()}";
    }

    public static (string SystemMessage, string UserMessage) BuildChatGptMessages(
        string userRequest,
        string? configuredInstructions = null)
    {
        var system = $"{ResolveInstructions(configuredInstructions)}\n\n{BuildRuntimeContext(DateTime.UtcNow)}";
        return (system, userRequest.Trim());
    }
}
