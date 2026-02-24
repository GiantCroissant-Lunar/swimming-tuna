namespace SwarmAssistant.Runtime.Execution;

internal static class LinuxSandboxWrapper
{
    public static string[] BuildNamespaceArgs() =>
        ["--net", "--mount", "--pid", "--fork"];

    public static SandboxCommand WrapCommand(
        string command,
        IReadOnlyList<string> args,
        string workspacePath,
        string[] allowedHosts)
    {
        var unshareArgs = new List<string>(BuildNamespaceArgs());
        unshareArgs.Add("--");
        unshareArgs.Add(command);
        unshareArgs.AddRange(args);
        return new SandboxCommand("unshare", unshareArgs.ToArray());
    }
}
