using System.Runtime.CompilerServices;

namespace Jibo.Cloud.Tests;

/// <summary>
/// The API loads the repo's .env into the test process (OpenJiboEnvLoader only fills empty
/// variables). Pin home-specific overrides to "off" first, so a real home .env (home city,
/// time zone) can't change test results.
/// </summary>
internal static class TestEnvironmentDefaults
{
    private static readonly string[] HomeOnlySettings =
    [
        "OPENJIBO_HOME_LATITUDE",
        "OPENJIBO_HOME_LONGITUDE",
        "OPENJIBO_HOME_LOCATION_NAME",
        "OPENJIBO_HOME_TIMEZONE",
        "OPENJIBO_HA_URL",
        "OPENJIBO_HA_TOKEN"
    ];

    [ModuleInitializer]
    internal static void PinHomeSettingsOff()
    {
        foreach (var name in HomeOnlySettings)
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                Environment.SetEnvironmentVariable(name, "off");
    }
}
