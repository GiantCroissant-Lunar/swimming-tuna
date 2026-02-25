using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SandboxSpectrumIntegrationTests
{
    [Theory]
    [InlineData("host", SandboxLevel.BareCli)]
    [InlineData("docker", SandboxLevel.Container)]
    [InlineData("apple-container", SandboxLevel.Container)]
    public void ParseAndEnforce_EndToEnd(string mode, SandboxLevel expectedLevel)
    {
        var parsed = SandboxCommandBuilder.ParseLevel(mode);
        Assert.Equal(expectedLevel, parsed);

        var enforcer = new SandboxLevelEnforcer(containerAvailable: true);
        var canEnforce = enforcer.CanEnforce(parsed);

        if (parsed == SandboxLevel.OsSandboxed)
        {
            var isUnix = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
            Assert.Equal(isUnix, canEnforce);
        }
        else
        {
            Assert.True(canEnforce);
        }
    }

    [Fact]
    public void FullSpectrum_RecommendThenEnforce()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: true);

        var oauthAgent = new SandboxRequirements { NeedsOAuth = true };
        var oauthLevel = SandboxSpectrumPolicy.RecommendLevel(oauthAgent);
        Assert.Equal(SandboxLevel.BareCli, oauthLevel);
        Assert.True(enforcer.CanEnforce(oauthLevel));

        var networkAgent = new SandboxRequirements { NeedsNetwork = ["api.github.com"] };
        var networkLevel = SandboxSpectrumPolicy.RecommendLevel(networkAgent);
        Assert.Equal(SandboxLevel.OsSandboxed, networkLevel);

        var plainAgent = new SandboxRequirements();
        var plainLevel = SandboxSpectrumPolicy.RecommendLevel(plainAgent);
        Assert.Equal(SandboxLevel.Container, plainLevel);
        Assert.True(enforcer.CanEnforce(plainLevel));
    }

    [Fact]
    public void Fallback_WhenContainerUnavailable()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);

        var plainAgent = new SandboxRequirements();
        var recommendedLevel = SandboxSpectrumPolicy.RecommendLevel(plainAgent);
        Assert.Equal(SandboxLevel.Container, recommendedLevel);

        var effectiveLevel = enforcer.GetEffectiveLevel(recommendedLevel);
        Assert.NotEqual(SandboxLevel.Container, effectiveLevel);

        var isUnix = OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
        var expectedFallback = isUnix ? SandboxLevel.OsSandboxed : SandboxLevel.BareCli;
        Assert.Equal(expectedFallback, effectiveLevel);
    }
}
