using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// "Hey Jibo, house report" / "which lights are on?": reads Home Assistant's states with a
/// long-lived token (OPENJIBO_HA_URL + OPENJIBO_HA_TOKEN in .env), then speaks the lights that
/// are on and shows them as text on Jibo's face. Read-only: it never changes anything in the house.
/// </summary>
public sealed partial class JiboInteractionService
{
    internal const string HouseReportIntent = "house_report";
    private const int MaxNamesOnScreen = 6;

    private static readonly HttpClient HomeAssistantHttp = new() { Timeout = TimeSpan.FromSeconds(6) };

    private static readonly Regex HouseReportPattern = new(
        @"\b(house|home)\s+(report|status|update)\b|" +
        @"\bhow('?s|\s+is)\s+(the\s+)?(house|home)\b|" +
        @"\b(which|what)\s+lights\s+(are\s+)?on\b|" +
        @"\bare\s+(any|the)\s+lights\s+(still\s+)?on\b|" +
        @"\blights?\s+report\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsHouseReportRequest(string lowered) =>
        !string.IsNullOrWhiteSpace(lowered) && HouseReportPattern.IsMatch(lowered);

    private static (Uri BaseUri, string Token)? HomeAssistantReadConfig()
    {
        var url = Environment.GetEnvironmentVariable("OPENJIBO_HA_URL")?.Trim().TrimEnd('/');
        var token = Environment.GetEnvironmentVariable("OPENJIBO_HA_TOKEN")?.Trim();
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) return null;
        return Uri.TryCreate(url + "/", UriKind.Absolute, out var uri) ? (uri, token) : null;
    }

    private static bool IsHouseReportConfigured() => HomeAssistantReadConfig() is not null;

    private async Task<JiboInteractionDecision> BuildHouseReportDecisionAsync(CancellationToken cancellationToken)
    {
        var config = HomeAssistantReadConfig();
        string speech;
        string screen;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(config!.Value.BaseUri, "api/states"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Value.Token);
            using var response = await HomeAssistantHttp.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("House report: Home Assistant answered {Status}.", (int)response.StatusCode);
                speech = "I couldn't get the house report. Home Assistant didn't let me in.";
                screen = "House\nHome Assistant\nnot reachable";
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var lights = ReadLights(json);
                logger?.LogInformation("House report: {On} of {Total} lights on.", lights.On.Count, lights.Total);
                speech = BuildHouseReportSpeech(lights.On, lights.Total);
                screen = BuildHouseReportScreen(lights.On, lights.Total);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("House report: could not reach Home Assistant ({Error}).", exception.GetType().Name);
            speech = "I couldn't reach Home Assistant just now. Try again in a moment.";
            screen = "House\nHome Assistant\nnot reachable";
        }

        var decision = ChitchatStateMachine.BuildKnowledgeSearchResponseDecision(speech);
        var payload = decision.SkillPayload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(decision.SkillPayload, StringComparer.OrdinalIgnoreCase);
        payload[ScreenTextPayloadKey] = screen;
        return decision with { IntentName = HouseReportIntent, SkillPayload = payload };
    }

    /// <summary>Lights that are on (friendly names) and how many lights there are, from /api/states.</summary>
    internal static (IReadOnlyList<string> On, int Total) ReadLights(string statesJson)
    {
        var on = new List<string>();
        var total = 0;
        using var document = JsonDocument.Parse(statesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return (on, 0);

        foreach (var entity in document.RootElement.EnumerateArray())
        {
            var id = entity.TryGetProperty("entity_id", out var idElement) ? idElement.GetString() : null;
            if (id is null || !id.StartsWith("light.", StringComparison.OrdinalIgnoreCase)) continue;
            var state = entity.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            if (state is "unavailable" or "unknown") continue;
            // Light groups would count their members twice.
            if (entity.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("entity_id", out var members) && members.ValueKind == JsonValueKind.Array)
                continue;

            total++;
            if (!string.Equals(state, "on", StringComparison.OrdinalIgnoreCase)) continue;
            var name = attributes.ValueKind == JsonValueKind.Object &&
                       attributes.TryGetProperty("friendly_name", out var friendly) &&
                       friendly.ValueKind == JsonValueKind.String
                ? friendly.GetString()
                : null;
            on.Add(string.IsNullOrWhiteSpace(name) ? id["light.".Length..].Replace('_', ' ') : name.Trim());
        }

        on.Sort(StringComparer.OrdinalIgnoreCase);
        return (on, total);
    }

    internal static string BuildHouseReportSpeech(IReadOnlyList<string> on, int total)
    {
        if (total == 0) return "I can't see any lights in Home Assistant.";
        if (on.Count == 0) return $"All {total} lights in the house are off.";
        if (on.Count == 1) return $"Only one light is on: {on[0]}. The other {total - 1} are off.";
        var names = on.Count <= MaxNamesOnScreen
            ? string.Join(", ", on.Take(on.Count - 1)) + " and " + on[^1]
            : string.Join(", ", on.Take(MaxNamesOnScreen)) + $" and {on.Count - MaxNamesOnScreen} more";
        return $"{on.Count} of {total} lights are on: {names}.";
    }

    internal static string BuildHouseReportScreen(IReadOnlyList<string> on, int total)
    {
        var lines = new List<string> { "House", $"Lights on: {on.Count} / {total}" };
        lines.AddRange(on.Take(MaxNamesOnScreen));
        if (on.Count > MaxNamesOnScreen) lines.Add($"+{on.Count - MaxNamesOnScreen} more");
        return string.Join("\n", lines);
    }
}
