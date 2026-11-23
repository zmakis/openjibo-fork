using System.Text;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// "Hey Jibo, how was your day?" / "show me your stats": daily activity counters,
/// spoken and shown as text on Jibo's face.
/// </summary>
public sealed partial class JiboInteractionService
{
    internal const string StatsIntent = "jibo_stats";
    internal const string ScreenTextPayloadKey = "screen_text";

    private static readonly Regex StatsRequestPattern = new(
        @"\b(your|jibo'?s?)\s+(daily\s+)?(stats|statistics|numbers|activity)\b|" +
        @"\bhow\s+was\s+your\s+(day|week)\b|" +
        @"\bhow\s+many\s+(conversations|talks|questions|photos|pictures)\b|" +
        @"\bwhat\s+did\s+you\s+do\s+(today|this\s+week)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsStatsRequest(string lowered) =>
        !string.IsNullOrWhiteSpace(lowered) && StatsRequestPattern.IsMatch(lowered);

    private static bool IsWeekStatsRequest(string lowered) =>
        lowered.Contains("week", StringComparison.OrdinalIgnoreCase);

    private void RecordTalkForStats(TurnContext turn)
    {
        var messageType = turn.Attributes.TryGetValue("messageType", out var rawMessageType)
            ? rawMessageType?.ToString()
            : null;
        if (string.Equals(messageType, "TRIGGER", StringComparison.OrdinalIgnoreCase)) return;

        var transcript = (turn.NormalizedTranscript ?? turn.RawTranscript ?? string.Empty).Trim();
        if (transcript.Length == 0) return;

        var local = TryResolveReferenceLocalTime(turn) ?? DateTimeOffset.Now;
        JiboDailyStats.RecordTalk(local, RobotPerception.Current?.SpeakerName);
    }

    private static JiboInteractionDecision BuildStatsDecision(string lowered, DateTimeOffset? robotLocalTime)
    {
        var today = robotLocalTime is { } local ? DateOnly.FromDateTime(local.DateTime) : JiboDailyStats.Today();
        var week = IsWeekStatsRequest(lowered);
        var stats = week ? JiboDailyStats.Range(today, 7) : JiboDailyStats.Get(today);

        var decision = ChitchatStateMachine.BuildKnowledgeSearchResponseDecision(
            BuildStatsSpeech(stats, week));
        var payload = decision.SkillPayload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(decision.SkillPayload, StringComparer.OrdinalIgnoreCase);
        payload[ScreenTextPayloadKey] = BuildStatsScreenText(stats, week);
        return decision with { IntentName = StatsIntent, SkillPayload = payload };
    }

    internal static string BuildStatsSpeech(JiboDayStats stats, bool week)
    {
        var period = week ? "This week" : "Today";
        if (stats.Talks == 0)
            return week ? "It's been a quiet week. Nobody has talked to me yet." : "It's been a quiet day. You're the first to talk to me.";

        var text = new StringBuilder();
        text.Append($"{period} I had {Plural(stats.Talks, "conversation")}. ");
        var ai = stats.AiAnswers + stats.AiSkills;
        if (ai > 0)
        {
            text.Append($"My AI brain helped with {ai} of them");
            text.Append(stats.AiSkills > 0 ? $", and picked one of my skills {Plural(stats.AiSkills, "time")}. " : ". ");
        }

        if (stats.BuiltIn > 0) text.Append($"I handled {stats.BuiltIn} on my own. ");
        if (stats.Photos > 0)
        {
            text.Append($"I took {Plural(stats.Photos, "photo")}");
            text.Append(stats.FacesRecognised > 0 ? $" and recognised {Plural(stats.FacesRecognised, "face")}. " : ". ");
        }

        if (stats.People.Count > 0)
        {
            var top = stats.People.MaxBy(static pair => pair.Value).Key;
            text.Append($"I talked with {top} the most. ");
        }

        if (stats.BusiestHour is { } hour) text.Append($"I was busiest around {FormatHour(hour)}.");
        return text.ToString().Trim();
    }

    internal static string BuildStatsScreenText(JiboDayStats stats, bool week)
    {
        var lines = new List<string>
        {
            week ? "This week" : "Today",
            $"Talks: {stats.Talks}",
            $"AI answers: {stats.AiAnswers}   AI skills: {stats.AiSkills}",
            $"On my own: {stats.BuiltIn}",
            $"Photos: {stats.Photos}   Faces: {stats.FacesRecognised}"
        };
        if (stats.BusiestHour is { } hour) lines.Add($"Busiest: {FormatHour(hour)}");
        return string.Join("\n", lines);
    }

    private static string Plural(int count, string word) => count == 1 ? $"1 {word}" : $"{count} {word}s";

    private static string FormatHour(int hour) => hour switch
    {
        0 => "midnight",
        12 => "noon",
        < 12 => $"{hour} AM",
        _ => $"{hour - 12} PM"
    };
}
