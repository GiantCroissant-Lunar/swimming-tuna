# Swimming Tuna MVP

CLI-first swarm assistant MVP implemented under `/project`.

## Goals

- Prioritize subscription-backed CLIs before API keys.
- Run a simple multi-role flow: `planner -> builder -> reviewer -> finalizer`.
- Keep durable local state for tasks and event logs.

## What It Does

- Loads role and adapter priorities from `config/swarm.config.json`.
- Probes available CLIs (`copilot`, `cline`, `kimi`) and uses the first working one per role.
- Falls back to a built-in `local-echo` adapter when CLIs are unavailable.
- Persists tasks in `data/tasks.json` and events in `data/events.jsonl`.

## Commands

From repository root:

```bash
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run init
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run status
npm --prefix /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project run run -- --task "Design MVP contracts" --desc "Focus on role state machine and event schema"
```

Or from `/Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project`:

```bash
npm run init
npm run status
npm run run -- --task "Design MVP contracts"
```

## Config Notes

Update `config/swarm.config.json` command templates to match your installed tools.

- `copilot` default uses `gh copilot suggest`.
- `cline` and `kimi` are placeholders and should be edited to the actual CLI invocation you use.
- No provider API key integration is implemented in this MVP by design.
