# Langfuse Stack (Phase 1)

This folder provides a local Langfuse stack for the swarm runtime.

## Files

- `docker-compose.yml`: base service stack.
- `env/local.env`: developer defaults.
- `env/secure-local.env`: local-only with stricter secrets/telemetry.
- `env/ci.env`: alternate ports for CI jobs.

## Start Commands

```bash
# Local
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/langfuse
docker compose --env-file env/local.env up -d

# Secure local
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/langfuse
docker compose --env-file env/secure-local.env up -d

# CI profile
cd /Users/apprenticegc/Work/lunar-horse/yokan-projects/swimming-tuna/project/infra/langfuse
docker compose --env-file env/ci.env up -d
```

## Stop

```bash
docker compose --env-file env/local.env down
```

## Notes

- Replace all `CHANGE_ME_*` values before using `secure-local.env`.
- Keep env files out of source control if you add real secrets.
