namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

public sealed class SandboxSpectrumPolicyTests
{
    [Fact]
    public void RecommendLevel_OAuthAgent_ReturnsBareCli()
    {
        var requirements = new SandboxRequirements
        {
            NeedsOAuth = true,
            NeedsKeychain = false,
            NeedsNetwork = [],
            NeedsGpuAccess = false
        };

        var result = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.BareCli, result);
    }

    [Fact]
    public void RecommendLevel_KeychainAgent_ReturnsBareCli()
    {
        var requirements = new SandboxRequirements
        {
            NeedsOAuth = false,
            NeedsKeychain = true,
            NeedsNetwork = [],
            NeedsGpuAccess = false
        };

        var result = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.BareCli, result);
    }

    [Fact]
    public void RecommendLevel_NoSpecialNeeds_ReturnsContainer()
    {
        var requirements = new SandboxRequirements
        {
            NeedsOAuth = false,
            NeedsKeychain = false,
            NeedsNetwork = [],
            NeedsGpuAccess = false
        };

        var result = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.Container, result);
    }

    [Fact]
    public void RecommendLevel_NetworkOnlyNeeds_ReturnsOsSandboxed()
    {
        var requirements = new SandboxRequirements
        {
            NeedsOAuth = false,
            NeedsKeychain = false,
            NeedsNetwork = ["api.github.com"],
            NeedsGpuAccess = false
        };

        var result = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.OsSandboxed, result);
    }
}
