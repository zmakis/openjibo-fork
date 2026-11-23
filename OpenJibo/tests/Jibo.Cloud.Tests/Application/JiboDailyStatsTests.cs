using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class JiboDailyStatsTests
{
    [Theory]
    [InlineData("show me your stats", true)]
    [InlineData("jibo's daily stats", true)]
    [InlineData("how was your day", true)]
    [InlineData("how was your week", true)]
    [InlineData("how many conversations did you have", true)]
    [InlineData("what did you do today", true)]
    [InlineData("how is your day", false)]
    [InlineData("that's your report", false)]
    [InlineData("what's the weather", false)]
    public void IsStatsRequest_MatchesStatsPhrases(string text, bool expected)
    {
        Assert.Equal(expected, JiboInteractionService.IsStatsRequest(text));
    }

    [Fact]
    public void RecordTalk_CountsByRobotLocalDayHourAndSpeaker()
    {
        // A far-past day so other tests recording "now" never collide with it.
        var local = new DateTimeOffset(2001, 3, 4, 20, 15, 0, TimeSpan.FromHours(-5));
        JiboDailyStats.RecordTalk(local, "Makis");
        JiboDailyStats.RecordTalk(local.AddMinutes(5), "Makis");

        var stats = JiboDailyStats.Get(new DateOnly(2001, 3, 4));
        Assert.Equal(2, stats.Talks);
        Assert.Equal(2, stats.Hours[20]);
        Assert.Equal(2, stats.People["Makis"]);
        Assert.Equal(20, stats.BusiestHour);
    }

    [Fact]
    public void Speech_SummarisesTheDay()
    {
        var stats = new JiboDayStats { Talks = 12, AiAnswers = 7, AiSkills = 2, Photos = 3, FacesRecognised = 2 };
        stats.Hours[20] = 5;
        stats.People["Makis"] = 9;

        var speech = JiboInteractionService.BuildStatsSpeech(stats, week: false);

        Assert.Contains("Today I had 12 conversations.", speech);
        Assert.Contains("My AI brain helped with 9 of them, and picked one of my skills 2 times.", speech);
        Assert.Contains("I handled 3 on my own.", speech);
        Assert.Contains("I took 3 photos and recognised 2 faces.", speech);
        Assert.Contains("I talked with Makis the most.", speech);
        Assert.Contains("busiest around 8 PM", speech);
    }

    [Fact]
    public void Speech_QuietDay()
    {
        Assert.Contains("quiet day", JiboInteractionService.BuildStatsSpeech(new JiboDayStats(), week: false));
    }

    [Fact]
    public void ScreenText_ListsTheCounts()
    {
        var stats = new JiboDayStats { Talks = 4, AiAnswers = 3, Photos = 1 };
        var screen = JiboInteractionService.BuildStatsScreenText(stats, week: true);

        Assert.StartsWith("This week\n", screen);
        Assert.Contains("Talks: 4", screen);
        Assert.Contains("AI answers: 3   AI skills: 0", screen);
        Assert.Contains("On my own: 1", screen);
        Assert.Contains("Photos: 1   Faces: 0", screen);
    }
}
