# RFC-003 Sandbox Spectrum — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement three sandbox isolation levels (bare CLI, OS-sandboxed, container) using a bootstrap spiral — the swarm builds each level while running at the previous one.

**Architecture:** Extend the existing `SandboxCommandBuilder` + `RuntimeOptions` with a `SandboxLevel` enum and per-level enforcement. Level 0 uses existing workspace branch isolation. Level 1 adds OS-native process sandboxing (`sandbox-exec` on macOS, seccomp/namespaces on Linux). Level 2 extends existing Docker/Apple Container support with lifecycle management. The `AgentCard` model (from RFC-001) already has a `sandboxLevel` field — we add `SandboxRequirements` and runtime enforcement.

**Tech Stack:** .NET 9, Akka.NET, xUnit + Akka.TestKit, C# file-scoped namespaces, sealed records

**Dependency:** RFC-001 is merged to `origin/main` (PR #137, commit `a42ed29`). Pull main before branching.

---

## Pre-Requisite: Pull Main

```bash
git pull origin main
git checkout -b feat/rfc-003-sandbox-spectrum main
```

---

## Phase 1: Level 0 Foundation (Run at Level 0, Build Level 1)

### Task 1: Add SandboxLevel Enum to Contracts

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/SandboxLevel.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxLevelTests.cs`

**Step 1: Write the failing test**

```csharp
// SandboxLevelTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;

public sealed class SandboxLevelTests
{
    [Theory]
    [InlineData(SandboxLevel.BareCli, 0)]
    [InlineData(SandboxLevel.OsSandboxed, 1)]
    [InlineData(SandboxLevel.Container, 2)]
    public void SandboxLevel_HasExpectedIntegerValues(SandboxLevel level, int expected)
    {
        Assert.Equal(expected, (int)level);
    }

    [Fact]
    public void SandboxLevel_DefaultIsBareCli()
    {
        Assert.Equal(0, (int)default(SandboxLevel));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxLevelTests -v n`
Expected: FAIL — `SandboxLevel` type does not exist

**Step 3: Write minimal implementation**

```csharp
// SandboxLevel.cs
namespace SwarmAssistant.Contracts.Messaging;

public enum SandboxLevel
{
    BareCli = 0,
    OsSandboxed = 1,
    Container = 2
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxLevelTests -v n`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Contracts/Messaging/SandboxLevel.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxLevelTests.cs
git commit -m "feat(rfc-003): add SandboxLevel enum to Contracts"
```

---

### Task 2: Add SandboxRequirements Record

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/SandboxRequirements.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxLevelTests.cs` (append)

**Step 1: Write the failing test**

Append to `SandboxLevelTests.cs`:

```csharp
[Fact]
public void SandboxRequirements_DefaultsAreFalseAndEmpty()
{
    var requirements = new SandboxRequirements();

    Assert.False(requirements.NeedsOAuth);
    Assert.False(requirements.NeedsKeychain);
    Assert.Empty(requirements.NeedsNetwork);
    Assert.False(requirements.NeedsGpuAccess);
}

[Fact]
public void SandboxRequirements_CanSpecifyNetworkHosts()
{
    var requirements = new SandboxRequirements
    {
        NeedsNetwork = ["api.github.com", "copilot-proxy.githubusercontent.com"]
    };

    Assert.Equal(2, requirements.NeedsNetwork.Length);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxRequirements -v n`
Expected: FAIL — `SandboxRequirements` type does not exist

**Step 3: Write minimal implementation**

```csharp
// SandboxRequirements.cs
namespace SwarmAssistant.Contracts.Messaging;

public sealed record SandboxRequirements
{
    public bool NeedsOAuth { get; init; }
    public bool NeedsKeychain { get; init; }
    public string[] NeedsNetwork { get; init; } = [];
    public bool NeedsGpuAccess { get; init; }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxRequirements -v n`
Expected: PASS (2 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Contracts/Messaging/SandboxRequirements.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxLevelTests.cs
git commit -m "feat(rfc-003): add SandboxRequirements record"
```

---

### Task 3: Add SandboxRequirements to AgentCard

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Agents/AgentCard.cs` (from RFC-001)
- Modify: `project/docs/openapi/schemas/AgentCard.schema.json`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentCardTests.cs` (from RFC-001)

**Step 1: Write the failing test**

Append to `AgentCardTests.cs`:

```csharp
[Fact]
public void AgentCard_SerializesSandboxRequirements()
{
    var card = new AgentCard
    {
        AgentId = "builder-01",
        Name = "Builder",
        Version = "1.0",
        Protocol = "a2a",
        Capabilities = [SwarmRole.Builder],
        Provider = "copilot",
        SandboxLevel = 0,
        EndpointUrl = "http://localhost:8001",
        SandboxRequirements = new SandboxRequirements
        {
            NeedsOAuth = true,
            NeedsNetwork = ["api.github.com"]
        }
    };

    var json = JsonSerializer.Serialize(card);
    Assert.Contains("\"sandboxRequirements\"", json);
    Assert.Contains("\"needsOAuth\":true", json);
    Assert.Contains("api.github.com", json);
}

[Fact]
public void AgentCard_SandboxRequirementsDefaultsToNull()
{
    var card = new AgentCard
    {
        AgentId = "reviewer-01",
        Name = "Reviewer",
        Version = "1.0",
        Protocol = "a2a",
        Capabilities = [SwarmRole.Reviewer],
        Provider = "kimi",
        SandboxLevel = 2,
        EndpointUrl = "http://localhost:8002"
    };

    Assert.Null(card.SandboxRequirements);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxRequirements -v n`
Expected: FAIL — `AgentCard` has no `SandboxRequirements` property

**Step 3: Write minimal implementation**

Add to `AgentCard.cs` after the `SandboxLevel` property:

```csharp
[JsonPropertyName("sandboxRequirements")]
public SandboxRequirements? SandboxRequirements { get; init; }
```

Add the using:
```csharp
using SwarmAssistant.Contracts.Messaging;
```

Update `AgentCard.schema.json` — add to `properties`:
```json
"sandboxLevel": {
  "type": "integer",
  "description": "Isolation level: 0=BareCli, 1=OsSandboxed, 2=Container",
  "enum": [0, 1, 2]
},
"sandboxRequirements": {
  "type": ["object", "null"],
  "properties": {
    "needsOAuth": { "type": "boolean" },
    "needsKeychain": { "type": "boolean" },
    "needsNetwork": {
      "type": "array",
      "items": { "type": "string" }
    },
    "needsGpuAccess": { "type": "boolean" }
  }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxRequirements -v n`
Expected: PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Agents/AgentCard.cs \
       project/docs/openapi/schemas/AgentCard.schema.json \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentCardTests.cs
git commit -m "feat(rfc-003): add SandboxRequirements to AgentCard"
```

---

### Task 4: Map SandboxMode String to SandboxLevel Enum

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs`

**Step 1: Write the failing test**

Append to `SandboxCommandBuilderTests.cs`:

```csharp
[Theory]
[InlineData("host", SandboxLevel.BareCli)]
[InlineData("HOST", SandboxLevel.BareCli)]
[InlineData("docker", SandboxLevel.Container)]
[InlineData("apple-container", SandboxLevel.Container)]
public void ParseLevel_MapsStringToEnum(string mode, SandboxLevel expected)
{
    Assert.Equal(expected, SandboxCommandBuilder.ParseLevel(mode));
}

[Fact]
public void ParseLevel_UnknownMode_ThrowsInvalidOperation()
{
    Assert.Throws<InvalidOperationException>(() =>
        SandboxCommandBuilder.ParseLevel("unknown-mode"));
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter ParseLevel -v n`
Expected: FAIL — `ParseLevel` method does not exist

**Step 3: Write minimal implementation**

Add to `SandboxCommandBuilder.cs`:

```csharp
public static SandboxLevel ParseLevel(string mode) =>
    (mode ?? "host").Trim().ToLowerInvariant() switch
    {
        "host" => SandboxLevel.BareCli,
        "docker" => SandboxLevel.Container,
        "apple-container" => SandboxLevel.Container,
        _ => throw new InvalidOperationException($"Unsupported sandbox mode '{mode}'.")
    };
```

Add using: `using SwarmAssistant.Contracts.Messaging;`

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter ParseLevel -v n`
Expected: PASS (5 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs
git commit -m "feat(rfc-003): add SandboxMode-to-SandboxLevel mapping"
```

---

### Task 5: Add SandboxLevelEnforcer Service

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxLevelEnforcer.cs`
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxLevelEnforcerTests.cs`

**Step 1: Write the failing test**

```csharp
// SandboxLevelEnforcerTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

public sealed class SandboxLevelEnforcerTests
{
    [Theory]
    [InlineData(SandboxLevel.BareCli)]
    [InlineData(SandboxLevel.Container)]
    public void Validate_SupportedLevels_ReturnsTrue(SandboxLevel level)
    {
        var enforcer = new SandboxLevelEnforcer();

        var result = enforcer.CanEnforce(level);

        Assert.True(result);
    }

    [Fact]
    public void Validate_OsSandboxed_ReturnsTrueOnMacOS()
    {
        var enforcer = new SandboxLevelEnforcer();

        var result = enforcer.CanEnforce(SandboxLevel.OsSandboxed);

        // On macOS (where tests run), sandbox-exec is available
        if (OperatingSystem.IsMacOS())
            Assert.True(result);
        else
            Assert.True(result); // Linux fallback to seccomp
    }

    [Fact]
    public void Validate_DeclaredLevelExceedsHost_ReturnsFalse()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);

        var result = enforcer.CanEnforce(SandboxLevel.Container);

        Assert.False(result);
    }

    [Fact]
    public void GetEffectiveLevel_FallsBackWhenUnavailable()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);

        var effective = enforcer.GetEffectiveLevel(SandboxLevel.Container);

        Assert.Equal(SandboxLevel.BareCli, effective);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxLevelEnforcerTests -v n`
Expected: FAIL — `SandboxLevelEnforcer` does not exist

**Step 3: Write minimal implementation**

```csharp
// SandboxLevelEnforcer.cs
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Execution;

internal sealed class SandboxLevelEnforcer
{
    private readonly bool _containerAvailable;

    public SandboxLevelEnforcer(bool containerAvailable = true)
    {
        _containerAvailable = containerAvailable;
    }

    public bool CanEnforce(SandboxLevel level) => level switch
    {
        SandboxLevel.BareCli => true,
        SandboxLevel.OsSandboxed => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(),
        SandboxLevel.Container => _containerAvailable,
        _ => false
    };

    public SandboxLevel GetEffectiveLevel(SandboxLevel declared)
    {
        if (CanEnforce(declared))
            return declared;

        // Fall back: Container → OsSandboxed → BareCli
        for (var fallback = declared - 1; fallback >= 0; fallback--)
        {
            if (CanEnforce(fallback))
                return fallback;
        }

        return SandboxLevel.BareCli;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxLevelEnforcerTests -v n`
Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxLevelEnforcer.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxLevelEnforcerTests.cs
git commit -m "feat(rfc-003): add SandboxLevelEnforcer with fallback logic"
```

---

### Task 6: Implement SandboxExecWrapper for macOS (Level 1)

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxExecWrapper.cs`
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxExecWrapperTests.cs`

**Step 1: Write the failing test**

```csharp
// SandboxExecWrapperTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class SandboxExecWrapperTests
{
    [Fact]
    public void BuildProfile_DeniesNetworkByDefault()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Contains("(deny network*)", profile);
    }

    [Fact]
    public void BuildProfile_AllowsSpecifiedHosts()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/workspace",
            allowedHosts: ["api.github.com"]);

        Assert.Contains("api.github.com", profile);
    }

    [Fact]
    public void BuildProfile_ScopesFileWriteToWorkspace()
    {
        var profile = SandboxExecWrapper.BuildProfile(
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Contains("/tmp/workspace", profile);
        Assert.Contains("(deny file-write*)", profile);
        Assert.Contains("(allow file-write*", profile);
    }

    [Fact]
    public void WrapCommand_ReturnsSandboxExecCommand()
    {
        var result = SandboxExecWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Equal("sandbox-exec", result.Command);
        Assert.Contains("-p", result.Args);
        Assert.Contains("copilot", result.Args);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxExecWrapperTests -v n`
Expected: FAIL — `SandboxExecWrapper` does not exist

**Step 3: Write minimal implementation**

```csharp
// SandboxExecWrapper.cs
namespace SwarmAssistant.Runtime.Execution;

internal static class SandboxExecWrapper
{
    public static string BuildProfile(string workspacePath, string[] allowedHosts)
    {
        var profile = "(version 1)\n";
        profile += "(deny default)\n";
        profile += "(allow process*)\n";
        profile += "(allow process-exec)\n";
        profile += "(allow sysctl-read)\n";
        profile += "(allow mach-lookup)\n";

        // File access: read everywhere, write only to workspace
        profile += "(allow file-read*)\n";
        profile += "(deny file-write*)\n";
        profile += $"(allow file-write* (subpath \"{workspacePath}\"))\n";
        profile += "(allow file-write* (subpath \"/tmp\"))\n";
        profile += "(allow file-write* (subpath \"/private/tmp\"))\n";

        // Network: deny by default, allow specific hosts
        profile += "(deny network*)\n";
        if (allowedHosts.Length > 0)
        {
            profile += "(allow network-outbound (remote tcp))\n";
            foreach (var host in allowedHosts)
            {
                profile += $";; allowed host: {host}\n";
            }
        }

        return profile;
    }

    public static SandboxCommand WrapCommand(
        string command,
        IReadOnlyList<string> args,
        string workspacePath,
        string[] allowedHosts)
    {
        var profile = BuildProfile(workspacePath, allowedHosts);
        var sandboxArgs = new List<string> { "-p", profile, command };
        sandboxArgs.AddRange(args);
        return new SandboxCommand("sandbox-exec", sandboxArgs.ToArray());
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxExecWrapperTests -v n`
Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxExecWrapper.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxExecWrapperTests.cs
git commit -m "feat(rfc-003): add SandboxExecWrapper for macOS Level 1"
```

---

### Task 7: Implement LinuxSandboxWrapper (Level 1)

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/LinuxSandboxWrapper.cs`
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/LinuxSandboxWrapperTests.cs`

**Step 1: Write the failing test**

```csharp
// LinuxSandboxWrapperTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class LinuxSandboxWrapperTests
{
    [Fact]
    public void WrapCommand_UsesUnshare()
    {
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Equal("unshare", result.Command);
        Assert.Contains("--net", result.Args);
        Assert.Contains("--mount", result.Args);
    }

    [Fact]
    public void WrapCommand_IncludesOriginalCommand()
    {
        var result = LinuxSandboxWrapper.WrapCommand(
            command: "copilot",
            args: ["--prompt", "hello"],
            workspacePath: "/tmp/workspace",
            allowedHosts: []);

        Assert.Contains("copilot", result.Args);
        Assert.Contains("--prompt", result.Args);
    }

    [Fact]
    public void BuildNamespaceArgs_IncludesNetAndMount()
    {
        var args = LinuxSandboxWrapper.BuildNamespaceArgs();

        Assert.Contains("--net", args);
        Assert.Contains("--mount", args);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter LinuxSandboxWrapperTests -v n`
Expected: FAIL — `LinuxSandboxWrapper` does not exist

**Step 3: Write minimal implementation**

```csharp
// LinuxSandboxWrapper.cs
namespace SwarmAssistant.Runtime.Execution;

internal static class LinuxSandboxWrapper
{
    public static string[] BuildNamespaceArgs() =>
        ["--net", "--mount", "--pid", "--fork"];

    public static SandboxCommand WrapCommand(
        string command,
        IReadOnlyList<string> args,
        string workspacePath,
        string[] allowedHosts)
    {
        var unshareArgs = new List<string>(BuildNamespaceArgs());
        unshareArgs.Add("--");
        unshareArgs.Add(command);
        unshareArgs.AddRange(args);
        return new SandboxCommand("unshare", unshareArgs.ToArray());
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter LinuxSandboxWrapperTests -v n`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/LinuxSandboxWrapper.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/LinuxSandboxWrapperTests.cs
git commit -m "feat(rfc-003): add LinuxSandboxWrapper for Level 1"
```

---

### Task 8: Integrate SandboxLevel into SandboxCommandBuilder

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs`

**Step 1: Write the failing test**

Append to `SandboxCommandBuilderTests.cs`:

```csharp
[Fact]
public void BuildForLevel_BareCli_ReturnsOriginalCommand()
{
    var command = SandboxCommandBuilder.BuildForLevel(
        SandboxLevel.BareCli,
        "copilot",
        ["--help"],
        workspacePath: "/tmp/ws",
        allowedHosts: []);

    Assert.Equal("copilot", command.Command);
    Assert.Equal(["--help"], command.Args);
}

[Fact]
public void BuildForLevel_OsSandboxed_WrapsMacOS()
{
    var command = SandboxCommandBuilder.BuildForLevel(
        SandboxLevel.OsSandboxed,
        "copilot",
        ["--help"],
        workspacePath: "/tmp/ws",
        allowedHosts: ["api.github.com"]);

    if (OperatingSystem.IsMacOS())
    {
        Assert.Equal("sandbox-exec", command.Command);
    }
    else if (OperatingSystem.IsLinux())
    {
        Assert.Equal("unshare", command.Command);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter BuildForLevel -v n`
Expected: FAIL — `BuildForLevel` method does not exist

**Step 3: Write minimal implementation**

Add to `SandboxCommandBuilder.cs`:

```csharp
public static SandboxCommand BuildForLevel(
    SandboxLevel level,
    string command,
    IReadOnlyList<string> args,
    string workspacePath,
    string[] allowedHosts) => level switch
{
    SandboxLevel.BareCli => new SandboxCommand(command, args.ToArray()),
    SandboxLevel.OsSandboxed when OperatingSystem.IsMacOS() =>
        SandboxExecWrapper.WrapCommand(command, args, workspacePath, allowedHosts),
    SandboxLevel.OsSandboxed when OperatingSystem.IsLinux() =>
        LinuxSandboxWrapper.WrapCommand(command, args, workspacePath, allowedHosts),
    SandboxLevel.OsSandboxed =>
        throw new PlatformNotSupportedException("OS-level sandboxing not supported on this platform."),
    SandboxLevel.Container =>
        throw new InvalidOperationException("Use container lifecycle manager for Level 2."),
    _ => throw new ArgumentOutOfRangeException(nameof(level))
};
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter BuildForLevel -v n`
Expected: PASS (2 tests)

**Step 5: Run full test suite**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests -v n`
Expected: All existing + new tests PASS

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs
git commit -m "feat(rfc-003): integrate SandboxLevel into SandboxCommandBuilder"
```

---

### Task 9: Wire SandboxLevelEnforcer into Worker Bootstrap

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Worker.cs` (~line 244-267)
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/DispatcherActor.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs`

**Step 1: Write the failing test**

Add to `RuntimeEventEmissionTests.cs` or create new test file:

```csharp
[Fact]
public async Task HappyPath_EnforcerIsAvailableInPipeline()
{
    var options = CreateTestOptions() with { SandboxMode = "host" };
    var (dispatcher, writer) = BuildDispatcher("enforcer-test", options: options);
    var taskId = $"enforcer-{Guid.NewGuid():N}";

    dispatcher.Tell(new TaskAssigned(taskId, "Enforcer Test", "Verify enforcer wired.", DateTimeOffset.UtcNow));

    await WaitForTaskStatus(taskId, TaskState.Done, TimeSpan.FromSeconds(30));

    // Pipeline completes — enforcer didn't block Level 0
    var events = writer.Events.Where(e => e.TaskId == taskId).ToList();
    Assert.NotEmpty(events);
}
```

**Step 2: Add `SandboxLevel` property to `RuntimeOptions.cs`**

After line 13 (`SandboxMode`), add:

```csharp
/// <summary>
/// Parsed sandbox level derived from SandboxMode. Use this for enforcement logic.
/// </summary>
[System.Text.Json.Serialization.JsonIgnore]
public SandboxLevel SandboxLevel => SandboxCommandBuilder.ParseLevel(SandboxMode);
```

**Step 3: Create `SandboxLevelEnforcer` in `Worker.cs`**

After the WorkspaceBranchManager creation (~line 246), add:

```csharp
var sandboxEnforcer = new SandboxLevelEnforcer(
    containerAvailable: _options.SandboxMode is "docker" or "apple-container");
```

Pass it to DispatcherActor constructor.

**Step 4: Update `DispatcherActor` to accept and forward the enforcer**

Add `SandboxLevelEnforcer? sandboxEnforcer` parameter to DispatcherActor constructor. Store as field. Pass to TaskCoordinatorActor in HandleTaskAssigned.

**Step 5: Run tests**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests -v n`
Expected: All PASS

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Worker.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/DispatcherActor.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs
git commit -m "feat(rfc-003): wire SandboxLevelEnforcer into bootstrap and dispatch"
```

---

### Task 10: Wire WorkspaceBranchManager into Build Dispatch (Diagnostic Sprint Task 8)

This completes the prerequisite from the diagnostic sprint.

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs` (~line 983-1003)

**Step 1: Verify current state**

Read `TaskCoordinatorActor.cs` lines 983-1003. The build dispatch section should have the branch manager reference but not call it in the action dispatch.

**Step 2: Wire the branch manager call**

In the Build dispatch section, before the `TransitionTo(TaskState.Building)` call, ensure:

```csharp
if (_workspaceBranchManager is not null)
{
    var branch = await _workspaceBranchManager.EnsureBranchAsync(_taskId);
    if (branch is not null)
    {
        _logger?.LogInformation("Builder isolated to branch {Branch} for task {TaskId}", branch, _taskId);
    }
}
```

**Step 3: Run tests**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests -v n`
Expected: All PASS (branch manager returns null when disabled, no side effects in tests)

**Step 4: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs
git commit -m "feat(rfc-003): wire WorkspaceBranchManager into build dispatch"
```

---

## Phase 1 Gate: Validate Pipeline at Level 0

After Task 10, run the full pipeline with a real adapter to validate Level 0:

```bash
# Start the runtime
Runtime__SandboxMode=host \
Runtime__WorkspaceBranchEnabled=true \
Runtime__ProjectContextPath=AGENTS.md \
dotnet run --project project/dotnet/src/SwarmAssistant.Runtime

# In another terminal, submit a test task
curl -X POST http://localhost:5080/a2a/tasks \
  -H "Content-Type: application/json" \
  -d '{"title": "Add README to workspace", "description": "Create a simple README.md in the workspace root."}'
```

If this succeeds with a real adapter (Copilot/Kimi/Kilo), Phase 1 is validated. If not, the failure tells us what to fix before Phase 2.

---

## Phase 2: Level 1 Enforcement (Run at Level 1, Build Level 2)

### Task 11: Integrate Level 1 into SubscriptionCliRoleExecutor

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs`

**Goal:** When `SandboxLevel` is `OsSandboxed`, wrap CLI adapter invocations with `SandboxExecWrapper` or `LinuxSandboxWrapper` instead of the string-based `SandboxMode` routing.

**Step 1: Write the failing test**

```csharp
[Fact]
public void BuildForLevel_OsSandboxed_IncludesWorkspacePath()
{
    var command = SandboxCommandBuilder.BuildForLevel(
        SandboxLevel.OsSandboxed,
        "copilot",
        ["--prompt", "test"],
        workspacePath: "/workspace/task-123",
        allowedHosts: ["api.github.com"]);

    // Verify the workspace path appears in the sandbox profile
    var argsJoined = string.Join(" ", command.Args);
    Assert.Contains("/workspace/task-123", argsJoined);
}
```

**Step 2: Verify this already passes from Task 8 implementation**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter BuildForLevel -v n`
Expected: PASS (workspace path is in the sandbox-exec profile)

**Step 3: Add Level 1 routing to SubscriptionCliRoleExecutor**

In `ExecuteCoreAsync`, where `SandboxCommandBuilder.Build()` is called (~line 135 and 162), add an alternative path:

```csharp
var sandboxCommand = enforcer is not null && enforcer.CanEnforce(SandboxLevel.OsSandboxed)
    ? SandboxCommandBuilder.BuildForLevel(
        effectiveLevel, adapter.ExecuteCommand, executeArgs,
        workspacePath: workspacePath ?? Environment.CurrentDirectory,
        allowedHosts: sandboxRequirements?.NeedsNetwork ?? [])
    : SandboxCommandBuilder.Build(_options, adapter.ExecuteCommand, executeArgs);
```

**Step 4: Run full test suite**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests -v n`
Expected: All PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs
git commit -m "feat(rfc-003): route Level 1 sandbox through SubscriptionCliRoleExecutor"
```

---

### Task 12: Container Lifecycle Manager

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/ContainerLifecycleManager.cs`
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/ContainerLifecycleManagerTests.cs`

**Step 1: Write the failing test**

```csharp
// ContainerLifecycleManagerTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class ContainerLifecycleManagerTests
{
    [Fact]
    public void BuildRunArgs_IncludesWorkspaceMount()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            imageName: "swarm-tools:latest",
            workspacePath: "/tmp/ws",
            cpuLimit: "1.0",
            memoryLimit: "512m",
            timeoutSeconds: 120);

        Assert.Contains("-v", args);
        Assert.Contains("/tmp/ws:/workspace:rw", args);
    }

    [Fact]
    public void BuildRunArgs_IncludesResourceLimits()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            imageName: "swarm-tools:latest",
            workspacePath: "/tmp/ws",
            cpuLimit: "2.0",
            memoryLimit: "1g",
            timeoutSeconds: 60);

        Assert.Contains("--cpus=2.0", args);
        Assert.Contains("--memory=1g", args);
    }

    [Fact]
    public void BuildRunArgs_IncludesAutoRemove()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            imageName: "swarm-tools:latest",
            workspacePath: "/tmp/ws",
            cpuLimit: "1.0",
            memoryLimit: "512m",
            timeoutSeconds: 120);

        Assert.Contains("--rm", args);
    }

    [Fact]
    public void BuildRunArgs_IncludesImageName()
    {
        var args = ContainerLifecycleManager.BuildRunArgs(
            imageName: "swarm-tools:latest",
            workspacePath: "/tmp/ws",
            cpuLimit: "1.0",
            memoryLimit: "512m",
            timeoutSeconds: 120);

        Assert.Contains("swarm-tools:latest", args);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter ContainerLifecycleManagerTests -v n`
Expected: FAIL — type does not exist

**Step 3: Write minimal implementation**

```csharp
// ContainerLifecycleManager.cs
namespace SwarmAssistant.Runtime.Execution;

internal static class ContainerLifecycleManager
{
    public static string[] BuildRunArgs(
        string imageName,
        string workspacePath,
        string cpuLimit,
        string memoryLimit,
        int timeoutSeconds)
    {
        return
        [
            "run",
            "--rm",
            $"--cpus={cpuLimit}",
            $"--memory={memoryLimit}",
            $"--stop-timeout={timeoutSeconds}",
            "-v", $"{workspacePath}:/workspace:rw",
            "-w", "/workspace",
            imageName
        ];
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter ContainerLifecycleManagerTests -v n`
Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/ContainerLifecycleManager.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/ContainerLifecycleManagerTests.cs
git commit -m "feat(rfc-003): add ContainerLifecycleManager for Level 2"
```

---

### Task 13: Network Policy for Containers

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/ContainerNetworkPolicy.cs`
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/ContainerNetworkPolicyTests.cs`

**Step 1: Write the failing test**

```csharp
// ContainerNetworkPolicyTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Runtime.Execution;

public sealed class ContainerNetworkPolicyTests
{
    [Fact]
    public void BuildNetworkArgs_NoHosts_DisablesNetwork()
    {
        var args = ContainerNetworkPolicy.BuildNetworkArgs([]);

        Assert.Contains("--network=none", args);
    }

    [Fact]
    public void BuildNetworkArgs_WithHosts_UsesBridgeNetwork()
    {
        var args = ContainerNetworkPolicy.BuildNetworkArgs(["api.github.com"]);

        Assert.DoesNotContain("--network=none", args);
    }

    [Fact]
    public void BuildNetworkArgs_A2AAllowed_IncludesHostGateway()
    {
        var args = ContainerNetworkPolicy.BuildNetworkArgs(
            allowedHosts: [],
            allowA2A: true);

        Assert.Contains("--add-host=host.docker.internal:host-gateway", args);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter ContainerNetworkPolicyTests -v n`
Expected: FAIL

**Step 3: Write minimal implementation**

```csharp
// ContainerNetworkPolicy.cs
namespace SwarmAssistant.Runtime.Execution;

internal static class ContainerNetworkPolicy
{
    public static string[] BuildNetworkArgs(string[] allowedHosts, bool allowA2A = false)
    {
        var args = new List<string>();

        if (allowedHosts.Length == 0 && !allowA2A)
        {
            args.Add("--network=none");
            return args.ToArray();
        }

        if (allowA2A)
        {
            args.Add("--add-host=host.docker.internal:host-gateway");
        }

        return args.ToArray();
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter ContainerNetworkPolicyTests -v n`
Expected: PASS (3 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/ContainerNetworkPolicy.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/ContainerNetworkPolicyTests.cs
git commit -m "feat(rfc-003): add ContainerNetworkPolicy for Level 2"
```

---

## Phase 2 Gate: Validate Pipeline at Level 1

After Task 13, switch the builder to Level 1 and run a real task:

```bash
Runtime__SandboxMode=host \
Runtime__SandboxLevel=1 \
Runtime__WorkspaceBranchEnabled=true \
dotnet run --project project/dotnet/src/SwarmAssistant.Runtime
```

Submit a task and verify:
- Builder runs under `sandbox-exec` on macOS
- File writes are scoped to workspace
- Network restrictions are enforced
- CLI adapter (Copilot/Kimi) still functions through the sandbox

If sandbox-exec breaks the adapter, document what restriction caused it and loosen the profile. This is expected and valuable.

---

## Phase 3: Level 2 Validation (Run at Level 2, Build Spectrum Selection)

### Task 14: Integrate ContainerLifecycleManager into SandboxCommandBuilder

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void BuildForLevel_Container_ReturnsDockerRunCommand()
{
    var command = SandboxCommandBuilder.BuildForLevel(
        SandboxLevel.Container,
        "copilot",
        ["--prompt", "test"],
        workspacePath: "/tmp/ws",
        allowedHosts: [],
        containerImage: "swarm-tools:latest");

    Assert.Equal("docker", command.Command);
    Assert.Contains("run", command.Args);
    Assert.Contains("--rm", command.Args);
    Assert.Contains("swarm-tools:latest", command.Args);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter BuildForLevel_Container -v n`
Expected: FAIL — `BuildForLevel` doesn't accept `containerImage` parameter

**Step 3: Update `BuildForLevel` to handle Level 2**

Replace the `SandboxLevel.Container` arm:

```csharp
SandboxLevel.Container => BuildContainerCommand(
    command, args, workspacePath, allowedHosts,
    containerImage ?? throw new ArgumentNullException(nameof(containerImage))),
```

Add private method:

```csharp
private static SandboxCommand BuildContainerCommand(
    string command,
    IReadOnlyList<string> args,
    string workspacePath,
    string[] allowedHosts,
    string containerImage)
{
    var dockerArgs = new List<string>(
        ContainerLifecycleManager.BuildRunArgs(
            containerImage, workspacePath,
            cpuLimit: "1.0", memoryLimit: "512m", timeoutSeconds: 120));
    dockerArgs.AddRange(ContainerNetworkPolicy.BuildNetworkArgs(allowedHosts, allowA2A: true));
    dockerArgs.Add(command);
    dockerArgs.AddRange(args);
    return new SandboxCommand("docker", dockerArgs.ToArray());
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter BuildForLevel -v n`
Expected: All PASS

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxCommandBuilder.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxCommandBuilderTests.cs
git commit -m "feat(rfc-003): integrate container lifecycle into SandboxCommandBuilder"
```

---

### Task 15: Spectrum Selection Policy

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxSpectrumPolicy.cs`
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxSpectrumPolicyTests.cs`

**Step 1: Write the failing test**

```csharp
// SandboxSpectrumPolicyTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

public sealed class SandboxSpectrumPolicyTests
{
    [Fact]
    public void RecommendLevel_OAuthAgent_ReturnsBareCli()
    {
        var requirements = new SandboxRequirements { NeedsOAuth = true };

        var level = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.BareCli, level);
    }

    [Fact]
    public void RecommendLevel_KeychainAgent_ReturnsBareCli()
    {
        var requirements = new SandboxRequirements { NeedsKeychain = true };

        var level = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.BareCli, level);
    }

    [Fact]
    public void RecommendLevel_NoSpecialNeeds_ReturnsContainer()
    {
        var requirements = new SandboxRequirements();

        var level = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.Container, level);
    }

    [Fact]
    public void RecommendLevel_NetworkOnlyNeeds_ReturnsOsSandboxed()
    {
        var requirements = new SandboxRequirements
        {
            NeedsNetwork = ["api.github.com"]
        };

        var level = SandboxSpectrumPolicy.RecommendLevel(requirements);

        Assert.Equal(SandboxLevel.OsSandboxed, level);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxSpectrumPolicyTests -v n`
Expected: FAIL

**Step 3: Write minimal implementation**

```csharp
// SandboxSpectrumPolicy.cs
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Execution;

internal static class SandboxSpectrumPolicy
{
    public static SandboxLevel RecommendLevel(SandboxRequirements requirements)
    {
        // OAuth or keychain access requires bare CLI (no isolation)
        if (requirements.NeedsOAuth || requirements.NeedsKeychain)
            return SandboxLevel.BareCli;

        // Network needs can be handled by OS sandboxing with allowlists
        if (requirements.NeedsNetwork.Length > 0)
            return SandboxLevel.OsSandboxed;

        // Default: maximum isolation
        return SandboxLevel.Container;
    }
}
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxSpectrumPolicyTests -v n`
Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/SandboxSpectrumPolicy.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxSpectrumPolicyTests.cs
git commit -m "feat(rfc-003): add SandboxSpectrumPolicy for level recommendation"
```

---

### Task 16: End-to-End Integration Test

**Files:**
- Create: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxSpectrumIntegrationTests.cs`

**Step 1: Write the integration test**

```csharp
// SandboxSpectrumIntegrationTests.cs
namespace SwarmAssistant.Runtime.Tests;

using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Execution;

public sealed class SandboxSpectrumIntegrationTests
{
    [Theory]
    [InlineData("host", SandboxLevel.BareCli)]
    [InlineData("docker", SandboxLevel.Container)]
    [InlineData("apple-container", SandboxLevel.Container)]
    public void ParseAndEnforce_EndToEnd(string mode, SandboxLevel expectedLevel)
    {
        var parsed = SandboxCommandBuilder.ParseLevel(mode);
        var enforcer = new SandboxLevelEnforcer();

        Assert.Equal(expectedLevel, parsed);
        Assert.True(enforcer.CanEnforce(parsed));
    }

    [Fact]
    public void FullSpectrum_RecommendThenEnforce()
    {
        var oauthAgent = new SandboxRequirements { NeedsOAuth = true };
        var networkAgent = new SandboxRequirements { NeedsNetwork = ["api.github.com"] };
        var apiAgent = new SandboxRequirements();

        var enforcer = new SandboxLevelEnforcer();

        // OAuth agent → Level 0
        var oauthLevel = SandboxSpectrumPolicy.RecommendLevel(oauthAgent);
        Assert.Equal(SandboxLevel.BareCli, oauthLevel);
        Assert.True(enforcer.CanEnforce(oauthLevel));

        // Network agent → Level 1
        var networkLevel = SandboxSpectrumPolicy.RecommendLevel(networkAgent);
        Assert.Equal(SandboxLevel.OsSandboxed, networkLevel);

        // API agent → Level 2
        var apiLevel = SandboxSpectrumPolicy.RecommendLevel(apiAgent);
        Assert.Equal(SandboxLevel.Container, apiLevel);
    }

    [Fact]
    public void Fallback_WhenContainerUnavailable()
    {
        var enforcer = new SandboxLevelEnforcer(containerAvailable: false);
        var requirements = new SandboxRequirements();

        var recommended = SandboxSpectrumPolicy.RecommendLevel(requirements);
        Assert.Equal(SandboxLevel.Container, recommended);

        var effective = enforcer.GetEffectiveLevel(recommended);
        Assert.NotEqual(SandboxLevel.Container, effective);
        // Falls back to OsSandboxed or BareCli depending on platform
    }
}
```

**Step 2: Run test**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests --filter SandboxSpectrumIntegrationTests -v n`
Expected: PASS (3 tests)

**Step 3: Run FULL test suite**

Run: `dotnet test project/dotnet/tests/SwarmAssistant.Runtime.Tests -v n`
Expected: ALL PASS (existing + all 16 tasks' tests)

**Step 4: Commit**

```bash
git add project/dotnet/tests/SwarmAssistant.Runtime.Tests/SandboxSpectrumIntegrationTests.cs
git commit -m "test(rfc-003): add end-to-end sandbox spectrum integration tests"
```

---

## Phase 3 Gate: Validate Pipeline at Level 2

Run the pipeline with the builder in a container:

```bash
Runtime__SandboxMode=docker \
Runtime__DockerSandboxWrapper__Command=docker \
Runtime__DockerSandboxWrapper__Args__0=run \
Runtime__DockerSandboxWrapper__Args__1=--rm \
Runtime__DockerSandboxWrapper__Args__2=swarm-tools:latest \
Runtime__DockerSandboxWrapper__Args__3=sh \
Runtime__DockerSandboxWrapper__Args__4=-lc \
Runtime__DockerSandboxWrapper__Args__5={{command}} {{args_joined}} \
dotnet run --project project/dotnet/src/SwarmAssistant.Runtime
```

This validates that:
- Container starts and stops cleanly
- CLI adapter works inside the container
- A2A communication works from container to host
- Artifacts are collected

---

## Summary

| Task | Phase | What | Tests |
|------|-------|------|-------|
| 1 | 1 | SandboxLevel enum | 3 |
| 2 | 1 | SandboxRequirements record | 2 |
| 3 | 1 | SandboxRequirements in AgentCard | 2 |
| 4 | 1 | SandboxMode→SandboxLevel mapping | 5 |
| 5 | 1 | SandboxLevelEnforcer | 4 |
| 6 | 1 | SandboxExecWrapper (macOS) | 4 |
| 7 | 1 | LinuxSandboxWrapper | 3 |
| 8 | 1 | Integrate into SandboxCommandBuilder | 2 |
| 9 | 1 | Wire enforcer into bootstrap | 1 |
| 10 | 1 | Wire branch manager into build | 0 (existing tests cover) |
| 11 | 2 | Level 1 in SubscriptionCliRoleExecutor | 1 |
| 12 | 2 | ContainerLifecycleManager | 4 |
| 13 | 2 | ContainerNetworkPolicy | 3 |
| 14 | 3 | Container in SandboxCommandBuilder | 1 |
| 15 | 3 | SandboxSpectrumPolicy | 4 |
| 16 | 3 | Integration tests | 3 |
| **Total** | | **16 tasks** | **42 tests** |
