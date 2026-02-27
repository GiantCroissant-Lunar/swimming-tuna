using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace SwarmAssistant.Runtime.Execution;

public enum MergeResult { Success, Conflict, BranchNotFound }

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
    /// <param name="taskId">The task identifier used to generate the branch name.</param>
    /// <param name="parentBranch">Optional parent branch to base the worktree on. Defaults to current HEAD if null.</param>
    public async Task<string?> EnsureWorktreeAsync(string taskId, string? parentBranch = null)
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
        var normalizedParentBranch = string.IsNullOrWhiteSpace(parentBranch) ? null : parentBranch.Trim();

        try
        {
            var args = new List<string> { "worktree", "add", worktreePath, "-b", branchName };
            if (normalizedParentBranch is not null)
            {
                args.Add(normalizedParentBranch);
            }
            var result = await RunGitAsync(args.ToArray());
            if (result.ExitCode == 0)
            {
                _logger?.LogInformation(
                    "Worktree created path={WorktreePath} branch={Branch} parentBranch={ParentBranch} taskId={TaskId}",
                    worktreePath, branchName, normalizedParentBranch ?? "HEAD", taskId);
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
    /// Commits any uncommitted changes in the worktree, then removes the worktree directory.
    /// The branch is preserved so the gatekeeper can review and create a PR.
    /// Best-effort; never throws.
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
            // Commit any uncommitted changes before removing the worktree
            await CommitWorktreeChangesAsync(worktreePath, taskId);

            var removeResult = await RunGitAsync(["worktree", "remove", worktreePath, "--force"]);
            if (removeResult.ExitCode == 0)
            {
                _logger?.LogInformation(
                    "Worktree removed path={WorktreePath} taskId={TaskId} (branch {Branch} preserved for review)",
                    worktreePath, taskId, branchName);
            }
            else
            {
                _logger?.LogWarning(
                    "git worktree remove failed exitCode={ExitCode} stderr={Stderr} taskId={TaskId}",
                    removeResult.ExitCode, removeResult.StdErr, taskId);
            }

            // Branch is intentionally preserved for gatekeeper review.
            // Use CleanupBranchAsync to delete it after review/merge.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Worktree cleanup failed for task {TaskId}", taskId);
        }
    }

    /// <summary>
    /// Commits any uncommitted changes in the worktree so they survive worktree removal.
    /// Best-effort; never throws. Skips if the worktree has no changes.
    /// </summary>
    private async Task CommitWorktreeChangesAsync(string worktreePath, string taskId)
    {
        if (!Directory.Exists(worktreePath))
        {
            return;
        }

        try
        {
            // Check for changes (staged + unstaged + untracked)
            var statusResult = await RunGitInDirAsync(worktreePath, ["status", "--porcelain"]);
            if (statusResult.ExitCode != 0 || string.IsNullOrWhiteSpace(statusResult.StdOut))
            {
                _logger?.LogDebug("No uncommitted changes in worktree for task {TaskId}", taskId);
                return;
            }

            // Stage all changes
            var addResult = await RunGitInDirAsync(worktreePath, ["add", "-A"]);
            if (addResult.ExitCode != 0)
            {
                _logger?.LogWarning(
                    "git add -A failed in worktree exitCode={ExitCode} stderr={Stderr} taskId={TaskId}",
                    addResult.ExitCode, addResult.StdErr, taskId);
                return;
            }

            // Commit with swarm attribution
            var commitResult = await RunGitInDirAsync(worktreePath,
                ["commit", "-m", $"feat(swarm): builder output for {taskId}", "--no-verify"]);
            if (commitResult.ExitCode == 0)
            {
                _logger?.LogInformation("Committed worktree changes for task {TaskId}", taskId);
            }
            else
            {
                _logger?.LogDebug(
                    "git commit failed in worktree exitCode={ExitCode} stderr={Stderr} taskId={TaskId}",
                    commitResult.ExitCode, commitResult.StdErr, taskId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to commit worktree changes for task {TaskId}", taskId);
        }
    }

    /// <summary>
    /// Deletes the branch for a task after the gatekeeper has reviewed and merged it.
    /// Call this explicitly after PR merge or manual review.
    /// Best-effort; never throws.
    /// </summary>
    public async Task CleanupBranchAsync(string taskId)
    {
        if (!_enabled)
        {
            return;
        }

        var branchName = BranchNameForTask(taskId);
        try
        {
            var branchResult = await RunGitAsync(["branch", "-D", branchName]);
            if (branchResult.ExitCode == 0)
            {
                _logger?.LogInformation("Branch deleted branch={Branch} taskId={TaskId}", branchName, taskId);
            }
            else
            {
                _logger?.LogDebug(
                    "git branch -D failed exitCode={ExitCode} stderr={Stderr} taskId={TaskId}",
                    branchResult.ExitCode, branchResult.StdErr, taskId);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Branch cleanup failed for task {TaskId}", taskId);
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

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(string[] args)
        => RunGitInDirAsync(null, args);

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunGitInDirAsync(
        string? workingDirectory, string[] args)
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

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }

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
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // best-effort kill
            }

            await Task.WhenAll(stdoutTask, stderrTask);
            return (124, await stdoutTask, "git command timed out after 30 seconds");
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Creates a new branch from a base branch. Returns true on success.
    /// </summary>
    public static async Task<bool> CreateBranchFromAsync(string branchName, string baseBranch)
    {
        var result = await RunGitAsync(["checkout", "-b", branchName, baseBranch]);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Pushes a branch to origin with upstream tracking. Returns true on success.
    /// </summary>
    public static async Task<bool> PushBranchAsync(string branchName)
    {
        var result = await RunGitAsync(["push", "-u", "origin", branchName]);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// Merges a task branch back into the target branch using --no-ff.
    /// Returns <see cref="MergeResult.Success"/> on clean merge,
    /// <see cref="MergeResult.Conflict"/> if the merge has conflicts (auto-aborted),
    /// or <see cref="MergeResult.BranchNotFound"/> if the task branch does not exist.
    /// </summary>
    public async Task<MergeResult> MergeTaskBranchAsync(string taskId, string targetBranch)
    {
        var taskBranch = BranchNameForTask(taskId);

        // Verify task branch exists
        var verifyResult = await RunGitAsync(["rev-parse", "--verify", taskBranch]);
        if (verifyResult.ExitCode != 0)
        {
            _logger?.LogWarning("Task branch not found branch={Branch} taskId={TaskId}", taskBranch, taskId);
            return MergeResult.BranchNotFound;
        }

        // Checkout target branch
        var checkoutResult = await RunGitAsync(["checkout", targetBranch]);
        if (checkoutResult.ExitCode != 0)
        {
            _logger?.LogWarning(
                "Failed to checkout target branch={Branch} exitCode={ExitCode} stderr={Stderr}",
                targetBranch, checkoutResult.ExitCode, checkoutResult.StdErr);
            return MergeResult.Conflict;
        }

        // Merge with --no-ff
        var mergeResult = await RunGitAsync(["merge", taskBranch, "--no-ff", "-m",
            $"merge: {taskBranch} into {targetBranch}"]);
        if (mergeResult.ExitCode != 0)
        {
            _logger?.LogWarning(
                "Merge conflict taskBranch={TaskBranch} targetBranch={TargetBranch} taskId={TaskId}",
                taskBranch, targetBranch, taskId);
            await RunGitAsync(["merge", "--abort"]);
            return MergeResult.Conflict;
        }

        // Clean up task branch on success
        var deleteResult = await RunGitAsync(["branch", "-d", taskBranch]);
        if (deleteResult.ExitCode != 0)
        {
            _logger?.LogDebug(
                "Failed to delete merged branch={Branch} exitCode={ExitCode}",
                taskBranch, deleteResult.ExitCode);
        }

        _logger?.LogInformation(
            "Merged task branch taskBranch={TaskBranch} into targetBranch={TargetBranch} taskId={TaskId}",
            taskBranch, targetBranch, taskId);

        return MergeResult.Success;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\-_]")]
    private static partial Regex SanitizeRegex();
}
