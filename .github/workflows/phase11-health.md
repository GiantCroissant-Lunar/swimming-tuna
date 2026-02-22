---
name: Phase 11 Health Check
description: Non-blocking weekly hygiene check for runtime/docs drift
on:
  schedule:
    - cron: "30 9 * * 1"
  workflow_dispatch:
engine: copilot
permissions:
  contents: read
  actions: read
  pull-requests: read
tools:
  github:
    toolsets: [repos]
    read-only: true
  agentic-workflows: true
safe-outputs:
  create-issue:
    max: 1
    title-prefix: "phase11: "
    labels: [maintenance]
---

# Phase 11 Health Check

You are performing a lightweight, non-blocking repository hygiene audit.

## Read these files

- `project/README.md`
- `project/ARCHITECTURE.md`
- `project/dotnet/README.md`
- `project/godot-ui/README.md`
- `project/infra/arcadedb/scripts/smoke-e2e.sh`
- `project/dotnet/src/SwarmAssistant.Runtime/Program.cs`
- `project/dotnet/src/SwarmAssistant.Runtime/Configuration/RuntimeOptions.cs`

## Checks

1. Ensure README/ARCHITECTURE phase labels are coherent (no stale references to older "current phase" claims).
2. Ensure runtime endpoints in `Program.cs` are represented in docs:
- `/healthz`
- `/ag-ui/recent`
- `/ag-ui/events`
- `/ag-ui/actions`
- `/memory/tasks`
- `/memory/tasks/{taskId}`
- `/a2a/tasks`
- `/a2a/tasks/{taskId}`
3. Ensure runtime options added in `RuntimeOptions.cs` are reflected in docs, especially:
- `MemoryBootstrapEnabled`
- `MemoryBootstrapLimit`
- `ApiKey`
4. Ensure `project/infra/arcadedb/scripts/smoke-e2e.sh` still validates both persistence and memory read behavior.

## Output rules

- If there are no findings, do not create issues; finish with noop.
- If there are findings, create exactly one issue titled:
  - `phase11 health audit — N findings`
- Issue body format:

```markdown
## Phase 11 Health Audit — <date>

### Findings
- <item>

### Suggested Follow-up
- <single concise follow-up action per finding>
```

Keep findings factual and concise. Do not propose code changes in the workflow output.
