# MVP Architecture (CLI-First Swarm)

This MVP borrows patterns from the reference set under `/ref-projects` while keeping implementation minimal.

## Borrowed Patterns

- Typed role flow (`planner -> builder -> reviewer -> finalizer`) inspired by swarm role separation.
- Durable event log (`data/events.jsonl`) and task state (`data/tasks.json`) for replay/debug.
- Adapter routing with ordered fallbacks to avoid single-provider lock-in.

## Runtime Design

- `src/lib/adapters.mjs`: probes CLIs and executes role prompts with fallback.
- `src/lib/orchestrator.mjs`: runs deterministic step machine per task.
- `src/lib/store.mjs`: persists tasks/events.
- `src/index.mjs`: CLI entrypoint for `init`, `status`, and `run`.
- `dotnet/src/SwarmAssistant.Runtime`: phase-1 bootstrap host for upcoming Akka-based runtime.
- `infra/langfuse`: local Langfuse stack with `local`, `secure-local`, and `ci` env profiles.

## Provider Strategy

Priority is subscription-backed local CLIs:

1. Copilot CLI
2. Cline CLI
3. Kimi CLI
4. Local fallback adapter

No direct provider API key integration is included in this MVP.

## Known Constraints

- Adapter probes only validate command availability; execution can still fail on auth or environment restrictions.
- In restricted sandboxes, CLIs that need sockets/process brokers may fail and fallback will trigger.
