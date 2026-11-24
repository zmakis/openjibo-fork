using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Morning hello: the first time each day Jibo greets someone he recognises, the usual
/// proactive greeting also gets today's home weather and a fun fact.
/// OPENJIBO_MORNING_HELLO=false turns the extras off.
/// </summary>
public sealed partial class JiboInteractionService
{
    private static bool IsMorningHelloEnabled()
    {
        var value = Environment.GetEnvironmentVariable("OPENJIBO_MORNING_HELLO")?.Trim();
        return !(string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(value, "off", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly HashSet<string> PlainSpeechPayloadKeys =
        new(StringComparer.OrdinalIgnoreCase) { "cloudSkill", ScreenTextPayloadKey, "screen_font_size" };

    /// <summary>
    /// Jibo's own "I see you" greetings mostly stay on the robot, so the morning hello rides on
    /// the first answer of the day instead: "Good morning, Makis! Right now it's 3 degrees..." and then
    /// the answer. Only for plain spoken answers (not weather cards, games or skill launches).
    /// </summary>
    private async Task<JiboInteractionDecision> AddFirstAnswerOfDayHelloAsync(
        TurnContext turn, JiboInteractionDecision decision, CancellationToken cancellationToken)
    {
        if (!IsMorningHelloEnabled() || string.IsNullOrWhiteSpace(decision.ReplyText) ||
            decision.ReplyText.TrimStart().StartsWith('<')) return decision;
        if (decision.SkillPayload is { } payload && payload.Keys.Any(key => !PlainSpeechPayloadKeys.Contains(key)))
            return decision;
        if (decision.IntentName is GreekNewsIntent or StatsIntent or HouseReportIntent) return decision;

        var messageType = turn.Attributes.TryGetValue("messageType", out var rawType) ? rawType?.ToString() : null;
        if (string.Equals(messageType, "TRIGGER", StringComparison.OrdinalIgnoreCase)) return decision;
        if (string.IsNullOrWhiteSpace(turn.NormalizedTranscript ?? turn.RawTranscript)) return decision;

        var local = TryResolveReferenceLocalTime(turn) ?? DateTimeOffset.Now;
        if (!JiboDailyStats.TryClaimMorningHello(local)) return decision;

        var name = BuildRobotPerceptionSnapshot(ResolveGreetingPresenceProfile(turn))?.SpeakerName;
        var hello = BuildFirstAnswerHello(local.Hour, name);
        var weather = await TryGetHomeWeatherSentenceAsync(turn, cancellationToken);
        var prefix = string.IsNullOrWhiteSpace(weather) ? hello : hello + " " + weather;
        logger?.LogInformation("Morning hello: added to the first answer of the day ({Intent}).", decision.IntentName);
        return decision with { ReplyText = prefix + " " + decision.ReplyText.TrimStart() };
    }

    internal static string BuildFirstAnswerHello(int localHour, string? name)
    {
        var greeting = localHour switch
        {
            >= 4 and < 12 => "Good morning",
            >= 12 and < 17 => "Good afternoon",
            _ => "Good evening"
        };
        return string.IsNullOrWhiteSpace(name) ? greeting + "!" : $"{greeting}, {name}!";
    }

    /// <summary>True when this person hasn't been greeted yet today (robot-local day).</summary>
    internal static bool IsFirstGreetingOfDay(DateTimeOffset? lastGreetedUtc, DateTimeOffset localNow) =>
        lastGreetedUtc is not { } last ||
        DateOnly.FromDateTime(last.ToOffset(localNow.Offset).DateTime) < DateOnly.FromDateTime(localNow.DateTime);

    private async Task<JiboInteractionDecision> AddMorningHelloAsync(
        JiboInteractionDecision greeting,
        TurnContext turn,
        CancellationToken cancellationToken)
    {
        var extras = new List<string>();

        var weather = await TryGetHomeWeatherSentenceAsync(turn, cancellationToken);
        if (!string.IsNullOrWhiteSpace(weather)) extras.Add(weather);

        if (funFactProvider is not null)
        {
            try
            {
                var fact = await funFactProvider.GetRandomFactAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(fact)) extras.Add("Here's a fun fact to start your day. " + fact.Trim());
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // No fact today; the greeting still plays.
            }
        }

        if (extras.Count == 0) return greeting;
        logger?.LogInformation("Morning hello: added {Count} extra(s) to the first greeting of the day.", extras.Count);
        return greeting with { ReplyText = greeting.ReplyText.TrimEnd() + " " + string.Join(" ", extras) };
    }

    private async Task<string?> TryGetHomeWeatherSentenceAsync(TurnContext turn, CancellationToken cancellationToken)
    {
        if (weatherReportProvider is null) return null;
        try
        {
            var coordinates = TryResolveWeatherCoordinates(turn);
            var snapshot = await weatherReportProvider.GetReportAsync(
                new WeatherReportRequest(
                    null,
                    coordinates?.Latitude,
                    coordinates?.Longitude,
                    false,
                    ShouldUseCelsius(turn, string.Empty),
                    0,
                    TryResolveCurrentLocationName(turn)),
                cancellationToken);
            return snapshot is null ? null : BuildMorningWeatherSentence(snapshot);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static string BuildMorningWeatherSentence(WeatherReportSnapshot snapshot)
    {
        var condition = string.IsNullOrWhiteSpace(snapshot.Condition) ? null : snapshot.Condition.Trim().ToLowerInvariant();
        var text = condition is null
            ? $"Right now it's {snapshot.Temperature} degrees outside"
            : $"Right now it's {snapshot.Temperature} degrees with {condition}";
        if (snapshot.HighTemperature is { } high) text += $", with a high of {high} today";
        return text + ".";
    }
}
