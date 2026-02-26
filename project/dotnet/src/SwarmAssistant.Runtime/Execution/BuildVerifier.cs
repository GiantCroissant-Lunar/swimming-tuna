using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SwarmAssistant.Runtime.Execution;

/// <summary>
/// Runs <c>dotnet build</c> and <c>dotnet test</c> directly (no LLM) to verify
/// that the current build compiles and tests pass. Used by the GOAP "Verify" step.
/// </summary>
public sealed class BuildVerifier
{
    private static readonly Regex PassedRegex = new(
        @"Passed\s*[:=]\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FailedRegex = new(
        @"Failed\s*[:=]\s*(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _solutionPath;
    private readonly ILogger<BuildVerifier> _logger;
    private readonly TimeSpan _timeout;

    public BuildVerifier(string solutionPath, ILogger<BuildVerifier> logger, TimeSpan? timeout = null)
    {
        _solutionPath = solutionPath;
        _logger = logger;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
    }

    public async Task<BuildVerifyResult> VerifyAsync(CancellationToken ct = default, string? workingDirectory = null)
    {
        _logger.LogInformation("Starting build verification for {SolutionPath} workingDirectory={WorkingDirectory}",
            _solutionPath, workingDirectory ?? "(default)");

        // 1. Run dotnet build --verbosity quiet
        var buildResult = await RunCommandAsync("dotnet", $"build \"{_solutionPath}\" --verbosity quiet", ct, workingDirectory);
        if (buildResult.ExitCode != 0)
        {
            _logger.LogWarning("Build failed exitCode={ExitCode}", buildResult.ExitCode);
            return new BuildVerifyResult(false, 0, 0, buildResult.Output, "Build failed");
        }

        _logger.LogInformation("Build succeeded, running tests");

        // 2. Run dotnet test --verbosity quiet
        var testResult = await RunCommandAsync("dotnet", $"test \"{_solutionPath}\" --verbosity quiet", ct, workingDirectory);

        // Parse test results
        var (passed, failed) = ParseTestResults(testResult.Output);

        if (testResult.ExitCode != 0 || failed > 0)
        {
            _logger.LogWarning("Tests failed passed={Passed} failed={Failed}", passed, failed);
            return new BuildVerifyResult(false, passed, failed, testResult.Output, $"{failed} test(s) failed");
        }

        _logger.LogInformation("Verification passed passed={Passed} failed={Failed}", passed, failed);
        return new BuildVerifyResult(true, passed, failed, testResult.Output, null);
    }

    private async Task<(int ExitCode, string Output)> RunCommandAsync(
        string fileName, string arguments, CancellationToken ct, string? workingDirectory = null)
    {
        _logger.LogDebug("Running {FileName} {Arguments} in {WorkingDirectory}", fileName, arguments, workingDirectory ?? "(default)");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };

        process.Start();

        // Read stdout and stderr concurrently to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Command timed out after {Timeout}", _timeout);
            try
            { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { _logger.LogDebug(ex, "Failed to kill process, it may have already exited"); }
            return (-1, $"Command timed out after {_timeout}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout}\n--- stderr ---\n{stderr}";

        return (process.ExitCode, combined);
    }

    internal static (int Passed, int Failed) ParseTestResults(string output)
    {
        int passed = 0;
        int failed = 0;

        foreach (Match passedMatch in PassedRegex.Matches(output))
        {
            if (int.TryParse(passedMatch.Groups[1].Value, out var p))
            {
                passed = p; // keep last match (final summary for multi-project)
            }
        }

        foreach (Match failedMatch in FailedRegex.Matches(output))
        {
            if (int.TryParse(failedMatch.Groups[1].Value, out var f))
            {
                failed = f; // keep last match (final summary for multi-project)
            }
        }

        return (passed, failed);
    }
}

/// <summary>
/// Result of a build verification run.
/// </summary>
public sealed record BuildVerifyResult(
    bool Success,
    int TestsPassed,
    int TestsFailed,
    string Output,
    string? Error);
