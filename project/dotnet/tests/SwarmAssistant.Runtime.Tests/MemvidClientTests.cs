using System.Text.Json;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Memvid;

namespace SwarmAssistant.Runtime.Tests;

public sealed class MemvidClientTests
{
    // ── Task 5: Model serialization / deserialization ────────────

    [Fact]
    public void MemvidDocument_Serializes_WithSnakeCaseKeys()
    {
        var doc = new MemvidDocument("Add IFoo", "builder", "Implemented IFoo interface");
        var json = JsonSerializer.Serialize(doc, MemvidJsonContext.Default.MemvidDocument);

        Assert.Contains("\"title\":", json);
        Assert.Contains("\"label\":", json);
        Assert.Contains("\"text\":", json);
        Assert.DoesNotContain("\"Title\"", json);
        Assert.DoesNotContain("\"Label\"", json);
        Assert.DoesNotContain("\"Text\"", json);
    }

    [Fact]
    public void MemvidDocument_WithMetadata_SerializesMetadataField()
    {
        var doc = new MemvidDocument("t", "l", "txt", new Dictionary<string, string> { ["role"] = "planner" });
        var json = JsonSerializer.Serialize(doc, MemvidJsonContext.Default.MemvidDocument);

        Assert.Contains("\"metadata\":", json);
        Assert.Contains("\"role\":", json);
    }

    [Fact]
    public void MemvidFindResponse_Deserializes_Correctly()
    {
        const string json = """
            {
              "results": [
                {"title": "Add IFoo", "text": "Implemented IFoo", "score": 0.92},
                {"title": "Fix bar", "text": "Patched bar", "score": 0.85}
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidFindResponse);

        Assert.NotNull(response);
        Assert.Equal(2, response.Results.Count);
        Assert.Equal("Add IFoo", response.Results[0].Title);
        Assert.Equal(0.92, response.Results[0].Score);
        Assert.Equal("Fix bar", response.Results[1].Title);
    }

    [Fact]
    public void MemvidCreateResponse_Deserializes_CreatedField()
    {
        const string json = """{"created": "/tmp/run-42.mv2"}""";

        var response = JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidCreateResponse);

        Assert.NotNull(response);
        Assert.Equal("/tmp/run-42.mv2", response.Created);
    }

    [Fact]
    public void MemvidPutResponse_Deserializes_FrameId()
    {
        const string json = """{"frame_id": 7}""";

        var response = JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidPutResponse);

        Assert.NotNull(response);
        Assert.Equal(7, response.FrameId);
    }

    [Fact]
    public void MemvidTimelineResponse_Deserializes_Entries()
    {
        const string json = """
            {
              "entries": [
                {"title": "Plan", "label": "planner", "text": "Step 1"},
                {"title": "Build", "label": "builder", "text": "Step 2"}
              ]
            }
            """;

        var response = JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidTimelineResponse);

        Assert.NotNull(response);
        Assert.Equal(2, response.Entries.Count);
        Assert.Equal("planner", response.Entries[0].Label);
    }

    [Fact]
    public void MemvidInfoResponse_Deserializes_SizeBytes()
    {
        const string json = """{"path": "/data/run.mv2", "frames": 42, "size_bytes": 1048576}""";

        var response = JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidInfoResponse);

        Assert.NotNull(response);
        Assert.Equal("/data/run.mv2", response.Path);
        Assert.Equal(42, response.Frames);
        Assert.Equal(1048576L, response.SizeBytes);
    }

    // ── Task 6: ParseJsonOrThrow / BuildArgs ────────────────────

    [Fact]
    public void ParseJsonOrThrow_ThrowsMemvidException_WhenJsonContainsError()
    {
        const string json = """{"error": "store not found"}""";

        var ex = Assert.Throws<MemvidException>(() =>
            MemvidClient.ParseJsonOrThrow<MemvidCreateResponse>(json));

        Assert.Equal("store not found", ex.Message);
    }

    [Fact]
    public void ParseJsonOrThrow_Deserializes_WhenNoError()
    {
        const string json = """{"created": "/tmp/test.mv2"}""";

        var result = MemvidClient.ParseJsonOrThrow<MemvidCreateResponse>(json);

        Assert.Equal("/tmp/test.mv2", result.Created);
    }

    [Fact]
    public void ParseJson_ThrowsMemvidException_WhenDeserializationReturnsNull()
    {
        const string json = "null";

        Assert.Throws<MemvidException>(() =>
            MemvidClient.ParseJson<MemvidCreateResponse>(json));
    }

    [Fact]
    public void BuildArgs_Create_ReturnsCorrectArray()
    {
        var args = MemvidClient.BuildArgs("create", "/tmp/test.mv2");

        Assert.Equal(["-m", "src", "create", "/tmp/test.mv2"], args);
    }

    [Fact]
    public void BuildArgs_Find_ReturnsCorrectArray()
    {
        var args = MemvidClient.BuildArgs("find", "/tmp/test.mv2", "--query", "IFoo");

        Assert.Equal(["-m", "src", "find", "/tmp/test.mv2", "--query", "IFoo"], args);
    }

    [Fact]
    public void BuildArgs_NoExtra_ReturnsBaseArgs()
    {
        var args = MemvidClient.BuildArgs("info");

        Assert.Equal(["-m", "src", "info"], args);
    }

    // ── Task 7: RuntimeOptions defaults ─────────────────────────

    [Fact]
    public void RuntimeOptions_MemvidDefaults_AreCorrect()
    {
        var opts = new RuntimeOptions();

        Assert.False(opts.MemvidEnabled);
        Assert.Equal(".venv/bin/python", opts.MemvidPythonPath);
        Assert.Equal("project/infra/memvid-svc", opts.MemvidSvcDir);
        Assert.Equal(30, opts.MemvidTimeoutSeconds);
        Assert.Equal(5, opts.MemvidSiblingMaxChunks);
        Assert.Equal("auto", opts.MemvidSearchMode);
    }
}
