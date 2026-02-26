using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Memvid;

namespace SwarmAssistant.Runtime.Tests.Services;

/// <summary>
/// Integration tests for MemvidClient find mode functionality.
/// Tests the lexical search mode (lex) to verify non-empty results are returned.
/// Requires Python 3.8+ and memvid-sdk installed. Skipped in CI.
/// </summary>
public sealed class MemvidClientFindModeTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _storePath;
    private readonly ILogger<MemvidClient> _logger;
    private readonly MemvidClient _client;

    public MemvidClientFindModeTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"memvid-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _storePath = Path.Combine(_tempDirectory, "test-store.mv2");
        _logger = NullLogger<MemvidClient>.Instance;

        var opts = new RuntimeOptions();
        var pythonPath = Environment.GetEnvironmentVariable("MEMVID_PYTHON_PATH") ?? opts.MemvidPythonPath;
        var svcDir = Environment.GetEnvironmentVariable("MEMVID_SVC_DIR") ?? opts.MemvidSvcDir;
        var timeoutMs = opts.MemvidTimeoutSeconds * 1000;

        _client = new MemvidClient(pythonPath, svcDir, timeoutMs, _logger);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact(Skip = "Requires memvid-sdk installed locally")]
    public async Task FindWithLexMode_ReturnsNonEmptyResults()
    {
        var createdPath = await _client.CreateStoreAsync(_storePath, CancellationToken.None);
        Assert.Equal(_storePath, createdPath);

        var doc = new MemvidDocument(
            "Implement IFoo interface",
            "builder",
            "Added IFoo interface to support dependency injection pattern"
        );

        var frameId = await _client.PutAsync(_storePath, doc, CancellationToken.None);
        Assert.True(frameId > 0);

        var results = await _client.FindAsync(
            _storePath,
            "dependency injection",
            k: 5,
            mode: "lex",
            CancellationToken.None
        );

        Assert.NotEmpty(results);
        Assert.Equal("Implement IFoo interface", results[0].Title);
    }

    [Fact(Skip = "Requires memvid-sdk installed locally")]
    public async Task FindWithLexMode_WhenNoMatches_ReturnsEmptyList()
    {
        var createdPath = await _client.CreateStoreAsync(_storePath, CancellationToken.None);
        Assert.Equal(_storePath, createdPath);

        var doc = new MemvidDocument(
            "Implement IFoo interface",
            "builder",
            "Added IFoo interface to support dependency injection pattern"
        );

        var frameId = await _client.PutAsync(_storePath, doc, CancellationToken.None);
        Assert.True(frameId > 0);

        var results = await _client.FindAsync(
            _storePath,
            "quantum computing",
            k: 5,
            mode: "lex",
            CancellationToken.None
        );

        Assert.Empty(results);
    }
}
