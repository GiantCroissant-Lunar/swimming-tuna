namespace SwarmAssistant.Runtime.Execution;

internal static class SandboxExecWrapper
{
    public static string BuildProfile(string workspacePath, string[] allowedHosts)
    {
        var profile = "(version 1)\n";
        profile += "(deny default)\n";
        profile += "(allow process*)\n";
        profile += "(allow process-exec)\n";
        profile += "(allow sysctl-read)\n";
        profile += "(allow mach-lookup)\n";

        // File access: read everywhere, write only to workspace
        profile += "(allow file-read*)\n";
        profile += "(deny file-write*)\n";
        profile += $"(allow file-write* (subpath \"{workspacePath}\"))\n";
        profile += "(allow file-write* (subpath \"/tmp\"))\n";
        profile += "(allow file-write* (subpath \"/private/tmp\"))\n";

        // Network: deny by default, allow specific hosts
        profile += "(deny network*)\n";
        if (allowedHosts.Length > 0)
        {
            profile += "(allow network-outbound (remote tcp))\n";
            foreach (var host in allowedHosts)
            {
                profile += $";; allowed host: {host}\n";
            }
        }

        return profile;
    }

    public static SandboxCommand WrapCommand(
        string command,
        IReadOnlyList<string> args,
        string workspacePath,
        string[] allowedHosts)
    {
        var profile = BuildProfile(workspacePath, allowedHosts);
        var sandboxArgs = new List<string> { "-p", profile, command };
        sandboxArgs.AddRange(args);
        return new SandboxCommand("sandbox-exec", sandboxArgs.ToArray());
    }
}
