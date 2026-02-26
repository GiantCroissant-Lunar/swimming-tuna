using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SwarmAssistant.Runtime.Execution;

public sealed partial class WorkspaceBranchManager
{
    private readonly bool _enabled;
    private readonly bool _worktreeIsolation;
    private readonly ILogger<WorkspaceBranchManager>? _logger;

    public WorkspaceBranchManager(bool enabled, ILogger<WorkspaceBranchManager>? logger = null, bool worktreeIsolation = false)
    {
        _enabled = enabled;
        _logger = logger;
        _worktreeIsolation = worktreeIsolation;
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
    /// Creates an isolated git worktree for the task. Returns the absolute worktree path
    /// on success, or <c>null</c> when disabled or if the git command fails.
    /// This method never throws.
    /// </summary>
    public async Task<string?> EnsureWorktreeAsync(string taskId)
    {
        if (!_enabled || !_worktreeIsolation)
        {
            return null;
        }

        var branchName = BranchNameForTask(taskId);
        var repoRoot = await GetRepoRootAsync();
        if (repoRoot is null)
        {
            return null;
        }

        var worktreePath = Path.Combine(repoRoot, ".worktrees", branchName.Replace('/', '-'));

        try
        {
            var result = await RunGitAsync(["worktree", "add", worktreePath, "-b", branchName]);
            if (result.ExitCode == 0)
            {
                _logger?.LogInformation(
                    "Worktree created path={WorktreePath} branch={Branch} taskId={TaskId}",
                    worktreePath, branchName, taskId);
                return worktreePath;
            }

            _logger?.LogWarning(
                "git worktree add failed exitCode={ExitCode} stderr={Stderr} taskId={TaskId}",
                result.ExitCode, result.StdErr, taskId);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "git worktree add failed for task {TaskId}", taskId);
            return null;
        }
    }

    /// <summary>
    /// Removes the worktree for the given task. Best-effort; never throws.
    /// </summary>
    public async Task RemoveWorktreeAsync(string taskId)
    {
        if (!_enabled || !_worktreeIsolation)
        {
            return;
        }

        var branchName = BranchNameForTask(taskId);
        var repoRoot = await GetRepoRootAsync();
        if (repoRoot is null)
        {
            return;
        }

        var worktreePath = Path.Combine(repoRoot, ".worktrees", branchName.Replace('/', '-'));

        try
        {
            await RunGitAsync(["worktree", "remove", worktreePath, "--force"]);
            _logger?.LogInformation("Worktree removed path={WorktreePath} taskId={TaskId}", worktreePath, taskId);

            // Clean up the branch too
            await RunGitAsync(["branch", "-D", branchName]);
            _logger?.LogDebug("Branch deleted branch={Branch} taskId={TaskId}", branchName, taskId);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Worktree cleanup failed for task {TaskId}", taskId);
        }
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

            // Drain stdout and stderr concurrently to prevent pipe-buffer deadlock,
            // then wait for the process to exit with a timeout.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cts.Token);
            await Task.WhenAll(stdoutTask, stderrTask);

            return process.ExitCode == 0 ? branchName : null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "git checkout -b {Branch} failed for task {TaskId}", branchName, taskId);
            return null;
        }
    }

    private async Task<string?> GetRepoRootAsync()
    {
        var result = await RunGitAsync(["rev-parse", "--show-toplevel"]);
        return result.ExitCode == 0 ? result.StdOut.Trim() : null;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(string[] args)
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

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (!process.Start())
        {
            return (1, string.Empty, "failed to start git");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        await Task.WhenAll(stdoutTask, stderrTask);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-_]")]
    private static partial Regex SanitizeRegex();
}
