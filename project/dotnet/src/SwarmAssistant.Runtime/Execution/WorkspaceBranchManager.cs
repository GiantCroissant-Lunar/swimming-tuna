using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SwarmAssistant.Runtime.Execution;

public sealed partial class WorkspaceBranchManager
{
    private readonly bool _enabled;
    private readonly ILogger? _logger;

    public WorkspaceBranchManager(bool enabled, ILogger? logger = null)
    {
        _enabled = enabled;
        _logger = logger;
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

            // Drain streams to avoid OS pipe-buffer deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);

            return process.ExitCode == 0 ? branchName : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to create workspace branch {Branch} for task {TaskId}", branchName, taskId);
            return null;
        }
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-_]")]
    private static partial Regex SanitizeRegex();
}
