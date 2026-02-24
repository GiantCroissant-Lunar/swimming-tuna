#!/usr/bin/env bash
# Dogfooding smoke run: start the runtime, create a run, submit a task,
# verify replay artifacts, and write a status summary.
set -euo pipefail

REPO_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RUNTIME_PROJECT="${REPO_DIR}/project/dotnet/src/SwarmAssistant.Runtime"
RUNTIME_LOG="/tmp/swarm_dogfood_runtime.log"
ARTIFACTS_DIR="${ARTIFACTS_DIR:-/tmp/smoke-artifacts}"
RUNTIME_PORT="${RUNTIME_PORT:-5080}"
BASE_URL="http://127.0.0.1:${RUNTIME_PORT}"

mkdir -p "${ARTIFACTS_DIR}"

# ---------------------------------------------------------------------------
# Cleanup: stop the runtime on exit
# ---------------------------------------------------------------------------
cleanup() {
  if [[ -n "${RUNTIME_PID:-}" ]]; then
    kill "${RUNTIME_PID}" >/dev/null 2>&1 || true
    wait "${RUNTIME_PID}" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# ---------------------------------------------------------------------------
# Start runtime (Development profile, A2A + AG-UI enabled, no ArcadeDB, no demo task)
# ---------------------------------------------------------------------------
DOTNET_ENVIRONMENT=Development \
Runtime__A2AEnabled=true \
Runtime__AgUiEnabled=true \
Runtime__AgUiBindUrl="http://0.0.0.0:${RUNTIME_PORT}" \
Runtime__ArcadeDbEnabled=false \
Runtime__MemoryBootstrapEnabled=false \
Runtime__AutoSubmitDemoTask=false \
  dotnet run --project "${RUNTIME_PROJECT}" --no-launch-profile \
  >"${RUNTIME_LOG}" 2>&1 &
RUNTIME_PID=$!

echo "Runtime PID=${RUNTIME_PID}; waiting for health check..."

runtime_ready="false"
for _ in {1..60}; do
  code="$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/healthz" 2>/dev/null)" || true
  if [[ "${code}" == "200" ]]; then
    runtime_ready="true"
    break
  fi
  sleep 2
done

if [[ "${runtime_ready}" != "true" ]]; then
  echo "ERROR: Runtime did not become healthy in time (last http_code=${code:-none})"
  tail -n 40 "${RUNTIME_LOG}" || true
  exit 1
fi

echo "Runtime healthy."

# ---------------------------------------------------------------------------
# Step 1: Create a run
# ---------------------------------------------------------------------------
run_code="$(curl -s -o /tmp/smoke_run_create.json -w '%{http_code}' \
  -X POST "${BASE_URL}/runs" \
  -H 'content-type: application/json' \
  -d '{"title":"dogfood-smoke-run"}')"

if [[ "${run_code}" != "201" ]]; then
  echo "ERROR: Run creation failed http=${run_code}"
  cat /tmp/smoke_run_create.json
  exit 1
fi

run_id="$(sed -n 's/.*"runId":"\([^"]*\)".*/\1/p' /tmp/smoke_run_create.json)"
if [[ -z "${run_id}" ]]; then
  echo "ERROR: Could not parse runId from response"
  cat /tmp/smoke_run_create.json
  exit 1
fi

echo "Created run: ${run_id}"
cp /tmp/smoke_run_create.json "${ARTIFACTS_DIR}/run-create.json"

# ---------------------------------------------------------------------------
# Step 2: Submit a task linked to the run
# ---------------------------------------------------------------------------
task_code="$(curl -s -o /tmp/smoke_task_submit.json -w '%{http_code}' \
  -X POST "${BASE_URL}/a2a/tasks" \
  -H 'content-type: application/json' \
  -d "{\"title\":\"dogfood smoke task\",\"description\":\"verify runtime pipeline end-to-end\",\"runId\":\"${run_id}\"}")"

if [[ "${task_code}" != "202" ]]; then
  echo "ERROR: Task submit failed http=${task_code}"
  cat /tmp/smoke_task_submit.json
  exit 1
fi

task_id="$(sed -n 's/.*"taskId":"\([^"]*\)".*/\1/p' /tmp/smoke_task_submit.json)"
if [[ -z "${task_id}" ]]; then
  echo "ERROR: Could not parse taskId from submit response"
  cat /tmp/smoke_task_submit.json
  exit 1
fi

echo "Submitted task: ${task_id}"
cp /tmp/smoke_task_submit.json "${ARTIFACTS_DIR}/task-submit.json"

# ---------------------------------------------------------------------------
# Step 3: Verify the task is linked to the run
# ---------------------------------------------------------------------------
run_has_task="false"
for _ in {1..30}; do
  run_query_code="$(curl -s -o /tmp/smoke_run_get.json -w '%{http_code}' \
    "${BASE_URL}/runs/${run_id}")" || true

  if [[ "${run_query_code}" == "200" ]]; then
    task_count="$(sed -n 's/.*"taskCount":\([0-9]*\).*/\1/p' /tmp/smoke_run_get.json)"
    if [[ "${task_count:-0}" -ge "1" ]]; then
      run_has_task="true"
      break
    fi
  fi
  sleep 2
done

if [[ "${run_has_task}" != "true" ]]; then
  echo "ERROR: Task not associated with run after polling runId=${run_id} taskId=${task_id}"
  cat /tmp/smoke_run_get.json
  exit 1
fi

echo "Run has ${task_count} task(s)."
cp /tmp/smoke_run_get.json "${ARTIFACTS_DIR}/run-status.json"

# ---------------------------------------------------------------------------
# Step 4: Fetch replay artifacts (run events + recent AG-UI events)
# ---------------------------------------------------------------------------
events_code="$(curl -s -o /tmp/smoke_run_events.json -w '%{http_code}' \
  "${BASE_URL}/runs/${run_id}/events")"

if [[ "${events_code}" != "200" ]]; then
  echo "ERROR: Run events fetch failed http=${events_code}"
  cat /tmp/smoke_run_events.json
  exit 1
fi

cp /tmp/smoke_run_events.json "${ARTIFACTS_DIR}/replay-snippet.json"
echo "Replay snippet saved."

recent_code="$(curl -s -o /tmp/smoke_agui_recent.json -w '%{http_code}' \
  "${BASE_URL}/ag-ui/recent?count=20")"

if [[ "${recent_code}" == "200" ]]; then
  cp /tmp/smoke_agui_recent.json "${ARTIFACTS_DIR}/agui-recent.json"
fi

# ---------------------------------------------------------------------------
# Step 5: Write status summary
# ---------------------------------------------------------------------------
STATUS_SUMMARY="${ARTIFACTS_DIR}/status-summary.json"
cat >"${STATUS_SUMMARY}" <<EOF
{
  "status": "pass",
  "runId": "${run_id}",
  "taskId": "${task_id}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

echo "Dogfood smoke passed  runId=${run_id}  taskId=${task_id}"
cat "${STATUS_SUMMARY}"
