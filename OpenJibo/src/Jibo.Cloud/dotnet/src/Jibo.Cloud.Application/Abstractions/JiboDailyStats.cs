using System.Text.Json;

namespace Jibo.Cloud.Application.Abstractions;

/// <summary>One day of Jibo's activity, as the server sees it.</summary>
public sealed class JiboDayStats
{
    public int Talks { get; set; }
    public int AiAnswers { get; set; }
    public int AiSkills { get; set; }
    public int Photos { get; set; }
    public int FacesRecognised { get; set; }
    public Dictionary<int, int> Hours { get; set; } = new();
    public Dictionary<string, int> People { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Talks the server answered on its own (jokes, timers, weather...) without the AI.</summary>
    public int BuiltIn => Math.Max(0, Talks - AiAnswers - AiSkills);

    public int? BusiestHour => Hours.Count == 0 ? null : Hours.MaxBy(static pair => pair.Value).Key;

    public void Add(JiboDayStats other)
    {
        Talks += other.Talks;
        AiAnswers += other.AiAnswers;
        AiSkills += other.AiSkills;
        Photos += other.Photos;
        FacesRecognised += other.FacesRecognised;
        foreach (var (hour, count) in other.Hours) Hours[hour] = Hours.GetValueOrDefault(hour) + count;
        foreach (var (name, count) in other.People) People[name] = People.GetValueOrDefault(name) + count;
    }
}

/// <summary>
/// Simple daily counters ("how many conversations, how many used the AI, photos...") kept in a
/// small JSON file on the API data volume. Days are the robot's local days.
/// OPENJIBO_STATS=false turns it off; OPENJIBO_STATS_PATH overrides the file (default /data/jibo-stats.json).
/// </summary>
public static class JiboDailyStats
{
    private const int KeepDays = 400;
    private static readonly object Gate = new();
    private static Dictionary<string, JiboDayStats>? _days;
    private static TimeSpan _lastRobotOffset = TimeZoneInfo.Local.BaseUtcOffset;

    /// <summary>Overrides the storage file (tests). Empty string means memory only.</summary>
    public static string? PathOverride { get; set; }

    private static bool Enabled =>
        !string.Equals(Environment.GetEnvironmentVariable("OPENJIBO_STATS")?.Trim(), "false",
            StringComparison.OrdinalIgnoreCase);

    private static string? StoragePath
    {
        get
        {
            if (PathOverride is not null) return PathOverride.Length == 0 ? null : PathOverride;
            var configured = Environment.GetEnvironmentVariable("OPENJIBO_STATS_PATH")?.Trim();
            if (!string.IsNullOrEmpty(configured)) return configured;
            return Directory.Exists("/data") ? "/data/jibo-stats.json" : null;
        }
    }

    public static string Key(DateOnly day) => day.ToString("yyyy-MM-dd");

    /// <summary>The robot's current local date (uses the last turn's time zone).</summary>
    public static DateOnly Today()
    {
        lock (Gate) return DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(_lastRobotOffset).DateTime);
    }

    /// <summary>A spoken turn ("Hey Jibo, ..."), at the robot's local time.</summary>
    public static void RecordTalk(DateTimeOffset robotLocalTime, string? speakerName)
    {
        Update(stats =>
        {
            stats.Talks++;
            stats.Hours[robotLocalTime.Hour] = stats.Hours.GetValueOrDefault(robotLocalTime.Hour) + 1;
            if (!string.IsNullOrWhiteSpace(speakerName))
                stats.People[speakerName] = stats.People.GetValueOrDefault(speakerName) + 1;
        }, robotLocalTime);
    }

    public static void RecordAiAnswer() => Update(static stats => stats.AiAnswers++);
    public static void RecordAiSkill() => Update(static stats => stats.AiSkills++);

    public static void RecordPhoto(int recognisedPeople) => Update(stats =>
    {
        stats.Photos++;
        stats.FacesRecognised += recognisedPeople;
    });

    public static JiboDayStats Get(DateOnly day)
    {
        lock (Gate)
        {
            var days = Load();
            var result = new JiboDayStats();
            if (days.TryGetValue(Key(day), out var stats)) result.Add(stats);
            return result;
        }
    }

    /// <summary>Totals for the last <paramref name="count"/> days, ending on <paramref name="lastDay"/>.</summary>
    public static JiboDayStats Range(DateOnly lastDay, int count)
    {
        var total = new JiboDayStats();
        for (var i = 0; i < count; i++) total.Add(Get(lastDay.AddDays(-i)));
        return total;
    }

    /// <summary>Clears memory so the next read reloads (tests).</summary>
    public static void Reset()
    {
        lock (Gate) _days = null;
    }

    private static void Update(Action<JiboDayStats> change, DateTimeOffset? robotLocalTime = null)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (robotLocalTime is { } local) _lastRobotOffset = local.Offset;
            var day = robotLocalTime is { } turnTime
                ? DateOnly.FromDateTime(turnTime.DateTime)
                : DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(_lastRobotOffset).DateTime);

            var days = Load();
            if (!days.TryGetValue(Key(day), out var stats))
            {
                stats = new JiboDayStats();
                days[Key(day)] = stats;
                var oldest = Key(day.AddDays(-KeepDays));
                foreach (var stale in days.Keys.Where(key => string.CompareOrdinal(key, oldest) < 0).ToList())
                    days.Remove(stale);
            }

            change(stats);
            Save(days);
        }
    }

    private static Dictionary<string, JiboDayStats> Load()
    {
        if (_days is not null) return _days;
        _days = new Dictionary<string, JiboDayStats>();
        var path = StoragePath;
        try
        {
            if (path is not null && File.Exists(path))
                _days = JsonSerializer.Deserialize<Dictionary<string, JiboDayStats>>(File.ReadAllText(path)) ?? _days;
        }
        catch
        {
            // A damaged stats file just starts fresh; it's only for curiosity.
        }

        return _days;
    }

    private static void Save(Dictionary<string, JiboDayStats> days)
    {
        var path = StoragePath;
        if (path is null) return;
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(days));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Never let stats break a conversation.
        }
    }
}
