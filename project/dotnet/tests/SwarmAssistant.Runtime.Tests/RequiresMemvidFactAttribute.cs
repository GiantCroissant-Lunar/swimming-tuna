using System.Diagnostics;
using SwarmAssistant.Runtime.Configuration;
using Xunit;

namespace SwarmAssistant.Runtime.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresMemvidFactAttribute : FactAttribute
{
    private const string EnableEnvVar = "MEMVID_INTEGRATION_TESTS";

    public RequiresMemvidFactAttribute()
    {
        var (canRun, reason) = CanRun();
        if (!canRun)
        {
            Skip = reason;
        }
    }

    private static (bool canRun, string reason) CanRun()
    {
        if (!IsTruthy(Environment.GetEnvironmentVariable(EnableEnvVar)))
        {
            return (false, $"Set {EnableEnvVar}=1 to enable memvid integration tests.");
        }

        var options = new RuntimeOptions();
        var memvidPaths = MemvidTestEnvironment.ResolvePaths(options);
        var svcDir = memvidPaths.ServiceDirectory;
        if (!Directory.Exists(svcDir))
        {
            return (false, $"memvid service directory not found: {svcDir}");
        }

        var pythonPath = memvidPaths.PythonPath;
        if (LooksLikePath(pythonPath) && !File.Exists(pythonPath))
        {
            return (false, $"memvid python executable not found: {pythonPath}");
        }

        if (!CanImportMemvidSdk(pythonPath, svcDir))
        {
            return (false, "python environment does not provide memvid_sdk import.");
        }

        return (true, string.Empty);
    }

    private static bool CanImportMemvidSdk(string pythonPath, string svcDir)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = svcDir
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("import memvid_sdk");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikePath(string value) =>
        Path.IsPathRooted(value) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar);
}
