using System.Text.Json;

namespace Jibo.Cloud.Application.Abstractions;

/// <summary>One newspaper feed Jibo can read.</summary>
public sealed class NewsFeedSetting
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; }
}

/// <summary>Settings changed from the home settings page (/jibo-settings).</summary>
public sealed class JiboSettingsData
{
    public List<NewsFeedSetting> NewsFeeds { get; set; } = new();
    public int MaxHeadlines { get; set; } = 5;
    /// <summary>Show the original Greek headlines on Jibo's face while he reads them.</summary>
    public bool ShowGreekOnScreen { get; set; } = true;
}

/// <summary>
/// Small JSON settings file on the API data volume (default /data/jibo-settings.json,
/// OPENJIBO_SETTINGS_PATH to override). Read on every use, so changes apply immediately.
/// </summary>
public static class JiboSettings
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Greek news feeds offered on the settings page.</summary>
    public static IReadOnlyList<NewsFeedSetting> DefaultGreekFeeds() =>
    [
        new() { Name = "ERT News", Url = "https://www.ertnews.gr/feed/", Enabled = true },
        new() { Name = "Ta Nea", Url = "https://www.tanea.gr/feed", Enabled = true },
        new() { Name = "To Vima", Url = "https://www.tovima.gr/feed" },
        new() { Name = "Proto Thema", Url = "https://www.protothema.gr/rss/news/general/" },
        new() { Name = "ERT", Url = "https://www.ert.gr/feed/" },
        new() { Name = "Oikonomikos Taxydromos (business)", Url = "https://www.ot.gr/feed/" },
        new() { Name = "Dimokratia", Url = "https://www.dimokratia.gr/feed" },
        new() { Name = "Avgi", Url = "https://www.avgi.gr/rss.xml" },
        new() { Name = "Documento", Url = "https://www.documentonews.gr/feed" }
    ];

    /// <summary>Overrides the file location (tests). Empty string means memory only.</summary>
    public static string? PathOverride { get; set; }

    private static JiboSettingsData? _memoryOnly;

    private static string? StoragePath
    {
        get
        {
            if (PathOverride is not null) return PathOverride.Length == 0 ? null : PathOverride;
            var configured = Environment.GetEnvironmentVariable("OPENJIBO_SETTINGS_PATH")?.Trim();
            if (!string.IsNullOrEmpty(configured)) return configured;
            return Directory.Exists("/data") ? "/data/jibo-settings.json" : null;
        }
    }

    public static JiboSettingsData Load()
    {
        lock (Gate)
        {
            JiboSettingsData? data = null;
            var path = StoragePath;
            try
            {
                if (path is not null && File.Exists(path))
                    data = JsonSerializer.Deserialize<JiboSettingsData>(File.ReadAllText(path));
                else if (path is null)
                    data = _memoryOnly;
            }
            catch
            {
                // Damaged file: fall back to defaults.
            }

            data ??= new JiboSettingsData { NewsFeeds = DefaultGreekFeeds().ToList() };
            // New built-in papers show up (unticked) even in older settings files.
            foreach (var feed in DefaultGreekFeeds())
                if (!data.NewsFeeds.Any(f => string.Equals(f.Url, feed.Url, StringComparison.OrdinalIgnoreCase)))
                    data.NewsFeeds.Add(new NewsFeedSetting { Name = feed.Name, Url = feed.Url, Enabled = false });
            data.MaxHeadlines = Math.Clamp(data.MaxHeadlines, 1, 10);
            return data;
        }
    }

    public static bool Save(JiboSettingsData data)
    {
        lock (Gate)
        {
            var path = StoragePath;
            if (path is null)
            {
                _memoryOnly = data;
                return true;
            }

            try
            {
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<NewsFeedSetting> EnabledFeeds() =>
        Load().NewsFeeds.Where(f => f.Enabled && Uri.IsWellFormedUriString(f.Url, UriKind.Absolute)).ToList();
}
