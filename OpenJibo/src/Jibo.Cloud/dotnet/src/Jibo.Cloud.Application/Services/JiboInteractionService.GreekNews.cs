using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// "Hey Jibo, read me the Greek news": reads the Greek newspapers ticked on the settings page
/// (/jibo-settings), has the AI translate the headlines into English for Jibo's English voice,
/// and shows the original Greek headlines on his face.
/// </summary>
public sealed partial class JiboInteractionService
{
    internal const string GreekNewsIntent = "greek_news";

    /// <summary>Prefix that tells the AI provider to translate headlines (one per line) to English.</summary>
    public const string TranslateHeadlinesMarker = "[[openjibo:translate-headlines]]";

    private const int MaxScreenLineLength = 42;

    private static readonly HttpClient FeedHttp = CreateFeedHttp();

    private static readonly Regex GreekNewsPattern = new(
        @"\bgreek\s+(news|newspapers?|papers?|headlines|press)\b|" +
        @"\bnews\s+(from|in|about)\s+greece\b|" +
        @"\b(greece|greek)\b.*\bheadlines\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static HttpClient CreateFeedHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (OpenJibo news reader)");
        return client;
    }

    internal static bool IsGreekNewsRequest(string lowered) =>
        !string.IsNullOrWhiteSpace(lowered) && GreekNewsPattern.IsMatch(lowered);

    private sealed record FeedHeadline(string Paper, string Title);

    private async Task<JiboInteractionDecision> BuildGreekNewsDecisionAsync(CancellationToken cancellationToken)
    {
        var settings = JiboSettings.Load();
        var feeds = settings.NewsFeeds
            .Where(f => f.Enabled && Uri.IsWellFormedUriString(f.Url, UriKind.Absolute))
            .ToList();
        if (feeds.Count == 0)
            return WithScreen(
                "I don't have any Greek newspapers picked yet. Tick some on my settings page.",
                "Ελληνικά νέα\nNo papers ticked yet");

        var perFeed = await Task.WhenAll(feeds.Select(feed => ReadFeedAsync(feed, cancellationToken)));
        var headlines = PickHeadlines(perFeed, settings.MaxHeadlines);
        logger?.LogInformation("Greek news: {Count} headline(s) from {Feeds} feed(s).", headlines.Count, feeds.Count);
        if (headlines.Count == 0)
            return WithScreen("I couldn't reach the Greek newspapers just now. Try again in a moment.",
                "Ελληνικά νέα\nNot reachable right now");

        var english = await TranslateHeadlinesAsync(headlines.Select(h => h.Title).ToList(), cancellationToken);
        var speech = english is null
            ? $"I found {headlines.Count} Greek headlines and put them on my screen, but I need my AI brain to translate them."
            : BuildGreekNewsSpeech(headlines.Select(h => h.Paper).ToList(), english);
        return WithScreen(speech, BuildGreekNewsScreen(headlines));
    }

    private static JiboInteractionDecision WithScreen(string speech, string screen)
    {
        var decision = ChitchatStateMachine.BuildKnowledgeSearchResponseDecision(speech);
        var payload = decision.SkillPayload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(decision.SkillPayload, StringComparer.OrdinalIgnoreCase);
        payload[ScreenTextPayloadKey] = screen;
        payload["screen_font_size"] = "52";
        return decision with { IntentName = GreekNewsIntent, SkillPayload = payload };
    }

    private async Task<IReadOnlyList<FeedHeadline>> ReadFeedAsync(NewsFeedSetting feed, CancellationToken cancellationToken)
    {
        try
        {
            var xml = await FeedHttp.GetStringAsync(feed.Url, cancellationToken);
            return ParseFeedTitles(xml, 3).Select(title => new FeedHeadline(feed.Name, title)).ToList();
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger?.LogWarning("Greek news: could not read {Paper} ({Error}).", feed.Name, exception.GetType().Name);
            return [];
        }
    }

    /// <summary>Item titles from an RSS or Atom feed, newest first as published.</summary>
    internal static IReadOnlyList<string> ParseFeedTitles(string xml, int max)
    {
        var document = XDocument.Parse(xml);
        return document.Descendants()
            .Where(e => e.Name.LocalName is "item" or "entry")
            .Select(e => e.Elements().FirstOrDefault(c => c.Name.LocalName == "title")?.Value)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => Regex.Replace(WebUtility.HtmlDecode(t!), @"\s+", " ").Trim())
            .Take(max)
            .ToList();
    }

    /// <summary>Takes headlines round-robin across papers so each ticked paper gets a turn.</summary>
    private static List<FeedHeadline> PickHeadlines(IReadOnlyList<IReadOnlyList<FeedHeadline>> perFeed, int max)
    {
        var picked = new List<FeedHeadline>();
        for (var round = 0; picked.Count < max && perFeed.Any(f => f.Count > round); round++)
            foreach (var feed in perFeed)
                if (feed.Count > round && picked.Count < max)
                    picked.Add(feed[round]);
        return picked;
    }

    private async Task<IReadOnlyList<string>?> TranslateHeadlinesAsync(IReadOnlyList<string> titles, CancellationToken cancellationToken)
    {
        if (knowledgeSearchService is null || !knowledgeSearchService.IsConfigured) return null;
        try
        {
            var result = await knowledgeSearchService.SearchAsync(
                TranslateHeadlinesMarker + string.Join("\n", titles), cancellationToken);
            if (result?.Outcome != KnowledgeSearchOutcome.Found || string.IsNullOrWhiteSpace(result.AnswerText)) return null;
            var lines = result.AnswerText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => Regex.Replace(line, @"^\s*(\d+[.)]|[-*•])\s*", string.Empty))
                .Where(line => line.Length > 0)
                .ToList();
            return lines.Count == 0 ? null : lines;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static string BuildGreekNewsSpeech(IReadOnlyList<string> papers, IReadOnlyList<string> english)
    {
        var parts = new List<string> { "Here are the Greek headlines." };
        string? lastPaper = null;
        for (var i = 0; i < english.Count; i++)
        {
            var paper = i < papers.Count ? papers[i] : null;
            var sentence = english[i].TrimEnd('.', ' ') + ".";
            parts.Add(paper is not null && paper != lastPaper ? $"From {paper}: {sentence}" : sentence);
            lastPaper = paper ?? lastPaper;
        }

        return string.Join(" ", parts);
    }

    private static string BuildGreekNewsScreen(IReadOnlyList<FeedHeadline> headlines)
    {
        var lines = new List<string> { "Ελληνικά νέα" };
        lines.AddRange(headlines.Select(h => h.Title.Length <= MaxScreenLineLength
            ? h.Title
            : h.Title[..MaxScreenLineLength].TrimEnd() + "…"));
        return string.Join("\n", lines);
    }
}
