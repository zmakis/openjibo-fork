using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class HouseReportTests
{
    [Theory]
    [InlineData("house report", true)]
    [InlineData("give me the home status", true)]
    [InlineData("how's the house", true)]
    [InlineData("which lights are on", true)]
    [InlineData("are any lights still on", true)]
    [InlineData("turn on the kitchen lights", false)]
    [InlineData("turn the lights off", false)]
    public void IsHouseReportRequest_MatchesReportPhrases(string text, bool expected)
    {
        Assert.Equal(expected, JiboInteractionService.IsHouseReportRequest(text));
    }

    [Fact]
    public void ReadLights_CountsRealLightsAndSkipsGroupsAndUnavailable()
    {
        const string json = """
        [
          {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen"}},
          {"entity_id":"light.porch","state":"on","attributes":{}},
          {"entity_id":"light.bedroom","state":"off","attributes":{"friendly_name":"Bedroom"}},
          {"entity_id":"light.garage","state":"unavailable","attributes":{"friendly_name":"Garage"}},
          {"entity_id":"light.downstairs","state":"on","attributes":{"friendly_name":"Downstairs","entity_id":["light.kitchen"]}},
          {"entity_id":"switch.fan","state":"on","attributes":{"friendly_name":"Fan"}}
        ]
        """;

        var (on, total) = JiboInteractionService.ReadLights(json);

        Assert.Equal(3, total);
        Assert.Equal(new[] { "Kitchen", "porch" }, on);
    }

    [Fact]
    public void Speech_And_Screen_DescribeLights()
    {
        var on = new[] { "Kitchen", "Living Room", "Porch" };
        Assert.Equal("3 of 12 lights are on: Kitchen, Living Room and Porch.",
            JiboInteractionService.BuildHouseReportSpeech(on, 12));
        Assert.Equal("All 12 lights in the house are off.", JiboInteractionService.BuildHouseReportSpeech([], 12));
        Assert.Equal("House\nLights on: 3 / 12\nKitchen\nLiving Room\nPorch",
            JiboInteractionService.BuildHouseReportScreen(on, 12));
    }
}
