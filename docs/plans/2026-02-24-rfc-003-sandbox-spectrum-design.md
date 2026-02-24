# RFC-003 Sandbox Spectrum — Bootstrap Spiral Design

**Date:** 2026-02-24
**Approach:** Recursive dogfood (Option C) — the swarm builds its own sandbox levels while running through them
**Gatekeeper:** Claude Code reviews `swarm/task-*` branches before merge

## Core Idea

Three phases. Each phase runs at the level it just built, building the next level up. Failures are features — they tell us what the sandbox needs.

## Prerequisites

- Wire `WorkspaceBranchManager` into build dispatch (diagnostic sprint Task 8)
- Verify pipeline runs end-to-end with a real adapter (Copilot/Kimi/Kilo)

## Phase 1: Level 0 (Bare CLI) — Build Level 1

Pipeline runs with real adapters, no isolation. Builds OS-sandboxing infrastructure.

| Task | Builds | Validates |
|------|--------|-----------|
| Add `SandboxLevel` enum to Contracts | Enum: BareCli, OsSandboxed, Container | Pipeline produces clean C# + tests |
| Add sandbox fields to agent card | `sandboxLevel` + `sandboxRequirements` in JSON | Pipeline handles schema changes |
| Refactor `SandboxMode` → `SandboxLevel` mapping | Backward-compat: host→0, docker→2 | Pipeline refactors without breaking |
| Implement `SandboxExecWrapper` (macOS) | sandbox-exec profile, file/network restrictions | Pipeline produces OS-integration code |
| Implement `LinuxSandboxWrapper` | seccomp/namespaces profile | Pipeline handles platform-conditional code |
| Add `SandboxLevelEnforcer` service | Runtime validation: declared vs host capability | Pipeline produces service code |
| Wire enforcer into agent spawn | Integration with SwarmAgentActor | Pipeline modifies existing actors |

**Exit:** Tasks merged, `dotnet test` green, sandbox-exec restricts a test process on macOS.

## Phase 2: Level 1 (OS-Sandboxed) — Build Level 2

Switch builder to run under sandbox-exec. Fall back to Level 0 if it breaks.

| Task | Builds | Validates |
|------|--------|-----------|
| Container lifecycle manager | Create/start/stop/cleanup for Docker + Apple Container | Level 1 builder produces Docker code |
| Workspace mount strategy | RW workspace, RO everything else | Level 1 file restrictions enforced |
| Network policy enforcement | Allowlist/denylist for containers | Level 1 network restrictions work |
| Resource limit enforcement | CPU, memory, timeout per container | Builder works under OS restrictions |
| Artifact collection on shutdown | Collect outputs before teardown | Sandboxed builder handles multi-file |

**Exit:** Tasks merged, container lifecycle works, Level 1 builder confirmed functional.

## Phase 3: Level 2 (Containerized) — Build Spectrum Selection

Switch builder into a container. The final level.

| Task | Builds | Validates |
|------|--------|-----------|
| Spectrum selection policy | Which level for which agent/role | Container builder produces policy code |
| Runtime policy validation | Enforce declared = actual environment | A2A works from inside container |
| Agent card level display | Godot UI shows sandbox level | Container builder handles UI code |
| Integration test suite | End-to-end tests for all 3 levels | Full pipeline at Level 2 |

**Exit:** All levels validated by running through them, integration tests green.

## Gatekeeper Protocol

1. Swarm completes on `swarm/task-{id}` branch
2. Claude Code reviews diff, runs `dotnet test`
3. Approve → merge to feature branch | Reject → correction task back to swarm | Escalate → flag to human

## Fallback Rules

- Broken code → reject, feed correction task
- Phase transition breaks builder → fall back to previous level, fix, retry
- Adapter auth fails → rotate Copilot→Kimi→Kilo
- Sandbox too restrictive → loosen, document what was needed
- Container can't reach A2A → add network allowlist

## Philosophy

This design validates itself by executing. We expect failures — they're the most valuable output. The plan is deliberately lightweight because we'll learn more from running than from planning.
