using System.Diagnostics;
using System.Text;
using SwarmAssistant.Runtime.Tasks;

namespace SwarmAssistant.Runtime.Execution;

public sealed class GitArtifactCollector
{
    private static readonly TimeSpan GitCommandTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<GitArtifactCollector>? _logger;

    public GitArtifactCollector(ILogger<GitArtifactCollector>? logger = null)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<TaskArtifact>> CollectFileArtifactsAsync(
        string taskId,
        string? runId,
        string agentId,
        string role,
        CancellationToken cancellationToken = default)
    {
        var topLevel = await RunGitAsync(["rev-parse", "--show-toplevel"], cancellationToken);
        if (topLevel.ExitCode != 0 || string.IsNullOrWhiteSpace(topLevel.StdOut))
        {
            return [];
        }

        var repositoryRoot = topLevel.StdOut.Trim();
        var branchResult = await RunGitAsync(["rev-parse", "--abbrev-ref", "HEAD"], cancellationToken);
        var branch = branchResult.ExitCode == 0
            ? branchResult.StdOut.Trim()
            : string.Empty;

        var statusResult = await RunGitAsync(["status", "--porcelain"], cancellationToken);
        if (statusResult.ExitCode != 0 || string.IsNullOrWhiteSpace(statusResult.StdOut))
        {
            return [];
        }

        var artifacts = new List<TaskArtifact>();
        var resolvedRunId = LegacyRunId.Resolve(runId, taskId);
        var now = DateTimeOffset.UtcNow;

        foreach (var rawLine in statusResult.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (rawLine.Length < 4)
            {
                continue;
            }

            var status = rawLine[..2].Trim();
            var pathPart = rawLine[3..].Trim();
            if (string.IsNullOrWhiteSpace(pathPart))
            {
                continue;
            }

            var normalizedPath = NormalizePath(pathPart);
            var fullPath = Path.Combine(repositoryRoot, normalizedPath);

            string contentHash;
            if (File.Exists(fullPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
                    contentHash = TaskArtifact.ComputeContentHash(bytes);
                }
                catch (IOException)
                {
                    contentHash = TaskArtifact.ComputeContentHash($"deleted:{normalizedPath}:{status}");
                }
                catch (UnauthorizedAccessException)
                {
                    contentHash = TaskArtifact.ComputeContentHash($"deleted:{normalizedPath}:{status}");
                }
            }
            else
            {
                contentHash = TaskArtifact.ComputeContentHash($"deleted:{normalizedPath}:{status}");
            }

            var (linesAdded, linesRemoved) = await GetNumStatAsync(normalizedPath, cancellationToken);
            if (linesAdded == 0 && linesRemoved == 0 && File.Exists(fullPath))
            {
                try
                {
                    linesAdded = await CountLinesAsync(fullPath, cancellationToken);
                }
                catch (IOException)
                {
                    linesAdded = 0;
                }
                catch (UnauthorizedAccessException)
                {
                    linesAdded = 0;
                }
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["status"] = status,
                ["role"] = role,
                ["language"] = GetLanguage(normalizedPath),
                ["linesAdded"] = linesAdded.ToString(),
                ["linesRemoved"] = linesRemoved.ToString()
            };
            if (!string.IsNullOrWhiteSpace(branch))
            {
                metadata["branch"] = branch;
            }

            artifacts.Add(new TaskArtifact(
                ArtifactId: TaskArtifact.BuildArtifactId(contentHash),
                RunId: resolvedRunId,
                TaskId: taskId,
                AgentId: agentId,
                Type: TaskArtifactTypes.File,
                Path: normalizedPath,
                ContentHash: contentHash,
                CreatedAt: now,
                Metadata: metadata));
        }

        return artifacts;
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunGitAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        try
        {
            using var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tokenSource.CancelAfter(GitCommandTimeout);
            var token = tokenSource.Token;

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
                return (1, string.Empty, "failed to start git process");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
            var stderrTask = process.StandardError.ReadToEndAsync(token);

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                try
                {
                    await Task.WhenAll(stdoutTask, stderrTask);
                }
                catch
                {
                    // ignore drain failures after timeout
                }

                _logger?.LogWarning(
                    "git command timed out after {TimeoutSeconds}s args={Args}",
                    GitCommandTimeout.TotalSeconds,
                    string.Join(" ", args));
                return (1, string.Empty, $"git command timed out after {GitCommandTimeout.TotalSeconds:0}s");
            }
            catch (OperationCanceledException)
            {
                TryKillProcess(process);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "git command failed args={Args}", string.Join(" ", args));
            return (1, string.Empty, exception.Message);
        }
    }

    private async Task<(int LinesAdded, int LinesRemoved)> GetNumStatAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(["diff", "--numstat", "--", path], cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return (0, 0);
        }

        var firstLine = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return (0, 0);
        }

        var parts = firstLine.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return (0, 0);
        }

        var added = int.TryParse(parts[0], out var a) ? a : 0;
        var removed = int.TryParse(parts[1], out var r) ? r : 0;
        return (added, removed);
    }

    private static async Task<int> CountLinesAsync(string path, CancellationToken cancellationToken)
    {
        var count = 0;
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken) is not null)
        {
            count++;
        }

        return count;
    }

    private static string NormalizePath(string path)
    {
        var renamedIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
        var normalized = renamedIndex >= 0
            ? path[(renamedIndex + 4)..]
            : path;
        return normalized.Replace('\\', '/');
    }

    private static string GetLanguage(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(ext))
        {
            return "unknown";
        }

        return ext.TrimStart('.').ToLowerInvariant();
    }

    private static void TryKillProcess(Process process)
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
            // best effort only
        }
    }
}
