using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class HomeLocationTests
{
    [Fact]
    public void CoordinatesOverride_IsNullWithoutSettings()
    {
        // Pinned to "off" for tests (TestEnvironmentDefaults), which must count as not set.
        Assert.Equal("off", Environment.GetEnvironmentVariable("OPENJIBO_HOME_LATITUDE"));
        Assert.Null(JiboInteractionService.HomeCoordinatesOverride());
    }

    [Fact]
    public void TimeZoneOverride_IgnoresUnknownZone()
    {
        var previous = Environment.GetEnvironmentVariable("OPENJIBO_HOME_TIMEZONE");
        try
        {
            Environment.SetEnvironmentVariable("OPENJIBO_HOME_TIMEZONE", "Not/A_Zone");
            Assert.Null(JiboInteractionService.HomeTimeZoneOverride());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENJIBO_HOME_TIMEZONE", previous);
        }
    }
}
