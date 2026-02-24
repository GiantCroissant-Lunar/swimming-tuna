using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SwarmAssistant.Runtime.Execution;

public sealed partial class WorkspaceBranchManager
{
    private readonly bool _enabled;

    public WorkspaceBranchManager(bool enabled)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Produces a deterministic git branch name for the given task identifier.
    /// Non-alphanumeric characters (except <c>-</c> and <c>_</c>) are replaced with <c>-</c>.
    /// </summary>
    public static string BranchNameForTask(string taskId)
    {
        var sanitized = SanitizeRegex().Replace(taskId, "-");
        return $"swarm/{sanitized}";
    }

    /// <summary>
    /// Creates a new git branch for the task. Returns the branch name on success,
    /// or <c>null</c> when disabled or if the git command fails for any reason.
    /// This method never throws.
    /// </summary>
    public async Task<string?> EnsureBranchAsync(string taskId)
    {
        if (!_enabled)
        {
            return null;
        }

        var branchName = BranchNameForTask(taskId);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("checkout");
            process.StartInfo.ArgumentList.Add("-b");
            process.StartInfo.ArgumentList.Add(branchName);

            if (!process.Start())
            {
                return null;
            }

            await process.WaitForExitAsync();

            return process.ExitCode == 0 ? branchName : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-_]")]
    private static partial Regex SanitizeRegex();
}
