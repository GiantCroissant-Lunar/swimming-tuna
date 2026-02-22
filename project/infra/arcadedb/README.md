# ArcadeDB Integration Notes (Phase 7)

Phase 7 wires runtime task snapshots to ArcadeDB through `ArcadeDbTaskMemoryWriter`.

## Runtime Flags

- `Runtime__ArcadeDbEnabled`
- `Runtime__ArcadeDbHttpUrl`
- `Runtime__ArcadeDbDatabase`
- `Runtime__ArcadeDbUser`
- `Runtime__ArcadeDbPassword`
- `Runtime__ArcadeDbAutoCreateSchema`

## Write Model

- Source: `TaskRegistry` snapshots emitted on task register, transition, role output update, done, and failed.
- Transport: HTTP `POST /api/v1/command/{database}` using ArcadeDB SQL command API.
- Auth: Basic auth when `Runtime__ArcadeDbUser` is set.
- Upsert target: `SwarmTask` document keyed by `taskId`.

Fields written:

- `taskId`
- `title`
- `description`
- `status`
- `createdAt`
- `updatedAt`
- `planningOutput`
- `buildOutput`
- `reviewOutput`
- `summary`
- `error`

## Schema Bootstrap

When `Runtime__ArcadeDbAutoCreateSchema=true`, runtime attempts best-effort setup:

1. `CREATE DOCUMENT TYPE SwarmTask IF NOT EXISTS`
2. `CREATE PROPERTY SwarmTask.<field> IF NOT EXISTS ...`
3. `CREATE INDEX ON SwarmTask (taskId) UNIQUE IF NOT EXISTS`

Bootstrap and write failures are logged as warnings and do not stop task execution.
