using System.Diagnostics;
using SwarmAssistant.Runtime.Configuration;

namespace SwarmAssistant.Runtime.Tests;

internal sealed record MemvidPaths(string RepoRoot, string ServiceDirectory, string PythonPath);

internal static class MemvidTestEnvironment
{
    internal static MemvidPaths ResolvePaths(RuntimeOptions options)
    {
        var repoRoot = FindRepoRoot();
        var serviceDirectory = ResolveServiceDirectory(
            Environment.GetEnvironmentVariable("MEMVID_SVC_DIR"),
            options.MemvidSvcDir,
            repoRoot);
        var pythonPath = ResolvePythonPath(
            Environment.GetEnvironmentVariable("MEMVID_PYTHON_PATH"),
            options.MemvidPythonPath,
            serviceDirectory,
            repoRoot);

        return new MemvidPaths(repoRoot, serviceDirectory, pythonPath);
    }

    internal static string ResolveServiceDirectory(string? envValue, string defaultValue, string repoRoot)
    {
        var value = string.IsNullOrWhiteSpace(envValue) ? defaultValue : envValue;
        return Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(Path.Combine(repoRoot, value));
    }

    internal static string ResolvePythonPath(string? envValue, string defaultValue, string serviceDirectory, string repoRoot)
    {
        if (string.IsNullOrWhiteSpace(envValue))
        {
            return Path.IsPathRooted(defaultValue)
                ? defaultValue
                : Path.GetFullPath(Path.Combine(serviceDirectory, defaultValue));
        }

        return Path.IsPathRooted(envValue)
            ? envValue
            : Path.GetFullPath(Path.Combine(repoRoot, envValue));
    }

    internal static string FindRepoRoot()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "rev-parse --show-toplevel",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                }
                else
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        return output;
                    }
                }
            }
        }
        catch
        {
            // Fall through to directory walk.
        }

        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
