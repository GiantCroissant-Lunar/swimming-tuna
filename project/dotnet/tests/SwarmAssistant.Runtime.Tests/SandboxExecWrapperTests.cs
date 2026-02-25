namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class SandboxExecWrapperTests
{
    [Fact]
    public void BuildProfile_DeniesNetworkByDefault()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Contains("(deny network*)", profile);
    }

    [Fact]
    public void BuildProfile_AllowsSpecifiedHosts()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/workspace",
            allowedHosts: ["api.github.com"]);

        Assert.Contains("api.github.com", profile);
    }

    [Fact]
    public void BuildProfile_ScopesFileWriteToWorkspace()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Contains("/tmp/workspace", profile);
        Assert.Contains("(deny file-write*)", profile);
        Assert.Contains("(allow file-write*", profile);
    }

    [Fact]
    public void BuildProfile_EscapesSpecialCharactersInWorkspacePath()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/work\"space\\dir",
            allowedHosts: []);

        // Backslash and double-quote should be escaped in the SBPL profile
        Assert.Contains("/tmp/work\\\"space\\\\dir", profile);
        Assert.DoesNotContain("/tmp/work\"space\\dir\"", profile);
    }

    [Fact]
    public void WrapCommand_ReturnsSandboxExecCommand()
    {
        var result = SandboxExecWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Equal("sandbox-exec", result.Command);
        Assert.Contains("-p", result.Args);
        Assert.Contains("copilot", result.Args);
    }
}
