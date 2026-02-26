# Memvid Run Memory Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add per-run `.mv2` memory stores so agents can query sibling task output before execution, eliminating blind-spot failures like CS0854.

**Architecture:** Python CLI wrapper (`memvid_svc`) around `memvid-sdk`, invoked as subprocesses from a C# `MemvidClient`. `TaskCoordinatorActor` encodes role output after each success and queries sibling stores before builder dispatch. `RolePromptFactory` gets a 7th context layer for sibling memory.

**Tech Stack:** Python 3.12 + memvid-sdk, C# .NET 9 + System.Diagnostics.Process, Akka.NET actors

**Design doc:** `docs/plans/2026-02-26-memvid-run-memory-design.md`

---

### Task 1: Python CLI — scaffold and `create` command

**Files:**
- Create: `project/infra/memvid-svc/src/__init__.py`
- Create: `project/infra/memvid-svc/src/cli.py`
- Create: `project/infra/memvid-svc/src/__main__.py`
- Create: `project/infra/memvid-svc/requirements.txt`

**Step 1: Create directory structure**

```bash
mkdir -p project/infra/memvid-svc/src
```

**Step 2: Write requirements.txt**

```
memvid-sdk>=2.0.0
```

**Step 3: Write `__init__.py`**

Empty file.

**Step 4: Write `__main__.py`**

```python
"""Entry point: python -m memvid_svc <command> [args]."""
from src.cli import main

if __name__ == "__main__":
    main()
```

**Step 5: Write `cli.py` with `create` command**

```python
"""Memvid CLI wrapper for SwarmAssistant subprocess integration."""
import argparse
import json
import sys

from memvid_sdk import create


def cmd_create(args: argparse.Namespace) -> dict:
    """Create a new .mv2 store at the given path."""
    mem = create(args.path)
    mem.commit()
    return {"ok": True, "path": args.path}


def main() -> None:
    parser = argparse.ArgumentParser(prog="memvid_svc")
    sub = parser.add_subparsers(dest="command", required=True)

    p_create = sub.add_parser("create")
    p_create.add_argument("path", help="Path to .mv2 file")

    args = parser.parse_args()
    handlers = {"create": cmd_create}

    try:
        result = handlers[args.command](args)
        json.dump(result, sys.stdout)
        sys.stdout.write("\n")
    except Exception as e:
        json.dump({"error": str(e)}, sys.stderr)
        sys.stderr.write("\n")
        sys.exit(1)
```

**Step 6: Test manually**

```bash
cd project/infra/memvid-svc
pip install -r requirements.txt
python -m src create /tmp/test-run.mv2
# Expected: {"ok": true, "path": "/tmp/test-run.mv2"}
rm /tmp/test-run.mv2
```

**Step 7: Commit**

```bash
git add project/infra/memvid-svc/
git commit -m "feat(memvid): scaffold Python CLI with create command"
```

---

### Task 2: Python CLI — `put` and `find` commands

**Files:**
- Modify: `project/infra/memvid-svc/src/cli.py`

**Step 1: Add `put` command**

Add to `cli.py` after `cmd_create`:

```python
from memvid_sdk import use


def cmd_put(args: argparse.Namespace) -> dict:
    """Put a document into an existing .mv2 store. Reads JSON from stdin."""
    doc = json.load(sys.stdin)
    mem = use("basic", args.path)
    frame_id = mem.put(
        title=doc.get("title", ""),
        label=doc.get("label", ""),
        text=doc["text"],
        metadata=doc.get("metadata", {}),
    )
    mem.commit()
    return {"frame_id": frame_id}
```

**Step 2: Add `find` command**

```python
def cmd_find(args: argparse.Namespace) -> dict:
    """Search an .mv2 store."""
    mem = use("basic", args.path)
    results = mem.find(args.query, k=args.k, mode=args.mode)
    return {
        "results": [
            {"title": r.title, "text": r.text, "score": r.score}
            for r in results
        ]
    }
```

**Step 3: Wire up parsers**

```python
    p_put = sub.add_parser("put")
    p_put.add_argument("path", help="Path to .mv2 file")

    p_find = sub.add_parser("find")
    p_find.add_argument("path", help="Path to .mv2 file")
    p_find.add_argument("--query", required=True, help="Search query")
    p_find.add_argument("--k", type=int, default=5, help="Number of results")
    p_find.add_argument("--mode", default="auto", choices=["lex", "sem", "auto"])
```

