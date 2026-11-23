using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class GreekNewsTests
{
    [Theory]
    [InlineData("read me the greek news", true)]
    [InlineData("what are the greek headlines", true)]
    [InlineData("any news from greece", true)]
    [InlineData("tell me the news", false)]
    [InlineData("speak greek", false)]
    public void IsGreekNewsRequest_MatchesGreekNewsPhrases(string text, bool expected)
    {
        Assert.Equal(expected, JiboInteractionService.IsGreekNewsRequest(text));
    }

    [Fact]
    public void ParseFeedTitles_ReadsRssAndAtom()
    {
        const string rss = """
            <rss version="2.0"><channel><title>ΤΑ ΝΕΑ</title>
              <item><title>Πρώτη   είδηση &amp; κάτι</title></item>
              <item><title><![CDATA[Δεύτερη είδηση]]></title></item>
              <item><title>Τρίτη</title></item>
            </channel></rss>
            """;
        Assert.Equal(new[] { "Πρώτη είδηση & κάτι", "Δεύτερη είδηση" }, JiboInteractionService.ParseFeedTitles(rss, 2));

        const string atom = """
            <feed xmlns="http://www.w3.org/2005/Atom"><title>Feed</title>
              <entry><title>Atom είδηση</title></entry>
            </feed>
            """;
        Assert.Equal(new[] { "Atom είδηση" }, JiboInteractionService.ParseFeedTitles(atom, 5));
    }

    [Fact]
    public void Speech_NamesEachPaperOnce()
    {
        var speech = JiboInteractionService.BuildGreekNewsSpeech(
            ["ERT News", "ERT News", "Ta Nea"],
            ["Storm hits Crete", "Ferries cancelled.", "Prices rise"]);

        Assert.Equal(
            "Here are the Greek headlines. From ERT News: Storm hits Crete. Ferries cancelled. From Ta Nea: Prices rise.",
            speech);
    }

    [Fact]
    public void Settings_DefaultsTickErtNewsAndTaNea()
    {
        JiboSettings.PathOverride = string.Empty;
        try
        {
            var enabled = JiboSettings.EnabledFeeds().Select(f => f.Name).ToList();
            Assert.Contains("ERT News", enabled);
            Assert.Contains("Ta Nea", enabled);
            Assert.DoesNotContain("Avgi", enabled);
        }
        finally
        {
            JiboSettings.PathOverride = null;
        }
    }
}
