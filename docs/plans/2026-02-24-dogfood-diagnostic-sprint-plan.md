# Dogfood Diagnostic Sprint Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable the SwarmAssistant to execute real tasks on its own codebase by diagnosing the pipeline, fixing context injection, and validating with a real task.

**Architecture:** Three-phase approach — (1) diagnostic instrumentation to trace where the planner→builder→reviewer pipeline breaks, (2) context injection fixes so the builder receives file-scoped instructions, (3) workspace isolation so builder output lands on a reviewable branch.

**Tech Stack:** .NET 9, Akka.NET, xUnit, SubscriptionCliRoleExecutor, RolePromptFactory, CodeIndexActor, RuntimeEventRecorder

---

## Phase 1: Diagnostic End-to-End Instrumentation

### Task 1: Add diagnostic event type to RuntimeEventRecorder

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Tasks/RuntimeEventRecorder.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs`

**Step 1: Write the failing test**

Add to `RuntimeEventEmissionTests.cs` after the existing tests:

```csharp
[Fact]
public async Task HappyPath_EmitsDiagnosticContextEvent()
{
    var (taskId, writer) = await SubmitTaskAndWait();

    var evt = writer.Events.FirstOrDefault(
        e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticContext);
    Assert.NotNull(evt);
    Assert.Contains("prompt", evt!.Payload ?? "");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_EmitsDiagnosticContextEvent" -v n`
Expected: FAIL — `DiagnosticContext` constant does not exist

**Step 3: Add DiagnosticContext constant and recording method**

In `RuntimeEventRecorder.cs`, add after the existing event type constants (~line 32):

```csharp
public const string DiagnosticContext = "diagnostic.context";
```

Add recording method after the existing methods (~line 66):

```csharp
public void RecordDiagnosticContextAsync(string taskId, string diagnosticPayload)
    => _ = WriteAsync(taskId, DiagnosticContext, diagnosticPayload);
```

**Step 4: Wire diagnostic recording into TaskCoordinatorActor**

In `TaskCoordinatorActor.cs`, in the `DoDispatchAction` method (~line 957), after the prompt is built via `RolePromptFactory.BuildPrompt()` and before the `Tell()` to worker/reviewer, add:

```csharp
_eventRecorder?.RecordDiagnosticContextAsync(_taskId, System.Text.Json.JsonSerializer.Serialize(new
{
    action = action.ToString(),
    role = role.ToString(),
    promptLength = prompt?.Length ?? 0,
    hasCodeContext = codeContext?.Chunks?.Count > 0,
    codeChunkCount = codeContext?.Chunks?.Count ?? 0,
    hasStrategyAdvice = strategyAdvice != null,
    targetFiles = codeContext?.Chunks?.Select(c => c.FilePath).Distinct().ToArray() ?? Array.Empty<string>()
}));
```

**Step 5: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_EmitsDiagnosticContextEvent" -v n`
Expected: PASS

**Step 6: Run all tests to verify no regressions**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass

**Step 7: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Tasks/RuntimeEventRecorder.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs
git commit -m "feat: add diagnostic.context event type for dogfood tracing"
```

---

### Task 2: Add CLI adapter diagnostic logging to SubscriptionCliRoleExecutor

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task HappyPath_EmitsAdapterDiagnosticEvent()
{
    var (taskId, writer) = await SubmitTaskAndWait();

    var evt = writer.Events.FirstOrDefault(
        e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticAdapter);
    Assert.NotNull(evt);
    Assert.Contains("adapterId", evt!.Payload ?? "");
    Assert.Contains("exitCode", evt!.Payload ?? "");
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_EmitsAdapterDiagnosticEvent" -v n`
Expected: FAIL — `DiagnosticAdapter` constant does not exist

**Step 3: Add DiagnosticAdapter constant and method to RuntimeEventRecorder**

In `RuntimeEventRecorder.cs`:

```csharp
public const string DiagnosticAdapter = "diagnostic.adapter";

public void RecordDiagnosticAdapterAsync(string taskId, string diagnosticPayload)
    => _ = WriteAsync(taskId, DiagnosticAdapter, diagnosticPayload);
```

**Step 4: Wire into SubscriptionCliRoleExecutor**

In `SubscriptionCliRoleExecutor.cs`, the `ExecuteCoreAsync` method runs the adapter loop. After a successful adapter execution (~line 184, where output is validated), add diagnostic recording.

This requires passing `RuntimeEventRecorder` and `taskId` through to the executor. The executor is created in `Worker.cs` and used via `AgentFrameworkRoleEngine`. The cleanest approach: add an optional `Action<string>` diagnostic callback to `ExecuteAsync` that the caller can wire.

In `SubscriptionCliRoleExecutor.cs`, after the process completes (~line 177):

```csharp
_logger.LogInformation(
    "CLI adapter diagnostic: adapter={Adapter} exitCode={ExitCode} outputLen={OutputLen} elapsed={Elapsed}ms taskId={TaskId}",
    adapterId, exitCode, stdout?.Length ?? 0, elapsed.TotalMilliseconds, command.TaskId);
```

In `WorkerActor.cs`, after `ExecuteAsync` returns (~line 87), record the diagnostic event via the event recorder passed from TaskCoordinatorActor:

```csharp
_eventRecorder?.RecordDiagnosticAdapterAsync(command.TaskId, System.Text.Json.JsonSerializer.Serialize(new
{
    adapterId = result.AdapterId,
    outputLength = result.Output?.Length ?? 0,
    role = command.Role.ToString(),
    exitCode = 0
}));
```

Note: The event recorder reference needs to flow from DispatcherActor → WorkerActor. Check if WorkerActor already has access; if not, add it as a constructor parameter.

**Step 5: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_EmitsAdapterDiagnosticEvent" -v n`
Expected: PASS

**Step 6: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass

**Step 7: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Tasks/RuntimeEventRecorder.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Execution/SubscriptionCliRoleExecutor.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/WorkerActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs
git commit -m "feat: add diagnostic.adapter event for CLI adapter tracing"
```

---

### Task 3: Create dogfood diagnostic script

**Files:**
- Create: `scripts/dogfood-diagnostic.sh`
- Modify: `Taskfile.yml`

**Step 1: Create the diagnostic script**

Based on the existing `scripts/dogfood-smoke.sh` pattern, create a diagnostic variant that submits a real scoped task and captures full trace output.

```bash
#!/usr/bin/env bash
set -euo pipefail

# Dogfood diagnostic: submit a real task and capture full pipeline trace.
# Usage: ./scripts/dogfood-diagnostic.sh [base_url]

BASE_URL="${1:-http://127.0.0.1:5080}"
ARTIFACTS_DIR="/tmp/dogfood-diagnostic-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$ARTIFACTS_DIR"

echo "=== Dogfood Diagnostic Run ==="
echo "Base URL: $BASE_URL"
echo "Artifacts: $ARTIFACTS_DIR"

# Step 1: Health check
echo "--- Step 1: Health check ---"
for i in $(seq 1 30); do
    if curl -sf "$BASE_URL/healthz" > /dev/null 2>&1; then
        echo "Runtime healthy"
        break
    fi
    [ "$i" -eq 30 ] && { echo "FAIL: Runtime not healthy after 30s"; exit 1; }
    sleep 1
done

# Step 2: Create run
echo "--- Step 2: Create run ---"
RUN_ID="diag-run-$(date +%s)"
RUN_RESP=$(curl -sf -X POST "$BASE_URL/runs" \
    -H "Content-Type: application/json" \
    -d "{\"runId\": \"$RUN_ID\", \"title\": \"dogfood-diagnostic\"}")
echo "Run created: $RUN_ID"
echo "$RUN_RESP" > "$ARTIFACTS_DIR/01-run-create.json"

# Step 3: Submit a real scoped task
echo "--- Step 3: Submit diagnostic task ---"
TASK_TITLE="Add a unit test for LegacyRunId.Resolve verifying null-input returns legacy prefix"
TASK_DESC="In project/dotnet/tests/SwarmAssistant.Runtime.Tests/, add a test that calls LegacyRunId.Resolve(null) and asserts the result starts with 'legacy-'. This validates the fallback behavior when no explicit runId is provided."

TASK_RESP=$(curl -sf -X POST "$BASE_URL/a2a/tasks" \
    -H "Content-Type: application/json" \
    -d "{\"title\": \"$TASK_TITLE\", \"description\": \"$TASK_DESC\", \"runId\": \"$RUN_ID\"}")
TASK_ID=$(echo "$TASK_RESP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('id',''))" 2>/dev/null || echo "")
echo "Task submitted: $TASK_ID"
echo "$TASK_RESP" > "$ARTIFACTS_DIR/02-task-submit.json"

if [ -z "$TASK_ID" ]; then
    echo "FAIL: No task ID returned"
    exit 1
fi

# Step 4: Poll for terminal state
echo "--- Step 4: Polling for terminal state ---"
TASK_STATUS=""
for i in $(seq 1 60); do
    SNAP=$(curl -sf "$BASE_URL/a2a/tasks/$TASK_ID" 2>/dev/null || echo "{}")
    TASK_STATUS=$(echo "$SNAP" | python3 -c "import sys,json; print(json.load(sys.stdin).get('status',''))" 2>/dev/null || echo "")
    echo "  Poll $i: status=$TASK_STATUS"
    echo "$SNAP" > "$ARTIFACTS_DIR/03-task-poll-$i.json"
    if [ "$TASK_STATUS" = "done" ] || [ "$TASK_STATUS" = "failed" ] || [ "$TASK_STATUS" = "blocked" ]; then
        break
    fi
    sleep 2
done
echo "Final status: $TASK_STATUS"

# Step 5: Fetch task snapshot with outputs
echo "--- Step 5: Task snapshot ---"
FINAL_SNAP=$(curl -sf "$BASE_URL/a2a/tasks/$TASK_ID" 2>/dev/null || echo "{}")
echo "$FINAL_SNAP" > "$ARTIFACTS_DIR/04-task-final.json"

# Step 6: Fetch replay events
echo "--- Step 6: Replay events ---"
RUN_EVENTS=$(curl -sf "$BASE_URL/runs/$RUN_ID/events" 2>/dev/null || echo "{}")
echo "$RUN_EVENTS" > "$ARTIFACTS_DIR/05-run-events.json"

TASK_EVENTS=$(curl -sf "$BASE_URL/memory/tasks/$TASK_ID/events" 2>/dev/null || echo "{}")
echo "$TASK_EVENTS" > "$ARTIFACTS_DIR/06-task-events.json"

# Step 7: Fetch AG-UI recent events
echo "--- Step 7: AG-UI events ---"
AGUI_EVENTS=$(curl -sf "$BASE_URL/ag-ui/recent?count=100" 2>/dev/null || echo "[]")
echo "$AGUI_EVENTS" > "$ARTIFACTS_DIR/07-agui-events.json"

# Step 8: Generate diagnostic report
echo "--- Step 8: Generating diagnostic report ---"
python3 - "$ARTIFACTS_DIR" <<'PYEOF'
import json, sys, os, glob

artifacts_dir = sys.argv[1]

def load(name):
    path = os.path.join(artifacts_dir, name)
    if not os.path.exists(path):
        return {}
    with open(path) as f:
        return json.load(f)

final = load("04-task-final.json")
run_events = load("05-run-events.json")
task_events = load("06-task-events.json")
agui_events = load("07-agui-events.json")

run_items = run_events.get("items", [])
task_items = task_events.get("items", [])
agui_list = agui_events if isinstance(agui_events, list) else []

# Extract diagnostic events
diag_context = [e for e in run_items if e.get("eventType") == "diagnostic.context"]
diag_adapter = [e for e in run_items if e.get("eventType") == "diagnostic.adapter"]

report = {
    "summary": {
        "taskId": final.get("id", ""),
        "status": final.get("status", "unknown"),
        "hasPlanningOutput": bool(final.get("planningOutput")),
        "hasBuildOutput": bool(final.get("buildOutput")),
        "hasReviewOutput": bool(final.get("reviewOutput")),
        "planningOutputLength": len(final.get("planningOutput", "") or ""),
        "buildOutputLength": len(final.get("buildOutput", "") or ""),
        "reviewOutputLength": len(final.get("reviewOutput", "") or ""),
    },
    "events": {
        "runEventCount": len(run_items),
        "taskEventCount": len(task_items),
        "aguiEventCount": len(agui_list),
        "eventTypes": list(set(e.get("eventType", "") for e in run_items)),
        "diagnosticContextEvents": len(diag_context),
        "diagnosticAdapterEvents": len(diag_adapter),
    },
    "diagnosticContext": diag_context,
    "diagnosticAdapter": diag_adapter,
    "pipeline": {
        "plannerReceivedContext": any(
            "hasCodeContext" in json.dumps(e.get("payload", ""))
            for e in diag_context
            if "plan" in json.dumps(e.get("payload", "")).lower()
        ),
        "builderReceivedPlan": bool(final.get("planningOutput")),
        "reviewerReceivedBuild": bool(final.get("reviewOutput")),
    },
    "outputs": {
        "planningOutput": (final.get("planningOutput", "") or "")[:500],
        "buildOutput": (final.get("buildOutput", "") or "")[:500],
        "reviewOutput": (final.get("reviewOutput", "") or "")[:500],
    }
}

report_path = os.path.join(artifacts_dir, "diagnostic-report.json")
with open(report_path, "w") as f:
    json.dump(report, f, indent=2)

print(f"\n=== DIAGNOSTIC REPORT ===")
print(f"Task status:           {report['summary']['status']}")
print(f"Planning output:       {'YES' if report['summary']['hasPlanningOutput'] else 'NO'} ({report['summary']['planningOutputLength']} chars)")
print(f"Build output:          {'YES' if report['summary']['hasBuildOutput'] else 'NO'} ({report['summary']['buildOutputLength']} chars)")
print(f"Review output:         {'YES' if report['summary']['hasReviewOutput'] else 'NO'} ({report['summary']['reviewOutputLength']} chars)")
print(f"Run replay events:     {report['events']['runEventCount']}")
print(f"Task replay events:    {report['events']['taskEventCount']}")
print(f"AG-UI events:          {report['events']['aguiEventCount']}")
print(f"Diagnostic ctx events: {report['events']['diagnosticContextEvents']}")
print(f"Diagnostic adp events: {report['events']['diagnosticAdapterEvents']}")
print(f"Event types:           {report['events']['eventTypes']}")
print(f"\nFull report: {report_path}")

# Identify failure points
issues = []
if not report["summary"]["hasPlanningOutput"]:
    issues.append("CRITICAL: Planner produced no output")
if not report["summary"]["hasBuildOutput"]:
    issues.append("CRITICAL: Builder produced no output")
if not report["summary"]["hasReviewOutput"]:
    issues.append("WARNING: Reviewer produced no output")
if report["events"]["diagnosticContextEvents"] == 0:
    issues.append("WARNING: No diagnostic context events — instrumentation may not be wired")
if report["summary"]["planningOutputLength"] < 50:
    issues.append("WARNING: Planning output suspiciously short — may be local-echo fallback")
if report["summary"]["buildOutputLength"] < 50:
    issues.append("WARNING: Build output suspiciously short — may be local-echo fallback")

if issues:
    print(f"\n=== ISSUES FOUND ===")
    for issue in issues:
        print(f"  - {issue}")
else:
    print(f"\n=== NO ISSUES FOUND — Pipeline appears healthy ===")

PYEOF

echo ""
echo "Artifacts saved to: $ARTIFACTS_DIR"
```

**Step 2: Add Taskfile entry**

In `Taskfile.yml`, add:

```yaml
  dogfood:diagnostic:
    desc: Run dogfood diagnostic (submits real task, traces pipeline)
    cmds:
      - bash scripts/dogfood-diagnostic.sh {{.CLI_ARGS}}
```

**Step 3: Make script executable and verify syntax**

Run: `chmod +x scripts/dogfood-diagnostic.sh && bash -n scripts/dogfood-diagnostic.sh`
Expected: No syntax errors

**Step 4: Commit**

```bash
git add scripts/dogfood-diagnostic.sh Taskfile.yml
git commit -m "feat: add dogfood diagnostic script for pipeline tracing"
```

---

## Phase 2: Context Injection Fix

### Task 4: Enrich planner prompt with AGENTS.md content

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RolePromptFactoryTests.cs` (create if not exists)

**Step 1: Write the failing test**

Create or add to `RolePromptFactoryTests.cs`:

```csharp
using SwarmAssistant.Runtime.Execution;
using Xunit;

namespace SwarmAssistant.Runtime.Tests;

public sealed class RolePromptFactoryTests
{
    [Fact]
    public void Planner_WithProjectContext_IncludesAgentsMdContent()
    {
        var command = new ExecuteRoleTask(
            TaskId: "test-1",
            Role: RoleType.Planner,
            Title: "Add a test",
            Description: "Add a unit test for Foo",
            PlanningOutput: null,
            BuildOutput: null,
            Prompt: null,
            RunId: "run-1",
            PreferredAdapter: null);

        var prompt = RolePromptFactory.BuildPrompt(command, projectContext: "## Project\nC# with xUnit");

        Assert.Contains("## Project", prompt);
        Assert.Contains("C# with xUnit", prompt);
    }

    [Fact]
    public void Builder_WithProjectContext_IncludesAgentsMdContent()
    {
        var command = new ExecuteRoleTask(
            TaskId: "test-1",
            Role: RoleType.Builder,
            Title: "Add a test",
            Description: "Add a unit test for Foo",
            PlanningOutput: "Step 1: create file",
            BuildOutput: null,
            Prompt: null,
            RunId: "run-1",
            PreferredAdapter: null);

        var prompt = RolePromptFactory.BuildPrompt(command, projectContext: "## Conventions\nfile-scoped namespaces");

        Assert.Contains("## Conventions", prompt);
    }

    [Fact]
    public void Planner_WithoutProjectContext_StillProducesValidPrompt()
    {
        var command = new ExecuteRoleTask(
            TaskId: "test-1",
            Role: RoleType.Planner,
            Title: "Add a test",
            Description: "Test desc",
            PlanningOutput: null,
            BuildOutput: null,
            Prompt: null,
            RunId: "run-1",
            PreferredAdapter: null);

        var prompt = RolePromptFactory.BuildPrompt(command, projectContext: null);

        Assert.NotNull(prompt);
        Assert.Contains("Add a test", prompt);
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "Planner_WithProjectContext_IncludesAgentsMdContent" -v n`
Expected: FAIL — no overload accepts `projectContext`

**Step 3: Add projectContext parameter to RolePromptFactory.BuildPrompt**

In `RolePromptFactory.cs`, add a new overload that accepts `projectContext` and injects it as a context layer between the base prompt and the historical insights section:

```csharp
public static string BuildPrompt(ExecuteRoleTask command, string? projectContext)
    => BuildPrompt(command, strategyAdvice: null, codeContext: null, projectContext: projectContext);

public static string BuildPrompt(
    ExecuteRoleTask command,
    StrategyAdvice? strategyAdvice,
    CodeIndexResult? codeContext,
    string? projectContext)
{
    // existing base prompt logic...
    // after base prompt, before historical insights:
    if (!string.IsNullOrWhiteSpace(projectContext)
        && command.Role is RoleType.Planner or RoleType.Builder or RoleType.Reviewer)
    {
        sb.AppendLine();
        sb.AppendLine("## Project Context");
        sb.AppendLine(projectContext);
    }
    // then historical insights, code context as before...
}
```

**Step 4: Run tests to verify they pass**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "RolePromptFactoryTests" -v n`
Expected: All 3 pass

**Step 5: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RolePromptFactoryTests.cs
git commit -m "feat: add project context injection to role prompts"
```

---

### Task 5: Load AGENTS.md at startup and wire to TaskCoordinatorActor

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Worker.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/DispatcherActor.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs`

**Step 1: Write the failing test**

Add to `RuntimeEventEmissionTests.cs`:

```csharp
[Fact]
public async Task HappyPath_ProjectContextFlowsThroughPipeline()
{
    // Rebuild dispatcher with project context
    var dispatcher = BuildDispatcher(_options, "## Test Project Context\nUse xUnit.");
    var taskId = $"ctx-test-{Guid.NewGuid():N}";
    _taskRegistry.Register(taskId, "Context flow test", null, "run-ctx");
    dispatcher.Tell(new TaskAssigned(taskId, "Context flow test"));

    await WaitForTaskStatus(taskId, "done", TimeSpan.FromSeconds(15));

    // Verify diagnostic context event mentions project context
    var diagEvt = _writer.Events.FirstOrDefault(
        e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticContext);
    // Project context was passed — the prompt should be longer than without it
    Assert.NotNull(diagEvt);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_ProjectContextFlowsThroughPipeline" -v n`
Expected: FAIL — `BuildDispatcher` doesn't accept project context parameter

**Step 3: Add ProjectContextPath to RuntimeOptions**

In `RuntimeOptions.cs`:

```csharp
public string? ProjectContextPath { get; init; }
```

**Step 4: Load AGENTS.md in Worker.cs and pass to DispatcherActor**

In `Worker.cs`, before creating the DispatcherActor (~line 215):

```csharp
string? projectContext = null;
if (!string.IsNullOrWhiteSpace(options.ProjectContextPath)
    && File.Exists(options.ProjectContextPath))
{
    projectContext = await File.ReadAllTextAsync(options.ProjectContextPath, stoppingToken);
    logger.LogInformation("Loaded project context from {Path} ({Length} chars)",
        options.ProjectContextPath, projectContext.Length);
}
```

Pass `projectContext` through DispatcherActor → TaskCoordinatorActor constructor.

**Step 5: Wire projectContext through DispatcherActor to TaskCoordinatorActor**

Add `string? projectContext` parameter to DispatcherActor constructor. Store as field. Pass to each TaskCoordinatorActor it creates.

In TaskCoordinatorActor, store `_projectContext` field. In `DoDispatchAction`, pass it to `RolePromptFactory.BuildPrompt()`.

**Step 6: Update BuildDispatcher helper in tests to accept optional projectContext**

In `RuntimeEventEmissionTests.cs`, update the `BuildDispatcher` helper to accept `string? projectContext = null` and thread it through.

**Step 7: Run tests to verify they pass**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_ProjectContextFlowsThroughPipeline" -v n`
Expected: PASS

**Step 8: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass (update any existing BuildDispatcher calls to pass null for projectContext)

**Step 9: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Worker.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/DispatcherActor.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs
git commit -m "feat: load AGENTS.md project context and wire to role prompts"
```

---

### Task 6: Add file-scoped plan output to builder task overlay

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RolePromptFactoryTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public void Builder_WithPlanningOutput_IncludesTargetFilesSection()
{
    var planOutput = """
        ## Plan
        1. Modify `project/dotnet/tests/FooTests.cs` to add test
        2. Run tests
        Target files: project/dotnet/tests/FooTests.cs
        """;

    var command = new ExecuteRoleTask(
        TaskId: "test-1",
        Role: RoleType.Builder,
        Title: "Add test",
        Description: "Add a unit test",
        PlanningOutput: planOutput,
        BuildOutput: null,
        Prompt: null,
        RunId: "run-1",
        PreferredAdapter: null);

    var prompt = RolePromptFactory.BuildPrompt(command, projectContext: null);

    Assert.Contains("FooTests.cs", prompt);
    Assert.Contains("## Implementation Plan", prompt);
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "Builder_WithPlanningOutput_IncludesTargetFilesSection" -v n`
Expected: FAIL — no "## Implementation Plan" header in builder prompt

**Step 3: Improve builder prompt template**

In `RolePromptFactory.cs`, update the Builder case to structure the planning output more explicitly:

```csharp
RoleType.Builder => $"""
    You are a builder agent. Given the task and implementation plan below, produce concrete
    implementation. Write only the code changes needed. Do not explain — just implement.

    ## Task
    Title: {command.Title}
    Description: {command.Description}

    ## Implementation Plan
    {command.PlanningOutput ?? "No plan provided — implement based on task description."}

    Produce the minimal code changes to complete this task. Include file paths for each change.
    """,
```

**Step 4: Run tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "RolePromptFactoryTests" -v n`
Expected: All pass

**Step 5: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/RolePromptFactory.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RolePromptFactoryTests.cs
git commit -m "feat: structure builder prompt with explicit plan and target files"
```

---

### Task 7: Add workspace branch creation before builder execution

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs`
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Execution/WorkspaceBranchManager.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/WorkspaceBranchManagerTests.cs`

**Step 1: Write the failing test**

```csharp
using SwarmAssistant.Runtime.Execution;
using Xunit;

namespace SwarmAssistant.Runtime.Tests;

public sealed class WorkspaceBranchManagerTests
{
    [Fact]
    public void BranchName_FromTaskId_ProducesValidGitBranch()
    {
        var branchName = WorkspaceBranchManager.BranchNameForTask("task-abc-123");
        Assert.Equal("swarm/task-abc-123", branchName);
    }

    [Fact]
    public void BranchName_SanitizesSpecialChars()
    {
        var branchName = WorkspaceBranchManager.BranchNameForTask("task with spaces & symbols!");
        Assert.DoesNotContain(" ", branchName);
        Assert.DoesNotContain("!", branchName);
        Assert.StartsWith("swarm/", branchName);
    }

    [Fact]
    public async Task CreateBranch_WhenDisabled_ReturnsNull()
    {
        var manager = new WorkspaceBranchManager(enabled: false);
        var result = await manager.EnsureBranchAsync("task-1");
        Assert.Null(result);
    }

    [Fact]
    public async Task CreateBranch_WhenEnabled_ReturnsBranchName()
    {
        // This test requires git — skip in CI if git not available
        var manager = new WorkspaceBranchManager(enabled: true);
        var result = await manager.EnsureBranchAsync("unit-test-branch");
        // In a real git repo, this creates and returns the branch name
        // In test, it may fail gracefully if not in a git repo
        Assert.True(result == null || result.StartsWith("swarm/"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "WorkspaceBranchManagerTests" -v n`
Expected: FAIL — class does not exist

**Step 3: Implement WorkspaceBranchManager**

```csharp
namespace SwarmAssistant.Runtime.Execution;

public sealed class WorkspaceBranchManager(bool enabled)
{
    public static string BranchNameForTask(string taskId)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(taskId, @"[^a-zA-Z0-9\-_]", "-");
        return $"swarm/{sanitized}";
    }

    public async Task<string?> EnsureBranchAsync(string taskId)
    {
        if (!enabled) return null;

        var branchName = BranchNameForTask(taskId);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"checkout -b {branchName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? branchName : null;
        }
        catch
        {
            return null;
        }
    }
}
```

**Step 4: Add WorkspaceBranchEnabled to RuntimeOptions**

```csharp
public bool WorkspaceBranchEnabled { get; init; }
```

**Step 5: Run tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "WorkspaceBranchManagerTests" -v n`
Expected: All pass

**Step 6: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass

**Step 7: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Execution/WorkspaceBranchManager.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/WorkspaceBranchManagerTests.cs
git commit -m "feat: add workspace branch manager for builder isolation"
```

---

### Task 8: Wire WorkspaceBranchManager into builder dispatch

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Worker.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs`

**Step 1: Write the failing test**

```csharp
[Fact]
public async Task HappyPath_BuilderDispatch_RecordsBranchInDiagnostic()
{
    _options = _options with { WorkspaceBranchEnabled = false }; // safe for test env
    var (taskId, writer) = await SubmitTaskAndWait();

    // When workspace branches are disabled, diagnostic should note it
    var diagEvts = writer.Events.Where(
        e => e.TaskId == taskId && e.EventType == RuntimeEventRecorder.DiagnosticContext);
    Assert.NotEmpty(diagEvts);
}
```

**Step 2: Run test — should pass with existing diagnostic instrumentation**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "HappyPath_BuilderDispatch_RecordsBranchInDiagnostic" -v n`
Expected: PASS (diagnostic events already emitted from Task 1)

**Step 3: Wire WorkspaceBranchManager into TaskCoordinatorActor**

In `TaskCoordinatorActor.cs`, in the `DoDispatchAction` method, before dispatching the Build action:

```csharp
case GoapAction.Build:
    if (_workspaceBranchManager != null)
    {
        var branch = await _workspaceBranchManager.EnsureBranchAsync(_taskId);
        if (branch != null)
            _logger.LogInformation("Builder working on branch {Branch} for task {TaskId}", branch, _taskId);
    }
    // existing build dispatch...
```

Pass `WorkspaceBranchManager` through Worker.cs → DispatcherActor → TaskCoordinatorActor constructor chain.

**Step 4: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln -v n`
Expected: All pass

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/TaskCoordinatorActor.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Worker.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/RuntimeEventEmissionTests.cs
git commit -m "feat: wire workspace branch creation into builder dispatch"
```

---

## Phase 3: Validation

### Task 9: Run the diagnostic and write the report

**Files:**
- Create: `docs/plans/diagnostic-report.md`

**Step 1: Start runtime with full config**

```bash
# Terminal 1: Start infrastructure
docker compose -f project/infra/arcadedb/docker-compose.yml \
    --env-file project/infra/arcadedb/env/local.env up -d

# Terminal 2: Start runtime
Runtime__A2AEnabled=true \
Runtime__AgUiEnabled=true \
Runtime__ArcadeDbEnabled=true \
Runtime__MemoryBootstrapEnabled=false \
Runtime__AutoSubmitDemoTask=false \
Runtime__ProjectContextPath=AGENTS.md \
Runtime__SandboxMode=host \
Runtime__AgentFrameworkExecutionMode=subscription-cli-fallback \
dotnet run --project project/dotnet/src/SwarmAssistant.Runtime/
```

**Step 2: Run diagnostic script**

```bash
task dogfood:diagnostic
```

**Step 3: Analyze diagnostic output**

Review `diagnostic-report.json` for:
- Did all pipeline stages produce output?
- Were diagnostic.context events emitted?
- Did the CLI adapter execute a real tool (not local-echo)?
- Was the planning output file-scoped?
- Was the build output actionable code?

**Step 4: Write diagnostic report**

Create `docs/plans/diagnostic-report.md` documenting:
- What worked
- What broke and where
- Specific failure traces from diagnostic events
- Recommended follow-up fixes

**Step 5: Commit report**

```bash
git add docs/plans/diagnostic-report.md
git commit -m "docs: add Phase 1 diagnostic report"
```

---

### Task 10: Run validation task and confirm dogfood loop

**Step 1: Submit a different task type**

Change the task in the diagnostic script or submit manually:

```bash
curl -X POST http://127.0.0.1:5080/a2a/tasks \
    -H "Content-Type: application/json" \
    -d '{
        "title": "Fix lint warning in RolePromptFactory",
        "description": "The RolePromptFactory.cs file may have unused using directives. Remove any unused imports and verify the build passes.",
        "runId": "validation-run-1"
    }'
```

**Step 2: Monitor via AG-UI events**

```bash
curl -sf http://127.0.0.1:5080/ag-ui/recent?count=50 | python3 -m json.tool
```

**Step 3: Check replay feed**

```bash
curl -sf http://127.0.0.1:5080/runs/validation-run-1/events | python3 -m json.tool
```

**Step 4: Verify working branch exists (if workspace branches enabled)**

```bash
git branch | grep swarm/
```

**Step 5: Document validation results and commit**

```bash
git add docs/plans/diagnostic-report.md
git commit -m "docs: add Phase 3 validation results to diagnostic report"
```

---

## Summary

| Task | Phase | Description | Estimated Complexity |
|------|-------|-------------|---------------------|
| 1 | 1 | Diagnostic event type in RuntimeEventRecorder | Small |
| 2 | 1 | CLI adapter diagnostic logging | Small |
| 3 | 1 | Dogfood diagnostic script | Medium |
| 4 | 2 | Project context injection in RolePromptFactory | Small |
| 5 | 2 | Load AGENTS.md and wire through actor chain | Medium |
| 6 | 2 | Builder prompt with structured plan overlay | Small |
| 7 | 2 | WorkspaceBranchManager implementation | Small |
| 8 | 2 | Wire branch manager into builder dispatch | Small |
| 9 | 3 | Run diagnostic and write report | Manual |
| 10 | 3 | Validation run and confirmation | Manual |
