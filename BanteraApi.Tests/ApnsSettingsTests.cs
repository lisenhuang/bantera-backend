using BanteraApi.Chat;
using Xunit;

namespace BanteraApi.Tests;

public class ApnsSettingsTests
{
    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData(ApnsSettings.EnvironmentAuto, true, true)]
    [InlineData(ApnsSettings.EnvironmentAuto, false, false)]
    [InlineData(ApnsSettings.EnvironmentSandbox, true, true)]
    [InlineData(ApnsSettings.EnvironmentSandbox, false, false)]
    [InlineData(ApnsSettings.EnvironmentProduction, true, true)]
    [InlineData(ApnsSettings.EnvironmentProduction, false, false)]
    public void EffectiveSandbox_UsesTokenFlagAndIgnoresEnvironment(
        string? configuredEnvironment,
        bool tokenIsSandbox,
        bool expected)
    {
        var settings = new ApnsSettings
        {
            Environment = configuredEnvironment,
        };

        Assert.Equal(expected, settings.EffectiveSandbox(tokenIsSandbox));
    }
}