Update handlers dict:

```python
    handlers = {"create": cmd_create, "put": cmd_put, "find": cmd_find}
```

**Step 4: Test put + find**

```bash
cd project/infra/memvid-svc
python -m src create /tmp/test-run.mv2
echo '{"title":"task-1","label":"builder","text":"Added IFoo interface to Services/Foo.cs with GetBar() method"}' | python -m src put /tmp/test-run.mv2
echo '{"title":"task-2","label":"builder","text":"Implemented FooTests.cs testing GetBar()"}' | python -m src put /tmp/test-run.mv2
python -m src find /tmp/test-run.mv2 --query "IFoo interface" --k 2
# Expected: {"results": [{"title": "task-1", "text": "Added IFoo...", "score": ...}, ...]}
rm /tmp/test-run.mv2
```

**Step 5: Commit**

```bash
git add project/infra/memvid-svc/src/cli.py
git commit -m "feat(memvid): add put and find CLI commands"
```

---

### Task 3: Python CLI — `timeline` and `info` commands

**Files:**
- Modify: `project/infra/memvid-svc/src/cli.py`

**Step 1: Add `timeline` command**

```python
def cmd_timeline(args: argparse.Namespace) -> dict:
    """List entries in chronological order."""
    mem = use("basic", args.path)
    entries = mem.timeline(limit=args.limit)
    return {
        "entries": [
            {"title": e.title, "label": e.label, "text": e.text}
            for e in entries
        ]
    }
```

**Step 2: Add `info` command**

```python
import os


def cmd_info(args: argparse.Namespace) -> dict:
    """Report store metadata."""
    mem = use("basic", args.path)
    entries = mem.timeline(limit=0)
    return {
        "path": args.path,
        "frames": len(entries),
        "size_bytes": os.path.getsize(args.path),
    }
```

**Step 3: Wire parsers and handlers**

```python
    p_timeline = sub.add_parser("timeline")
    p_timeline.add_argument("path")
    p_timeline.add_argument("--limit", type=int, default=50)

    p_info = sub.add_parser("info")
    p_info.add_argument("path")

    handlers = {
        "create": cmd_create,
        "put": cmd_put,
        "find": cmd_find,
        "timeline": cmd_timeline,
        "info": cmd_info,
    }
```

**Step 4: Test timeline and info**

```bash
cd project/infra/memvid-svc
python -m src create /tmp/test-run.mv2
echo '{"title":"t1","label":"plan","text":"Plan output"}' | python -m src put /tmp/test-run.mv2
python -m src timeline /tmp/test-run.mv2 --limit 10
python -m src info /tmp/test-run.mv2
# Expected: {"path":"/tmp/test-run.mv2","frames":1,"size_bytes":...}
rm /tmp/test-run.mv2
```

**Step 5: Commit**

```bash
git add project/infra/memvid-svc/src/cli.py
git commit -m "feat(memvid): add timeline and info CLI commands"
```

---

### Task 4: Python CLI — automated tests

**Files:**
- Create: `project/infra/memvid-svc/tests/__init__.py`
- Create: `project/infra/memvid-svc/tests/test_cli.py`

**Step 1: Write tests**

