using Jibo.Cloud.Infrastructure.Search;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class JiboKnowledgeSearchPromptsTests
{
    [Fact]
    public void BuildRuntimeContext_IncludesUtcTime_WhenNoLocalZone()
    {
        var context = JiboKnowledgeSearchPrompts.BuildRuntimeContext(
            new DateTimeOffset(2026, 11, 14, 4, 20, 0, TimeSpan.Zero),
            null);

        Assert.Contains("Saturday, November 14, 2026", context, StringComparison.Ordinal);
        Assert.Contains("04:20 UTC", context, StringComparison.Ordinal);
        Assert.Contains("time zones", context, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRuntimeContext_UsesLocalZone_WhenAvailable()
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        }
        catch (TimeZoneNotFoundException)
        {
            return; // tzdata not installed in this environment
        }

        var context = JiboKnowledgeSearchPrompts.BuildRuntimeContext(
            new DateTimeOffset(2026, 11, 14, 4, 20, 0, TimeSpan.Zero),
            zone);

        Assert.Contains("Friday, November 13, 2026, 11:20 PM", context, StringComparison.Ordinal);
        Assert.Contains("America/Toronto", context, StringComparison.Ordinal);
        Assert.Contains("04:20 UTC", context, StringComparison.Ordinal);
    }
}
