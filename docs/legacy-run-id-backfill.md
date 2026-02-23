# Legacy Run-ID Backfill

## Background

The `runId` field was introduced in Phase 8 to group related task executions under a
single logical run. Records created before this change (snapshots in `SwarmTask` and
events in `TaskExecutionEvent`) have a `null` or empty `runId` field in ArcadeDB.

## Readability

Legacy records are **already fully readable** without any schema change. The
`ArcadeDbTaskMemoryReader` and `ArcadeDbTaskExecutionEventRepository` parsers apply
a deterministic **synthetic run-ID** whenever the stored value is absent:

```
synthetic runId = "legacy-" + taskId
```

This means all API endpoints (`/memory/tasks`, `/a2a/tasks`, `/memory/tasks/{taskId}`)
return a non-null `runId` for every record, enabling run-based grouping by consumers.
The logic lives in `LegacyRunId.Resolve(string? runId, string taskId)`.

## Synthetic Run-ID Contract

| Stored value | Returned `runId` |
|---|---|
| non-empty string | unchanged |
| `null` or empty | `"legacy-" + taskId` |

Because the derivation is pure and deterministic, repeated reads of the same record
always produce the same synthetic ID.

## Optional Database Migration

If you want to persist the synthetic IDs back to the database (so they survive
schema introspection tools that bypass the reader layer), run the following SQL
once against the `swarm_assistant` ArcadeDB database:

```sql
-- Backfill SwarmTask records that have no runId
UPDATE SwarmTask SET runId = CONCAT('legacy-', taskId) WHERE runId IS NULL;

-- Backfill TaskExecutionEvent records that have no runId
UPDATE TaskExecutionEvent
  SET runId = CONCAT('legacy-', taskId)
  WHERE runId = '' OR runId IS NULL;
```

After running the migration the synthetic-ID logic in the readers remains a safe
no-op; persisted non-empty values are returned as-is.

> **Note:** No schema changes are required. Both types already have a `runId`
> property defined in the schema (created at startup with `IF NOT EXISTS`).