```python
"""Tests for memvid_svc CLI commands."""
import json
import os
import subprocess
import tempfile

import pytest

CLI = ["python", "-m", "src"]


@pytest.fixture
def mv2_path(tmp_path):
    return str(tmp_path / "test.mv2")


@pytest.fixture
def populated_store(mv2_path):
    subprocess.run([*CLI, "create", mv2_path], check=True, cwd=SVC_DIR)
    for i in range(3):
        proc = subprocess.run(
            [*CLI, "put", mv2_path],
            input=json.dumps({"title": f"task-{i}", "label": "builder", "text": f"Output for task {i}"}),
            capture_output=True, text=True, check=True, cwd=SVC_DIR,
        )
    return mv2_path


SVC_DIR = os.path.join(os.path.dirname(__file__), "..")


class TestCreate:
    def test_creates_mv2_file(self, mv2_path):
        result = subprocess.run([*CLI, "create", mv2_path], capture_output=True, text=True, cwd=SVC_DIR)
        assert result.returncode == 0
        data = json.loads(result.stdout)
        assert data["ok"] is True
        assert os.path.exists(mv2_path)


class TestPut:
    def test_put_returns_frame_id(self, mv2_path):
        subprocess.run([*CLI, "create", mv2_path], check=True, cwd=SVC_DIR)
        result = subprocess.run(
            [*CLI, "put", mv2_path],
            input=json.dumps({"title": "t1", "label": "plan", "text": "Plan content"}),
            capture_output=True, text=True, cwd=SVC_DIR,
        )
        assert result.returncode == 0
        data = json.loads(result.stdout)
        assert "frame_id" in data


class TestFind:
    def test_find_returns_results(self, populated_store):
        result = subprocess.run(
            [*CLI, "find", populated_store, "--query", "task 1", "--k", "2"],
            capture_output=True, text=True, cwd=SVC_DIR,
        )
        assert result.returncode == 0
        data = json.loads(result.stdout)
        assert "results" in data
        assert len(data["results"]) <= 2


class TestTimeline:
    def test_timeline_returns_entries(self, populated_store):
        result = subprocess.run(
            [*CLI, "timeline", populated_store, "--limit", "10"],
            capture_output=True, text=True, cwd=SVC_DIR,
        )
        assert result.returncode == 0
        data = json.loads(result.stdout)
        assert len(data["entries"]) == 3


class TestInfo:
    def test_info_returns_metadata(self, populated_store):
        result = subprocess.run(
            [*CLI, "info", populated_store],
            capture_output=True, text=True, cwd=SVC_DIR,
        )
        assert result.returncode == 0
        data = json.loads(result.stdout)
        assert data["frames"] == 3
        assert data["size_bytes"] > 0
```

**Step 2: Run tests**

```bash
cd project/infra/memvid-svc
pip install pytest
pytest tests/ -v
# Expected: 5 passed
```

**Step 3: Commit**

```bash
git add project/infra/memvid-svc/tests/
git commit -m "test(memvid): add CLI integration tests"
```

---

### Task 5: C# models — MemvidDocument and MemvidResult

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Memvid/MemvidModels.cs`

**Step 1: Write the failing test**

Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidClientTests.cs`

```csharp
using SwarmAssistant.Runtime.Memvid;

namespace SwarmAssistant.Runtime.Tests;

public sealed class MemvidModelTests
{
    [Fact]
    public void MemvidDocument_Serializes_To_Expected_Json()
    {
        var doc = new MemvidDocument("task-1", "builder", "Added IFoo interface");
        var json = System.Text.Json.JsonSerializer.Serialize(doc, MemvidJsonContext.Default.MemvidDocument);

        Assert.Contains("\"title\":\"task-1\"", json);
        Assert.Contains("\"label\":\"builder\"", json);
        Assert.Contains("\"text\":\"Added IFoo interface\"", json);
    }

    [Fact]
    public void MemvidFindResponse_Deserializes_Results()
    {
        var json = """{"results":[{"title":"t1","text":"content","score":0.95}]}""";
        var response = System.Text.Json.JsonSerializer.Deserialize(json, MemvidJsonContext.Default.MemvidFindResponse);

        Assert.NotNull(response);
        Assert.Single(response!.Results);
        Assert.Equal("t1", response.Results[0].Title);
        Assert.Equal(0.95, response.Results[0].Score, precision: 2);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "MemvidModelTests" --verbosity quiet
# Expected: FAIL — MemvidDocument, MemvidJsonContext not found
```

**Step 3: Write models**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwarmAssistant.Runtime.Memvid;

public sealed record MemvidDocument(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata = null);

public sealed record MemvidResult(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("score")] double Score);

public sealed record MemvidFindResponse(
    [property: JsonPropertyName("results")] List<MemvidResult> Results);

public sealed record MemvidCreateResponse(
    [property: JsonPropertyName("ok")] bool Ok);

public sealed record MemvidPutResponse(
    [property: JsonPropertyName("frame_id")] int FrameId);

public sealed record MemvidTimelineEntry(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("text")] string Text);

