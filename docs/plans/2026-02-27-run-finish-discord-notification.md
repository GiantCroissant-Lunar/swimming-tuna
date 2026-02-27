# Run Finish Discord Notification Plan

**Date:** 2026-02-27
**Status:** Proposed
**Owner:** runtime/dogfooding

## Problem

During dogfood runs, operators must manually poll `/runs/{runId}/tasks` to know
when a run is complete. This is easy to miss for long-running runs and causes
slow follow-through (review, merge, PR, retro).

Observed case (RFC-014 z.ai run):

- all 15 tasks moved into `building`
- no immediate terminal transitions
- no push notification when terminal state is eventually reached

## Goal

Send a Discord notification when a run reaches terminal state so the gatekeeper
can immediately act.

## Non-goals

- Replacing AG-UI event streaming
- Adding Slack/Teams in the first iteration
- Requiring ArcadeDB or Langfuse to be enabled

## Terminal State Definition

A run is terminal when all tasks under `/runs/{runId}/tasks` are in:

- `done`
- `failed`
- `blocked`

Run outcome:

- `success` when `failed=0` and `blocked=0`
- `attention-required` otherwise

## Proposed Solution

Two-phase implementation to unblock quickly.

### Phase 1 (fast): external poller + Discord webhook

Add a small script (for local ops/CI) that:

1. Polls `/runs/{runId}/tasks` every N seconds.
2. Computes status counts and terminal check.
3. Posts one message to Discord Incoming Webhook when terminal.
4. Exits non-zero if outcome is `attention-required` (optional flag).

Required env vars:

- `BASE_URL` (default `http://127.0.0.1:5080`)
- `RUN_ID`
- `DISCORD_WEBHOOK_URL`

Optional env vars:

- `POLL_SECONDS` (default `15`)
- `MAX_WAIT_SECONDS` (default `0`, unlimited)
- `FAIL_ON_ATTENTION` (`0|1`, default `0`)

### Phase 2 (native): runtime notifier hook

Add runtime-level notification sink:

- Emit notification from RunCoordinator when run transitions terminal.
- Keep webhook URL in env/config (`Runtime__Notifications__DiscordWebhookUrl`).
- Reuse run summary payload from run/task registries.

This removes client polling and ensures one canonical notification source.

## Discord Payload (MVP)

Use webhook JSON with at least:

- title: `Run finished: <runId>`
- status: `success` or `attention-required`
- counts: `done/failed/blocked/total`
- timestamps: created + completed check time
- links:
  - `/runs/{runId}`
  - `/runs/{runId}/tasks`
  - `/runs/{runId}/events`

Example content:

```text
Swarm run finished: run-abc123
Status: attention-required
Counts: done=12 failed=2 blocked=1 total=15
Inspect: http://127.0.0.1:5080/runs/run-abc123/tasks
```

## Reliability Requirements

- Exactly one terminal notification per run (dedupe by `runId`).
- Phase 1 dedupe scope is per poller process (in-memory); restarting the poller may re-send.
- Phase 2 must enforce durable dedupe in runtime state so restarts still keep one notification.
- Poller tolerates transient HTTP errors with retry.
- Do not print webhook secrets in logs.
- Handle empty/non-JSON responses safely (no crash loops).

## Security

- Treat `DISCORD_WEBHOOK_URL` as secret.
- Keep it in env/secret store only.
- Redact webhook in diagnostics.

## Acceptance Criteria

1. Start run, wait for terminal state, Discord receives one message.
2. Message includes runId and terminal counts.
3. If run has failed/blocked tasks, message clearly marks attention-required.
4. Poller exits within one poll interval after terminal transition.

## Suggested Task Breakdown

1. Add `scripts/notify-run-finish-discord.sh`.
2. Add Taskfile target `notify:run:discord`.
3. Add run-playbook section with one command example.
4. Add tests for terminal-state evaluation helper (if extracted).
5. Optional: runtime-native notifier RFC follow-up task.

## Operator Command (target flow)

```bash
RUN_ID=<run-id> \
DISCORD_WEBHOOK_URL=<webhook> \
task notify:run:discord
```
