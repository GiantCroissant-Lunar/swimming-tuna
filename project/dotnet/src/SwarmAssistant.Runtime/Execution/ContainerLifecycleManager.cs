using System.Globalization;

namespace SwarmAssistant.Runtime.Execution;

public static class ContainerLifecycleManager
{
    public static string[] BuildRunArgs(
        string imageName,
        string workspacePath,
        double cpuLimit,
        string memoryLimit,
        int timeoutSeconds)
    {
        return
        [
            "run",
            "--rm",
            $"--cpus={cpuLimit.ToString(CultureInfo.InvariantCulture)}",
            $"--memory={memoryLimit}",
            $"--stop-timeout={timeoutSeconds}",
            "-v",
            $"{workspacePath}:/workspace:rw",
            "-w",
            "/workspace",
            imageName
        ];
    }
}
