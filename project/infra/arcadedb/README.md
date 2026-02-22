# ArcadeDB Stack + Runtime Integration (Phase 7)

This folder provides a local ArcadeDB stack and validates runtime persistence through `ArcadeDbTaskMemoryWriter`.

## Files

- `docker-compose.yml`: ArcadeDB single-node service.
- `env/local.env`: developer defaults.
- `env/secure-local.env`: template with explicit secret replacement.
- `env/ci.env`: alternate ports for CI jobs.
- `scripts/smoke-e2e.sh`: starts ArcadeDB + runtime and verifies `SwarmTask` persistence end-to-end.

## Start Commands

```bash
# Local
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb
docker compose --env-file env/local.env up -d

# Secure local
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb
docker compose --env-file env/secure-local.env up -d

# CI profile
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb
docker compose --env-file env/ci.env up -d
```

## Stop

```bash
docker compose --env-file env/local.env down
```

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
- Persistence strategy: `DELETE ... WHERE taskId` followed by `INSERT INTO SwarmTask SET ...`.

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
- `taskError` (mapped from runtime snapshot `error`)

## Schema Bootstrap

When `Runtime__ArcadeDbAutoCreateSchema=true`, runtime attempts best-effort setup:

1. `CREATE DOCUMENT TYPE SwarmTask IF NOT EXISTS`
2. `CREATE PROPERTY SwarmTask.<field> IF NOT EXISTS ...`
3. `CREATE INDEX ON SwarmTask (taskId) UNIQUE IF NOT EXISTS`

Bootstrap and write failures are logged as warnings and do not stop task execution.

The compose stack bootstraps default database users with `-Darcadedb.server.defaultDatabases=<db>[root]`.
If you used older bootstrap values and encounter permission errors, recreate the volume with `docker compose down -v`.

## End-to-End Verification

Run the smoke test:

```bash
/Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/arcadedb/scripts/smoke-e2e.sh
```

Manual query example:

```bash
curl -s -u root:playwithdata \
  -H 'content-type: application/json' \
  -d '{"language":"sql","command":"select from SwarmTask order by updatedAt desc limit 5"}' \
  http://127.0.0.1:2480/api/v1/command/swarm_assistant
```
