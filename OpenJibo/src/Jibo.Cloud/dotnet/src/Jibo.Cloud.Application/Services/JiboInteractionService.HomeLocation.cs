using System.Globalization;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Home location and time zone overrides, for robots that report the wrong place
/// (e.g. one first set up in another country). Set in .env:
///   OPENJIBO_HOME_LATITUDE / OPENJIBO_HOME_LONGITUDE  -> home weather coordinates
///   OPENJIBO_HOME_LOCATION_NAME                       -> name Jibo says ("Toronto")
///   OPENJIBO_HOME_TIMEZONE                            -> IANA zone for local time ("America/Toronto")
/// Anything not set falls back to what the robot reports.
/// </summary>
public sealed partial class JiboInteractionService
{
    private static (double Latitude, double Longitude)? TryResolveWeatherCoordinates(TurnContext turn) =>
        HomeCoordinatesOverride() ?? TryResolveWeatherCoordinatesFromRobot(turn);

    private static string? TryResolveCurrentLocationName(TurnContext turn)
    {
        var configured = Environment.GetEnvironmentVariable("OPENJIBO_HOME_LOCATION_NAME")?.Trim();
        return string.IsNullOrEmpty(configured) || IsOffValue(configured)
            ? TryResolveCurrentLocationNameFromRobot(turn)
            : configured;
    }

    private static DateTimeOffset? TryResolveReferenceLocalTime(TurnContext turn)
    {
        var zone = HomeTimeZoneOverride();
        if (zone is null) return TryResolveReferenceLocalTimeFromRobot(turn);
        // The robot's clock instant is right even when its zone is wrong; only the offset changes.
        var instant = TryResolveReferenceLocalTimeFromRobot(turn) ?? DateTimeOffset.UtcNow;
        return TimeZoneInfo.ConvertTime(instant, zone);
    }

    internal static (double Latitude, double Longitude)? HomeCoordinatesOverride()
    {
        var latText = Environment.GetEnvironmentVariable("OPENJIBO_HOME_LATITUDE")?.Trim();
        var lonText = Environment.GetEnvironmentVariable("OPENJIBO_HOME_LONGITUDE")?.Trim();
        return double.TryParse(latText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
               double.TryParse(lonText, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
               lat is >= -90 and <= 90 && lon is >= -180 and <= 180
            ? (lat, lon)
            : null;
    }

    internal static TimeZoneInfo? HomeTimeZoneOverride()
    {
        var id = Environment.GetEnvironmentVariable("OPENJIBO_HOME_TIMEZONE")?.Trim();
        if (string.IsNullOrEmpty(id) || IsOffValue(id)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static bool IsOffValue(string value) =>
        value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("false", StringComparison.OrdinalIgnoreCase);
}
