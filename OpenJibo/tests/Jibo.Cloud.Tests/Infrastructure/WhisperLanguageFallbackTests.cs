using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class WhisperLanguageFallbackTests
{
    [Theory]
    [InlineData("english", "en,el", true)]
    [InlineData("greek", "en,el", true)]
    [InlineData("en", "en,el", true)]
    [InlineData("korean", "en,el", false)]
    [InlineData("spanish", "en,el", false)]
    [InlineData("polish", "en", false)]
    [InlineData("korean", "any", true)]
    [InlineData("Greek", " EN , EL ", true)]
    public void IsAllowedLanguage_ChecksCodesAndNames(string detected, string setting, bool expected)
    {
        Assert.Equal(expected, WhisperServerBufferedAudioSttStrategy.IsAllowedLanguage(detected, setting));
    }

    [Theory]
    [InlineData("{\"task\":\"transcribe\",\"language\":\"korean\",\"text\":\" hi\"}", "korean")]
    [InlineData("{\"text\":\" hi\"}", null)]
    [InlineData("[00:00.000 --> 00:01.000] hi", null)]
    [InlineData("", null)]
    public void ExtractDetectedLanguage_ReadsVerboseJson(string raw, string? expected)
    {
        Assert.Equal(expected, WhisperServerBufferedAudioSttStrategy.ExtractDetectedLanguage(raw));
    }
}
