namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class LinuxSandboxWrapperTests
{
    [Fact]
    public void WrapCommand_UsesUnshare()
    {
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Equal("unshare", result.Command);
        Assert.Contains("--net", result.Args);
        Assert.Contains("--mount", result.Args);
    }

    [Fact]
    public void WrapCommand_IncludesOriginalCommand()
    {
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Contains("copilot", result.Args);
        Assert.Contains("--prompt", result.Args);
    }

    [Fact]
    public void BuildNamespaceArgs_IncludesNetAndMount()
    {
        var args = LinuxSandboxWrapper.BuildNamespaceArgs();

        Assert.Contains("--net", args);
        Assert.Contains("--mount", args);
    }
}