public sealed record MemvidTimelineResponse(
    [property: JsonPropertyName("entries")] List<MemvidTimelineEntry> Entries);

public sealed record MemvidInfoResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("frames")] int Frames,
    [property: JsonPropertyName("size_bytes")] long SizeBytes);

public sealed record MemvidErrorResponse(
    [property: JsonPropertyName("error")] string Error);

[JsonSerializable(typeof(MemvidDocument))]
[JsonSerializable(typeof(MemvidFindResponse))]
[JsonSerializable(typeof(MemvidCreateResponse))]
[JsonSerializable(typeof(MemvidPutResponse))]
[JsonSerializable(typeof(MemvidTimelineResponse))]
[JsonSerializable(typeof(MemvidInfoResponse))]
[JsonSerializable(typeof(MemvidErrorResponse))]
internal sealed partial class MemvidJsonContext : JsonSerializerContext
{
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "MemvidModelTests" --verbosity quiet
# Expected: 2 passed
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Memvid/ project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidClientTests.cs
git commit -m "feat(memvid): add C# models for memvid CLI JSON protocol"
```

---

### Task 6: C# MemvidClient — subprocess wrapper

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Memvid/MemvidClient.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidClientTests.cs`

**Step 1: Write the failing test**

Add to `MemvidClientTests.cs`:

```csharp
public sealed class MemvidClientTests
{
    [Fact]
    public void ParseCreateResponse_Returns_Ok()
    {
        var json = """{"ok": true, "path": "/tmp/test.mv2"}""";
        var response = MemvidClient.ParseJson<MemvidCreateResponse>(json);
        Assert.True(response.Ok);
    }

    [Fact]
    public void ParseFindResponse_Returns_Results()
    {
        var json = """{"results":[{"title":"t1","text":"content","score":0.9}]}""";
        var response = MemvidClient.ParseJson<MemvidFindResponse>(json);
        Assert.Single(response.Results);
    }

    [Fact]
    public void ParseErrorResponse_Throws()
    {
        var json = """{"error": "file not found"}""";
        var ex = Assert.Throws<MemvidException>(() => MemvidClient.ParseJsonOrThrow<MemvidCreateResponse>(json));
        Assert.Contains("file not found", ex.Message);
    }

    [Fact]
    public void BuildArgs_Create_Produces_Correct_Args()
    {
        var args = MemvidClient.BuildArgs("create", "/tmp/test.mv2");
        Assert.Equal(new[] { "-m", "src", "create", "/tmp/test.mv2" }, args);
    }

    [Fact]
    public void BuildArgs_Find_With_Options_Produces_Correct_Args()
    {
        var args = MemvidClient.BuildArgs("find", "/tmp/test.mv2", "--query", "IFoo", "--k", "3", "--mode", "auto");
        Assert.Equal(new[] { "-m", "src", "find", "/tmp/test.mv2", "--query", "IFoo", "--k", "3", "--mode", "auto" }, args);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "MemvidClientTests" --verbosity quiet
# Expected: FAIL — MemvidClient, MemvidException not found
```

**Step 3: Write MemvidClient**

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace SwarmAssistant.Runtime.Memvid;

public sealed class MemvidException : Exception
{
    public MemvidException(string message) : base(message) { }
}

public sealed class MemvidClient
{
    private readonly string _pythonPath;
    private readonly string _svcDir;
    private readonly int _timeoutMs;
    private readonly ILogger<MemvidClient> _logger;

    public MemvidClient(
        string pythonPath,
        string svcDir,
        int timeoutMs,
        ILogger<MemvidClient> logger)
    {
        _pythonPath = pythonPath;
        _svcDir = svcDir;
        _timeoutMs = timeoutMs;
        _logger = logger;
    }

    public async Task<bool> CreateStoreAsync(string path, CancellationToken ct)
    {
        var result = await RunAsync("create", null, ct, path);
        var response = ParseJsonOrThrow<MemvidCreateResponse>(result);
        return response.Ok;
    }

    public async Task<int> PutAsync(string path, MemvidDocument doc, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(doc, MemvidJsonContext.Default.MemvidDocument);
        var result = await RunAsync("put", input, ct, path);
        var response = ParseJsonOrThrow<MemvidPutResponse>(result);
        return response.FrameId;
    }

