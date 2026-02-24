namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;

public sealed class SandboxLevelTests
{
    [Theory]
    [InlineData(SandboxLevel.BareCli, 0)]
    [InlineData(SandboxLevel.OsSandboxed, 1)]
    [InlineData(SandboxLevel.Container, 2)]
    public void SandboxLevel_HasExpectedIntegerValues(SandboxLevel level, int expected)
    {
        Assert.Equal(expected, (int)level);
    }

    [Fact]
    public void SandboxLevel_DefaultIsBareCli()
    {
        Assert.Equal(0, (int)default(SandboxLevel));
    }

    [Fact]
    public void SandboxRequirements_DefaultsAreFalseAndEmpty()
    {
        var requirements = new SandboxRequirements();

        Assert.False(requirements.NeedsOAuth);
        Assert.False(requirements.NeedsKeychain);
        Assert.Empty(requirements.NeedsNetwork);
        Assert.False(requirements.NeedsGpuAccess);
    }

    [Fact]
    public void SandboxRequirements_CanSpecifyNetworkHosts()
    {
        var requirements = new SandboxRequirements
        {
            NeedsNetwork = ["api.github.com", "copilot-proxy.githubusercontent.com"]
        };

        Assert.Equal(2, requirements.NeedsNetwork.Length);
    }
}
