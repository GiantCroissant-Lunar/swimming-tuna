using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SwarmAssistant.Runtime.Memvid;

/// <summary>
/// Thrown when the memvid Python CLI returns an error (exit code != 0 or
/// the response JSON contains an <c>"error"</c> field).
/// </summary>
public sealed class MemvidException : Exception
{
    public MemvidException(string message) : base(message) { }
    public MemvidException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thin wrapper around the memvid Python CLI (<c>python -m src &lt;command&gt;</c>).
/// All heavy lifting is delegated to the subprocess; this class handles
/// argument building, process lifecycle, and JSON deserialization.
/// </summary>
public sealed class MemvidClient
{
    private readonly string _pythonPath;
    private readonly string _svcDir;
    private readonly int _timeoutMs;
    private readonly ILogger<MemvidClient> _logger;

    public MemvidClient(string pythonPath, string svcDir, int timeoutMs, ILogger<MemvidClient> logger)
    {
        _pythonPath = pythonPath;
        _svcDir = svcDir;
        _timeoutMs = timeoutMs;
        _logger = logger;
    }

    // ── Public API ──────────────────────────────────────────────

    public async Task<string> CreateStoreAsync(string path, CancellationToken ct)
    {
        var json = await RunAsync("create", stdin: null, ct, path);
        var response = ParseJsonOrThrow<MemvidCreateResponse>(json);
        return response.Created;
    }

    public async Task<int> PutAsync(string path, MemvidDocument doc, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(doc, MemvidJsonContext.Default.MemvidDocument);
        var json = await RunAsync("put", stdin: payload, ct, path);
        var response = ParseJsonOrThrow<MemvidPutResponse>(json);
        return response.FrameId;
    }

    public async Task<List<MemvidResult>> FindAsync(string path, string query, int k, string mode, CancellationToken ct)
    {
        var json = await RunAsync("find", stdin: null, ct, path, "--query", query, "--k", k.ToString(), "--mode", mode);
        var response = ParseJsonOrThrow<MemvidFindResponse>(json);
        return response.Results;
    }

    public async Task<List<MemvidTimelineEntry>> TimelineAsync(string path, int limit, CancellationToken ct)
    {
        var json = await RunAsync("timeline", stdin: null, ct, path, "--limit", limit.ToString());
        var response = ParseJsonOrThrow<MemvidTimelineResponse>(json);
        return response.Entries;
    }

    // ── Internal helpers (visible to tests via InternalsVisibleTo) ───

    internal static string[] BuildArgs(string command, params string[] extra)
    {
        var args = new string[2 + 1 + extra.Length]; // "-m", "src", command, ...extra
        args[0] = "-m";
        args[1] = "src";
        args[2] = command;
        extra.CopyTo(args, 3);
        return args;
    }

    internal static T ParseJson<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, MemvidJsonContext.Default.Options)
            ?? throw new MemvidException($"Deserialization returned null for type {typeof(T).Name}");
    }

    internal static T ParseJsonOrThrow<T>(string json)
    {
        // Check for error envelope first.
        try
        {
            var errorResponse = JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidErrorResponse);
            if (errorResponse?.Error is { Length: > 0 } errorMsg)
            {
                throw new MemvidException(errorMsg);
            }
        }
        catch (JsonException)
        {
            // Not an error envelope, proceed with normal deserialization.
        }

        return ParseJson<T>(json);
    }

    // ── Private ─────────────────────────────────────────────────

    private async Task<string> RunAsync(string command, string? stdin, CancellationToken ct, params string[] extra)
    {
        var args = BuildArgs(command, extra);

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            WorkingDirectory = _svcDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.LogDebug("memvid: {Python} {Args}", _pythonPath, string.Join(' ', args));

        using var process = Process.Start(psi)
            ?? throw new MemvidException("Failed to start memvid process");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeoutMs);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            throw new MemvidException($"memvid process timed out after {_timeoutMs}ms");
        }

        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("memvid exited {ExitCode}: {Stderr}", process.ExitCode, stderr);
            throw new MemvidException(stderr.Length > 0 ? stderr : $"memvid exited with code {process.ExitCode}");
        }

        _logger.LogDebug("memvid stdout: {Stdout}", stdout);
        return stdout;
    }
}
