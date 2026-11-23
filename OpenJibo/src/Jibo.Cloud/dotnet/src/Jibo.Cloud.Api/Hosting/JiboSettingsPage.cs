using System.Net;
using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>
/// Home settings page for Jibo: http://&lt;vm-ip&gt;:8080/jibo-settings
/// Tick which Greek newspapers Jibo reads. Protected with a password
/// (OPENJIBO_SETTINGS_PASSWORD in .env, user name "jibo"); the page is off when no password is set.
/// </summary>
internal static class JiboSettingsPage
{
    private const string Path = "/jibo-settings";

    internal static void MapJiboSettingsPage(this WebApplication app)
    {
        app.MapGet(Path, (HttpContext context) =>
        {
            if (!Authorize(context, out var denied)) return denied;
            return Results.Content(Render(JiboSettings.Load(), saved: context.Request.Query.ContainsKey("saved")),
                "text/html; charset=utf-8");
        });

        app.MapPost(Path, async (HttpContext context) =>
        {
            if (!Authorize(context, out var denied)) return denied;
            var form = await context.Request.ReadFormAsync();
            var data = JiboSettings.Load();

            var enabled = form["feed"].ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var feed in data.NewsFeeds) feed.Enabled = enabled.Contains(feed.Url);

            var removed = form["remove"].ToHashSet(StringComparer.OrdinalIgnoreCase);
            var builtIn = JiboSettings.DefaultGreekFeeds().Select(f => f.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
            data.NewsFeeds.RemoveAll(f => removed.Contains(f.Url) && !builtIn.Contains(f.Url));

            var newName = form["newName"].ToString().Trim();
            var newUrl = form["newUrl"].ToString().Trim();
            if (newName.Length > 0 && Uri.TryCreate(newUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !data.NewsFeeds.Any(f => string.Equals(f.Url, newUrl, StringComparison.OrdinalIgnoreCase)))
                data.NewsFeeds.Add(new NewsFeedSetting { Name = newName[..Math.Min(newName.Length, 60)], Url = newUrl, Enabled = true });

            if (int.TryParse(form["maxHeadlines"], out var max)) data.MaxHeadlines = Math.Clamp(max, 1, 10);

            return JiboSettings.Save(data)
                ? Results.Redirect(Path + "?saved=1")
                : Results.Content("Could not save the settings file.", "text/plain", statusCode: 500);
        });
    }

    private static bool Authorize(HttpContext context, out IResult denied)
    {
        denied = Results.NotFound();
        var password = Environment.GetEnvironmentVariable("OPENJIBO_SETTINGS_PASSWORD");
        if (string.IsNullOrEmpty(password))
        {
            denied = Results.Content("The Jibo settings page is off. Set OPENJIBO_SETTINGS_PASSWORD in .env to turn it on.",
                "text/plain", statusCode: 404);
            return false;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon >= 0 && FixedTimeEquals(decoded[(colon + 1)..], password)) return true;
            }
            catch (FormatException)
            {
                // Fall through to the challenge.
            }
        }

        context.Response.Headers.WWWAuthenticate = "Basic realm=\"Jibo settings\", charset=\"UTF-8\"";
        denied = Results.Unauthorized();
        return false;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static string H(string value) => WebUtility.HtmlEncode(value);

    internal static string Render(JiboSettingsData data, bool saved)
    {
        var builtIn = JiboSettings.DefaultGreekFeeds().Select(f => f.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new StringBuilder();
        foreach (var feed in data.NewsFeeds)
        {
            var removable = builtIn.Contains(feed.Url)
                ? string.Empty
                : $"<label class=\"rm\"><input type=\"checkbox\" name=\"remove\" value=\"{H(feed.Url)}\"> remove</label>";
            rows.Append($"""
                <div class="feed">
                  <label><input type="checkbox" name="feed" value="{H(feed.Url)}"{(feed.Enabled ? " checked" : "")}> {H(feed.Name)}</label>
                  <span class="url">{H(feed.Url)}</span>{removable}
                </div>
                """);
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Jibo settings</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 640px; margin: 24px auto; padding: 0 16px; color: #1d2433; background: #f6f7fb; }
              h1 { font-size: 1.6rem; margin-bottom: 4px; } h2 { font-size: 1.15rem; margin-top: 28px; }
              .card { background: #fff; border-radius: 12px; padding: 16px 18px; box-shadow: 0 1px 3px rgba(0,0,0,.08); }
              .feed { padding: 10px 0; border-bottom: 1px solid #eef0f4; } .feed:last-child { border-bottom: 0; }
              .feed label { font-size: 1.05rem; cursor: pointer; } input[type=checkbox] { transform: scale(1.3); margin-right: 8px; }
              .url { display: block; color: #8a91a3; font-size: .8rem; margin-left: 30px; word-break: break-all; }
              .rm { font-size: .85rem !important; color: #b4483c; margin-left: 30px; }
              input[type=text], input[type=url], input[type=number] { width: 100%; box-sizing: border-box; padding: 8px; margin: 4px 0 10px; border: 1px solid #d5d9e2; border-radius: 8px; font-size: 1rem; }
              button { background: #2f6fed; color: #fff; border: 0; border-radius: 10px; padding: 12px 22px; font-size: 1.05rem; margin-top: 18px; cursor: pointer; }
              .saved { background: #e5f6ea; color: #1d6b35; padding: 10px 14px; border-radius: 10px; margin: 12px 0; }
              .hint { color: #5d6476; font-size: .9rem; }
            </style></head><body>
            <h1>Jibo settings</h1>
            <p class="hint">Changes apply from Jibo's next question. No restart needed.</p>
            {{(saved ? "<div class=\"saved\">Saved.</div>" : "")}}
            <form method="post">
              <h2>Greek newspapers</h2>
              <p class="hint">Say "Hey Jibo, read me the Greek news". He reads the headlines in English and shows the Greek ones on his face.</p>
              <div class="card">{{rows}}</div>
              <h2>Add another feed</h2>
              <div class="card">
                <label>Name<input type="text" name="newName" placeholder="e.g. Kathimerini"></label>
                <label>RSS feed address<input type="url" name="newUrl" placeholder="https://..."></label>
              </div>
              <h2>How many headlines</h2>
              <div class="card"><input type="number" name="maxHeadlines" min="1" max="10" value="{{data.MaxHeadlines}}"></div>
              <button type="submit">Save</button>
            </form>
            </body></html>
            """;
    }
}
