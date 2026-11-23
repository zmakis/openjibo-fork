using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class MorningHelloTests
{
    private static readonly TimeSpan Toronto = TimeSpan.FromHours(-5);

    [Fact]
    public void FirstGreetingOfDay_WhenNeverGreeted()
    {
        Assert.True(JiboInteractionService.IsFirstGreetingOfDay(null, new DateTimeOffset(2026, 11, 23, 8, 0, 0, Toronto)));
    }

    [Fact]
    public void FirstGreetingOfDay_UsesRobotLocalDate()
    {
        var now = new DateTimeOffset(2026, 11, 23, 8, 0, 0, Toronto);
        // 03:00 UTC on Nov 23 is still Nov 22 in Toronto: yesterday.
        Assert.True(JiboInteractionService.IsFirstGreetingOfDay(new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero), now));
        // 12:00 UTC on Nov 23 is 7 AM in Toronto: already greeted today.
        Assert.False(JiboInteractionService.IsFirstGreetingOfDay(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero), now));
    }

    [Fact]
    public void WeatherSentence_UsesTemperatureConditionAndHigh()
    {
        var snapshot = new WeatherReportSnapshot("Toronto", "cloudy", 3, 6, -1, "Light Snow", true);
        Assert.Equal("Right now it's 3 degrees with light snow, with a high of 6 today.",
            JiboInteractionService.BuildMorningWeatherSentence(snapshot));
    }
}