    public async Task<List<MemvidResult>> FindAsync(
        string path, string query, int k, string mode, CancellationToken ct)
    {
        var result = await RunAsync("find", null, ct, path,
            "--query", query, "--k", k.ToString(), "--mode", mode);
        var response = ParseJsonOrThrow<MemvidFindResponse>(result);
        return response.Results;
    }

    public async Task<List<MemvidTimelineEntry>> TimelineAsync(
        string path, int limit, CancellationToken ct)
    {
        var result = await RunAsync("timeline", null, ct, path, "--limit", limit.ToString());
        var response = ParseJsonOrThrow<MemvidTimelineResponse>(result);
        return response.Entries;
    }

    internal static string[] BuildArgs(string command, params string[] extra)
    {
        var args = new List<string> { "-m", "src", command };
        args.AddRange(extra);
        return args.ToArray();
    }

    internal static T ParseJson<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json)
            ?? throw new MemvidException($"Failed to deserialize: {json}");
    }

    internal static T ParseJsonOrThrow<T>(string json)
    {
        // Check for error response first
        try
        {
            var errorCheck = JsonSerializer.Deserialize<MemvidErrorResponse>(json);
            if (errorCheck?.Error is not null)
            {
                throw new MemvidException(errorCheck.Error);
            }
        }
        catch (JsonException)
        {
            // Not an error response, continue parsing
        }

        return ParseJson<T>(json);
    }

    private async Task<string> RunAsync(
        string command, string? stdin, CancellationToken ct, params string[] extra)
    {
        var args = BuildArgs(command, extra);

        _logger.LogDebug("memvid: {Command} {Args}", command, string.Join(" ", extra));

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            WorkingDirectory = _svcDir,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)
            ?? throw new MemvidException("Failed to start memvid process");

        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("memvid exited {Code}: {Stderr}", process.ExitCode, stderr);
            throw new MemvidException($"memvid {command} failed: {stderr.Trim()}");
        }

        return stdout.Trim();
    }
}
```

**Step 4: Run test to verify it passes**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "MemvidClientTests" --verbosity quiet
# Expected: 5 passed
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Memvid/MemvidClient.cs project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidClientTests.cs
git commit -m "feat(memvid): add MemvidClient subprocess wrapper with parsing"
```

---

### Task 7: RuntimeOptions + feature flag

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs` (after line ~245)

**Step 1: Write the failing test**

Add to `MemvidClientTests.cs`:

```csharp
using SwarmAssistant.Runtime.Configuration;

public sealed class MemvidOptionsTests
{
    [Fact]
    public void Defaults_Memvid_Disabled()
    {
        var options = new RuntimeOptions();
        Assert.False(options.MemvidEnabled);
        Assert.Equal("python3", options.MemvidPythonPath);
        Assert.Equal(30, options.MemvidTimeoutSeconds);
        Assert.Equal(5, options.MemvidSiblingMaxChunks);
        Assert.Equal("auto", options.MemvidSearchMode);
    }
}
```

**Step 2: Run test to verify it fails**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "MemvidOptionsTests" --verbosity quiet
# Expected: FAIL — MemvidEnabled property not found
```

**Step 3: Add properties to RuntimeOptions.cs**

Add after the CodeIndex section (around line 245):

```csharp
    // ── Memvid Run Memory ───────────────────────────────────────
    public bool MemvidEnabled { get; init; } = false;
    public string MemvidPythonPath { get; init; } = "python3";
    public string MemvidSvcDir { get; init; } = "project/infra/memvid-svc";
    public int MemvidTimeoutSeconds { get; init; } = 30;
    public int MemvidSiblingMaxChunks { get; init; } = 5;
    public string MemvidSearchMode { get; init; } = "auto";
```

