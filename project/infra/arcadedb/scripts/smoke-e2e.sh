#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ARCADEDB_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
REPO_DIR="$(cd "${ARCADEDB_DIR}/../../.." && pwd)"
RUNTIME_PROJECT="${REPO_DIR}/project/dotnet/src/SwarmAssistant.Runtime"
RUNTIME_LOG="/tmp/swarm_runtime_arcadedb_smoke.log"

set -a
source "${ARCADEDB_DIR}/env/local.env"
set +a

cleanup() {
  if [[ -n "${RUNTIME_PID:-}" ]]; then
    kill "${RUNTIME_PID}" >/dev/null 2>&1 || true
    wait "${RUNTIME_PID}" >/dev/null 2>&1 || true
  fi
  docker compose --env-file "${ARCADEDB_DIR}/env/local.env" -f "${ARCADEDB_DIR}/docker-compose.yml" down -v >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker compose --env-file "${ARCADEDB_DIR}/env/local.env" -f "${ARCADEDB_DIR}/docker-compose.yml" down -v >/dev/null 2>&1 || true
docker compose --env-file "${ARCADEDB_DIR}/env/local.env" -f "${ARCADEDB_DIR}/docker-compose.yml" up -d

arcadedb_ready="false"
for _ in {1..60}; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${ARCADEDB_HTTP_PORT}/api/v1/ready" 2>/dev/null)" || true
  if [[ "${code}" == "204" || "${code}" == "200" ]]; then
    arcadedb_ready="true"
    break
  fi
  sleep 1
done

if [[ "${arcadedb_ready}" != "true" ]]; then
  echo "ArcadeDB did not become healthy in time (last http_code=${code:-none})"
  docker compose --env-file "${ARCADEDB_DIR}/env/local.env" -f "${ARCADEDB_DIR}/docker-compose.yml" logs --tail=40 || true
  exit 1
fi

DOTNET_ENVIRONMENT=Local \
Runtime__A2AEnabled=true \
Runtime__AutoSubmitDemoTask=false \
Runtime__ArcadeDbEnabled=true \
Runtime__ArcadeDbHttpUrl="http://127.0.0.1:${ARCADEDB_HTTP_PORT}" \
Runtime__ArcadeDbDatabase="${ARCADEDB_DATABASE}" \
Runtime__ArcadeDbUser="root" \
Runtime__ArcadeDbPassword="${ARCADEDB_ROOT_PASSWORD}" \
dotnet run --project "${RUNTIME_PROJECT}" --no-launch-profile >"${RUNTIME_LOG}" 2>&1 &
RUNTIME_PID=$!

runtime_ready="false"
for _ in {1..60}; do
  code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5080/healthz 2>/dev/null)" || true
  if [[ "${code}" == "200" ]]; then
    runtime_ready="true"
    break
  fi
  sleep 1
done

if [[ "${runtime_ready}" != "true" ]]; then
  echo "Runtime did not become healthy in time (last http_code=${code:-none})"
  tail -n 40 "${RUNTIME_LOG}" || true
  exit 1
fi

submit_code="$(curl -s -o /tmp/swarm_arcadedb_submit.json -w '%{http_code}' \
  -X POST http://127.0.0.1:5080/a2a/tasks \
  -H 'content-type: application/json' \
  -d '{"title":"ArcadeDB smoke task","description":"verify task snapshot persistence"}')"

if [[ "${submit_code}" != "202" ]]; then
  echo "A2A submit failed http=${submit_code}"
  cat /tmp/swarm_arcadedb_submit.json
  exit 1
fi

task_id="$(sed -n 's/.*"taskId":"\([^"]*\)".*/\1/p' /tmp/swarm_arcadedb_submit.json)"
if [[ -z "${task_id}" ]]; then
  echo "Could not parse taskId from submit response"
  cat /tmp/swarm_arcadedb_submit.json
  exit 1
fi

found="false"
for _ in {1..60}; do
  query_code="$(curl -s -o /tmp/swarm_arcadedb_query.json -w '%{http_code}' \
    -u "root:${ARCADEDB_ROOT_PASSWORD}" \
    -H 'content-type: application/json' \
    -d "{\"language\":\"sql\",\"command\":\"select from SwarmTask where taskId = '${task_id}'\"}" \
    "http://127.0.0.1:${ARCADEDB_HTTP_PORT}/api/v1/command/${ARCADEDB_DATABASE}" || true)"

  if [[ "${query_code}" == "200" ]] && rg -q "${task_id}" /tmp/swarm_arcadedb_query.json; then
    found="true"
    break
  fi

  sleep 1
done

if [[ "${found}" != "true" ]]; then
  echo "Task was not found in ArcadeDB after submit taskId=${task_id}"
  echo "--- submit ---"
  cat /tmp/swarm_arcadedb_submit.json
  echo "--- query ---"
  cat /tmp/swarm_arcadedb_query.json
  echo "--- runtime log tail ---"
  tail -n 80 "${RUNTIME_LOG}" || true
  exit 1
fi

memory_code="$(curl -s -o /tmp/swarm_arcadedb_memory.json -w '%{http_code}' \
  'http://127.0.0.1:5080/memory/tasks?limit=10')"
if [[ "${memory_code}" != "200" ]] || ! rg -q "${task_id}" /tmp/swarm_arcadedb_memory.json; then
  echo "Runtime memory endpoint did not return submitted task taskId=${task_id} http=${memory_code}"
  echo "--- memory ---"
  cat /tmp/swarm_arcadedb_memory.json
  exit 1
fi

action_code="$(curl -s -o /tmp/swarm_arcadedb_action_memory.json -w '%{http_code}' \
  -X POST http://127.0.0.1:5080/ag-ui/actions \
  -H 'content-type: application/json' \
  -d '{"actionId":"load_memory","payload":{"source":"arcadedb-smoke","limit":10}}')"
if [[ "${action_code}" != "200" ]]; then
  echo "AG-UI load_memory action failed http=${action_code}"
  cat /tmp/swarm_arcadedb_action_memory.json
  exit 1
fi

echo "ArcadeDB smoke passed taskId=${task_id}"
cat /tmp/swarm_arcadedb_query.json
