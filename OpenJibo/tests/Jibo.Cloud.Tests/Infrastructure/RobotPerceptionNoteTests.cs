using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Search;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class RobotPerceptionNoteTests
{
    [Fact]
    public void Note_IsEmpty_WhenRobotRecognisedNobody()
    {
        Assert.Equal(string.Empty, ChatGptSearchProvider.BuildRobotPerceptionNote(null));
        Assert.Equal(string.Empty, ChatGptSearchProvider.BuildRobotPerceptionNote(new RobotPerceptionSnapshot(null, [])));
    }

    [Fact]
    public void Note_NamesSpeakerAndOthers_WithoutRepeatingSpeaker()
    {
        var note = ChatGptSearchProvider.BuildRobotPerceptionNote(
            new RobotPerceptionSnapshot("Makis", ["Makis", "Mary"]));

        Assert.Contains("the person talking to you is Makis", note);
        Assert.Contains("also nearby: Mary", note);
        Assert.DoesNotContain("nearby: Makis", note);
        Assert.Contains("trust the photo", note);
    }
}
