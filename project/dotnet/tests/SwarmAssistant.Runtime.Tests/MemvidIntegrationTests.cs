using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Memvid;

namespace SwarmAssistant.Runtime.Tests;

/// <summary>
/// Integration tests for the full memvid lifecycle: create → put → find.
/// Runs only when MEMVID_INTEGRATION_TESTS=1 and memvid prerequisites are available.
/// </summary>
public sealed class MemvidIntegrationTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _storePath;
    private readonly MemvidClient _client;

    public MemvidIntegrationTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"memvid-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _storePath = Path.Combine(_tempDirectory, "lifecycle-store.mv2");

        var opts = new RuntimeOptions();
        var memvidPaths = MemvidTestEnvironment.ResolvePaths(opts);

        _client = new MemvidClient(
            memvidPaths.PythonPath,
            memvidPaths.ServiceDirectory,
            opts.MemvidTimeoutSeconds * 1000,
            NullLogger<MemvidClient>.Instance);
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

    [RequiresMemvidFact]
    public async Task Full_Lifecycle_Create_Put_Find()
    {
        var createdPath = await _client.CreateStoreAsync(_storePath, CancellationToken.None);
        Assert.Equal(_storePath, createdPath);

        var frameId = await _client.PutAsync(
            _storePath,
            new MemvidDocument(
                Title: "Lifecycle test",
                Label: "builder",
                Text: "Implements create put find lifecycle validation"),
            CancellationToken.None);
        Assert.True(frameId >= 0);

        var results = await _client.FindAsync(
            _storePath,
            query: "lifecycle validation",
            k: 5,
            mode: "lex",
            CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Equal("Lifecycle test", results[0].Title);
    }
}
