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
    private static bool IsMorningHelloEnabled() =>
        !string.Equals(Environment.GetEnvironmentVariable("OPENJIBO_MORNING_HELLO")?.Trim(), "false",
            StringComparison.OrdinalIgnoreCase);

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