**Step 4: Run test to verify it passes**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "MemvidOptionsTests" --verbosity quiet
# Expected: 1 passed
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidClientTests.cs
git commit -m "feat(memvid): add MemvidEnabled feature flag to RuntimeOptions"
```

---

### Task 8: Wire MemvidClient into DI and Worker

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Program.cs` (after Langfuse registration ~line 62)
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Worker.cs` (near CodeIndexActor creation ~line 254)

**Step 1: Add DI registration in Program.cs**

After the Langfuse service registration block:

```csharp
if (bootstrapOptions.MemvidEnabled)
{
    builder.Services.AddSingleton(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<RuntimeOptions>>().Value;
        var logger = sp.GetRequiredService<ILogger<MemvidClient>>();
        return new MemvidClient(
            opts.MemvidPythonPath,
            opts.MemvidSvcDir,
            opts.MemvidTimeoutSeconds * 1000,
            logger);
    });
}
```

**Step 2: Add MemvidClient to TaskCoordinatorActor constructor wiring in Worker.cs**

Find where TaskCoordinatorActor is created (in Worker.cs) and add `memvidClient` parameter, following the `langfuseScoreWriter` pattern — pass `null` if not enabled.

Look for the `Props.Create(() => new TaskCoordinatorActor(` call and add:

```csharp
memvidClient: _options.MemvidEnabled ? _serviceProvider.GetService<MemvidClient>() : null,
```

**Step 3: Build to verify compilation**

```bash
dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 0 errors (will have warnings about unused memvidClient param until Task 9)
```

**Step 4: Run all tests**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 591+ passed
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Program.cs project/dotnet/src/SwarmAssistant.Runtime/Worker.cs
git commit -m "feat(memvid): wire MemvidClient into DI and Worker"
```

---

### Task 9: TaskCoordinatorActor — create run.mv2 after planning

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`

**Step 1: Add MemvidClient field and constructor parameter**

Add field (near `_langfuseScoreWriter` ~line 68):

```csharp
private readonly MemvidClient? _memvidClient;
```

Add constructor parameter (after `langfuseScoreWriter`):

```csharp
MemvidClient? memvidClient = null
```

Assign in constructor body:

```csharp
_memvidClient = memvidClient;
```

**Step 2: Add helper to resolve memory directory**

Add private method (near `TryWriteReviewerVerdictAsync`):

```csharp
private string GetMemoryDir()
{
    var basePath = _worktreePath ?? Directory.GetCurrentDirectory();
    return Path.Combine(basePath, ".swarm", "memory");
}

private string GetRunMemoryPath() => Path.Combine(GetMemoryDir(), "run.mv2");

private string GetTaskMemoryPath(string taskId) =>
    Path.Combine(GetMemoryDir(), "tasks", $"{taskId}.mv2");
```

**Step 3: Add run.mv2 creation after planning succeeds**

In `OnRoleSucceededAsync`, inside the `case SwarmRole.Planner:` block (after `StoreBlackboard("planner_output", message.Output);` around line 435), add:

```csharp
await TryCreateRunMemoryAsync(message.Output);
```

Add the method:

```csharp
private async Task TryCreateRunMemoryAsync(string planOutput)
{
    if (_memvidClient is null) return;

    try
    {
        var memDir = GetMemoryDir();
        Directory.CreateDirectory(memDir);
        Directory.CreateDirectory(Path.Combine(memDir, "tasks"));

        var runPath = GetRunMemoryPath();
        await _memvidClient.CreateStoreAsync(runPath, CancellationToken.None);
        await _memvidClient.PutAsync(runPath, new MemvidDocument(
            Title: _title,
            Label: "plan",
            Text: planOutput,
            Metadata: new Dictionary<string, string>
            {
                ["task_id"] = _taskId,
                ["run_id"] = _runId ?? "",
            }), CancellationToken.None);

        _logger.LogInformation("Created run memory at {Path}", runPath);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to create run memory; continuing without memvid");
    }
}
```

**Step 4: Build and run tests**

```bash
dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet && dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 0 errors, 591+ passed
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs
git commit -m "feat(memvid): create run.mv2 after planning succeeds"
```

---

### Task 10: TaskCoordinatorActor — encode task output after role success

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`

**Step 1: Add encoding after builder and reviewer success**

In `OnRoleSucceededAsync`, after the `case SwarmRole.Builder:` block stores to blackboard (~line 443), add:

```csharp
await TryEncodeTaskMemoryAsync(message.Role, message.Output, message.Confidence);
```

Add the same call after `case SwarmRole.Reviewer:` stores to blackboard.

**Step 2: Implement the method**

```csharp
private async Task TryEncodeTaskMemoryAsync(SwarmRole role, string output, double confidence)
{
    if (_memvidClient is null) return;

    try
    {
        var taskPath = GetTaskMemoryPath(_taskId);
        if (!File.Exists(taskPath))
        {
            await _memvidClient.CreateStoreAsync(taskPath, CancellationToken.None);
        }

        await _memvidClient.PutAsync(taskPath, new MemvidDocument(
            Title: $"{_title} — {role}",
            Label: role.ToString().ToLowerInvariant(),
            Text: output,
            Metadata: new Dictionary<string, string>
            {
                ["task_id"] = _taskId,
                ["role"] = role.ToString().ToLowerInvariant(),
                ["confidence"] = confidence.ToString("F2"),
                ["run_id"] = _runId ?? "",
            }), CancellationToken.None);

        _logger.LogDebug("Encoded {Role} output to {Path}", role, taskPath);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to encode {Role} output to memvid; continuing", role);
    }
}
```

**Step 3: Build and run tests**

```bash
dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet && dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 0 errors, 591+ passed
```

**Step 4: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs
git commit -m "feat(memvid): encode task output to .mv2 after role success"
```

---

### Task 11: TaskCoordinatorActor — query sibling context before builder dispatch

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`

**Step 1: Add sibling context query**

In `DecideAndExecuteAsync`, before the builder dispatch (around line 1105 where the "Build" action is handled), add a call to build sibling context:

```csharp
var siblingContext = await TryBuildSiblingContextAsync();
```

Pass `siblingContext` to the prompt building — it will be integrated via RolePromptFactory in Task 12.

**Step 2: Implement sibling context builder**

```csharp
private async Task<string?> TryBuildSiblingContextAsync()
{
    if (_memvidClient is null) return null;

    try
    {
        var tasksDir = Path.Combine(GetMemoryDir(), "tasks");
        if (!Directory.Exists(tasksDir)) return null;

        var siblingFiles = Directory.GetFiles(tasksDir, "*.mv2")
            .Where(f => !Path.GetFileNameWithoutExtension(f).Equals(_taskId, StringComparison.Ordinal))
            .ToList();

        if (siblingFiles.Count == 0) return null;

        var contextParts = new List<string>();
        contextParts.Add("--- Sibling Task Context ---");

        foreach (var siblingPath in siblingFiles)
        {
            var siblingId = Path.GetFileNameWithoutExtension(siblingPath);
            var results = await _memvidClient.FindAsync(
                siblingPath,
                _description,
                _options.MemvidSiblingMaxChunks,
                _options.MemvidSearchMode,
                CancellationToken.None);

            foreach (var r in results)
            {
                contextParts.Add($"  [{siblingId}] {r.Title}: {r.Text[..Math.Min(r.Text.Length, 500)]}");
            }
        }

        contextParts.Add("--- End Sibling Task Context ---");

        if (contextParts.Count <= 2) return null; // only headers, no actual content

        var context = string.Join("\n", contextParts);
        _logger.LogInformation("Built sibling context from {Count} stores ({Length} chars)",
            siblingFiles.Count, context.Length);
        return context;
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to build sibling context; continuing without");
        return null;
    }
}
```

**Step 3: Store sibling context in field for RolePromptFactory to consume**

Add field:

```csharp
private string? _siblingContext;
```

In the Build action handler, before dispatching:

```csharp
_siblingContext = await TryBuildSiblingContextAsync();
```

**Step 4: Build and run tests**

```bash
dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet && dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 0 errors, 591+ passed
```

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs
git commit -m "feat(memvid): query sibling .mv2 stores before builder dispatch"
```

---

### Task 12: RolePromptFactory — 7th context layer

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs`

**Step 1: Write the failing test**

Add to test file:

```csharp
public sealed class RolePromptFactoryMemvidTests
{
    [Fact]
    public void BuildPrompt_Includes_Sibling_Context_When_Provided()
    {
        var command = CreateBuilderCommand(siblingContext: "--- Sibling Task Context ---\n  [task-1] IFoo added\n--- End ---");
        var prompt = RolePromptFactory.BuildPrompt(command);

        Assert.Contains("Sibling Task Context", prompt);
        Assert.Contains("IFoo added", prompt);
    }

    [Fact]
    public void BuildPrompt_Excludes_Sibling_Context_When_Null()
    {
        var command = CreateBuilderCommand(siblingContext: null);
        var prompt = RolePromptFactory.BuildPrompt(command);

        Assert.DoesNotContain("Sibling Task Context", prompt);
    }
}
```

Note: The `CreateBuilderCommand` helper will need to be adapted to match the existing test patterns in the codebase. Check existing `RolePromptFactory` tests for the helper pattern.

**Step 2: Run test to verify it fails**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --filter "RolePromptFactoryMemvidTests" --verbosity quiet
# Expected: FAIL — siblingContext parameter not found
```

**Step 3: Add siblingContext parameter to BuildPrompt**

In `RolePromptFactory.cs`, add `string? siblingContext = null` parameter to the `BuildPrompt` method (line ~25).

After the Langfuse context layer (line ~143), add:

```csharp
if (siblingContext is not null && command.Role is SwarmRole.Planner or SwarmRole.Builder or SwarmRole.Reviewer)
{
    layers.Add(siblingContext);
}
```

**Step 4: Pass siblingContext from TaskCoordinatorActor**

In the Build action handler in `TaskCoordinatorActor.cs`, find where `RolePromptFactory.BuildPrompt` is called and pass `siblingContext: _siblingContext`.

**Step 5: Run all tests**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 591+ passed (all existing + 2 new)
```

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidClientTests.cs
git commit -m "feat(memvid): add sibling context as 7th RolePromptFactory layer"
```

---

### Task 13: Update .gitignore and meta.json support

**Files:**
- Modify: `.gitignore`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`

**Step 1: Add .swarm/memory/ to .gitignore**

Append to `.gitignore`:

```
# Memvid run memory stores
.swarm/memory/
```

**Step 2: Write meta.json on run creation**

In `TryCreateRunMemoryAsync`, after creating run.mv2, write meta.json:

```csharp
var metaPath = Path.Combine(memDir, "meta.json");
var meta = new
{
    run_id = _runId ?? "",
    task_id = _taskId,
    langfuse_trace_id = _runId ?? "",
    created_at = DateTimeOffset.UtcNow.ToString("o"),
    tasks = new Dictionary<string, string>(),
};
var metaJson = JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(metaPath, metaJson);
```

**Step 3: Build and run tests**

```bash
dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet && dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 0 errors, 591+ passed
```

**Step 4: Commit**

```bash
git add .gitignore project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs
git commit -m "feat(memvid): add .gitignore rule and meta.json for run tracking"
```

---

### Task 14: Integration test — full memvid lifecycle

**Files:**
- Create or modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidIntegrationTests.cs`

**Step 1: Write integration test**

```csharp
/// <summary>
/// Tests the full memvid lifecycle: create → put → find → verify sibling context.
/// Requires Python 3.8+ and memvid-sdk installed. Skipped in CI if not available.
/// </summary>
public sealed class MemvidIntegrationTests
{
    private static bool MemvidAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("python3", "-c \"import memvid_sdk\"")
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            var p = Process.Start(psi);
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    [Fact(Skip = "Requires memvid-sdk installed locally")]
    public async Task Full_Lifecycle_Create_Put_Find()
    {
        // Test create → put → find round-trip
        // Only run locally where memvid-sdk is installed
    }
}
```

This is a skeleton — the actual assertions depend on having memvid-sdk available. Mark as skipped for CI.

**Step 2: Run all tests to confirm nothing broke**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 591+ passed, 1 skipped
```

**Step 3: Commit**

```bash
git add project/dotnet/tests/SwarmAssistant.Runtime.Tests/MemvidIntegrationTests.cs
git commit -m "test(memvid): add integration test skeleton for full lifecycle"
```

---

### Task 15: Final verification and push

**Step 1: Run full test suite**

```bash
dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 591+ passed, 0 failed
```

**Step 2: Run Python CLI tests**

```bash
cd project/infra/memvid-svc && pytest tests/ -v
# Expected: 5 passed
```

**Step 3: Verify build**

```bash
dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet
# Expected: 0 errors
```

**Step 4: Push all commits**

```bash
git push
```
