namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

public sealed class SandboxLevelEnforcerTests
{
    [Theory]
    [InlineData(SandboxLevel.BareCli)]
    [InlineData(SandboxLevel.Container)]
    public void Validate_SupportedLevels_ReturnsTrue(SandboxLevel level)
    {
        var enforcer = new SandboxLevelEnforcer();

        var result = enforcer.CanEnforce(level);

        Assert.True(result);
    }

    [Fact]
    public void Validate_OsSandboxed_ReturnsTrueOnMacOSOrLinux()
    {
        var enforcer = new SandboxLevelEnforcer();

        var result = enforcer.CanEnforce(SandboxLevel.OsSandboxed);

        // On macOS or Linux (where CI and local tests run), this should be true
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            Assert.True(result);
    }

    [Fact]
    public void Validate_DeclaredLevelExceedsHost_ReturnsFalse()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);

        var result = enforcer.CanEnforce(SandboxLevel.Container);

        Assert.False(result);
    }

    [Fact]
    public void GetEffectiveLevel_FallsBackWhenUnavailable()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);

        var effective = enforcer.GetEffectiveLevel(SandboxLevel.Container);

        Assert.NotEqual(SandboxLevel.Container, effective);
    }

    [Fact]
    public void GetEffectiveLevel_ReturnsDeclaredWhenAvailable()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: true);

        var effective = enforcer.GetEffectiveLevel(SandboxLevel.Container);

        Assert.Equal(SandboxLevel.Container, effective);
    }

    [Fact]
    public void GetEffectiveLevel_BareCliAlwaysAvailable()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);

        var effective = enforcer.GetEffectiveLevel(SandboxLevel.BareCli);

        Assert.Equal(SandboxLevel.BareCli, effective);
    }
}
