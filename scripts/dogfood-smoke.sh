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
TASK_POLL_ITERATIONS="${TASK_POLL_ITERATIONS:-60}"

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
# Step 4: Poll for task terminal state (done or failed)
# ---------------------------------------------------------------------------
task_terminal="false"
task_status=""
for _ in $(seq 1 "${TASK_POLL_ITERATIONS}"); do
  task_query_code="$(curl -s -o /tmp/smoke_task_get.json -w '%{http_code}' \
    "${BASE_URL}/a2a/tasks/${task_id}")" || true

  if [[ "${task_query_code}" == "200" ]]; then
    task_status="$(python3 -c "import json; print(json.load(open('/tmp/smoke_task_get.json')).get('status', ''))" 2>/dev/null || echo "")"
    if [[ "${task_status}" == "done" || "${task_status}" == "failed" ]]; then
      task_terminal="true"
      break
    fi
  fi
  sleep 2
done

if [[ "${task_terminal}" != "true" ]]; then
  echo "ERROR: Task did not reach terminal state within polling window runId=${run_id} taskId=${task_id} lastStatus=${task_status:-unknown}"
  cat /tmp/smoke_task_get.json || true
  tail -n 40 "${RUNTIME_LOG}" || true
  exit 1
fi

echo "Task reached terminal state: ${task_status}  taskId=${task_id}"
cp /tmp/smoke_task_get.json "${ARTIFACTS_DIR}/task-terminal.json"

# ---------------------------------------------------------------------------
# Step 5: Fetch run replay feed and assert non-empty when ArcadeDB is enabled
# ---------------------------------------------------------------------------
events_code="$(curl -s -o /tmp/smoke_run_events.json -w '%{http_code}' \
  "${BASE_URL}/runs/${run_id}/events")"

if [[ "${events_code}" != "200" ]]; then
  echo "ERROR: Run events fetch failed http=${events_code} runId=${run_id} taskId=${task_id}"
  cat /tmp/smoke_run_events.json || true
  exit 1
fi

cp /tmp/smoke_run_events.json "${ARTIFACTS_DIR}/replay-run-events.json"

run_items_count="$(python3 -c \
  "import json,sys; d=json.load(open('/tmp/smoke_run_events.json')); print(len(list(d.get('items',[]))))" \
  2>/dev/null || echo "0")"

if [[ "${Runtime__ArcadeDbEnabled:-false}" == "true" ]]; then
  if [[ "${run_items_count}" -eq "0" ]]; then
    echo "ERROR: Run replay feed is empty after task completion runId=${run_id} taskId=${task_id}"
    cat /tmp/smoke_run_events.json
    exit 1
  fi
  echo "Run replay feed: ${run_items_count} event(s)  runId=${run_id}"
else
  echo "Note: ArcadeDB disabled — run replay feed empty (${run_items_count} items) is expected. Skipping non-empty assertion."
fi

# ---------------------------------------------------------------------------
# Step 6: Fetch task replay feed and assert non-empty when ArcadeDB is enabled
# ---------------------------------------------------------------------------
task_events_code="$(curl -s -o /tmp/smoke_task_events.json -w '%{http_code}' \
  "${BASE_URL}/memory/tasks/${task_id}/events")"

if [[ "${task_events_code}" != "200" ]]; then
  echo "ERROR: Task events fetch failed http=${task_events_code} taskId=${task_id} runId=${run_id}"
  cat /tmp/smoke_task_events.json || true
  exit 1
fi

cp /tmp/smoke_task_events.json "${ARTIFACTS_DIR}/replay-task-events.json"

task_items_count="$(python3 -c \
  "import json,sys; d=json.load(open('/tmp/smoke_task_events.json')); print(len(list(d.get('items',[]))))" \
  2>/dev/null || echo "0")"

if [[ "${Runtime__ArcadeDbEnabled:-false}" == "true" ]]; then
  if [[ "${task_items_count}" -eq "0" ]]; then
    echo "ERROR: Task replay feed is empty after task completion taskId=${task_id} runId=${run_id}"
    cat /tmp/smoke_task_events.json
    exit 1
  fi
  echo "Task replay feed: ${task_items_count} event(s)  taskId=${task_id}"
else
  echo "Note: ArcadeDB disabled — task replay feed empty (${task_items_count} items) is expected. Skipping non-empty assertion."
fi

# ---------------------------------------------------------------------------
# Step 7: Assert AG-UI event stream has task-related events (always available)
# ---------------------------------------------------------------------------
recent_code="$(curl -s -o /tmp/smoke_agui_recent.json -w '%{http_code}' \
  "${BASE_URL}/ag-ui/recent?count=50")"

if [[ "${recent_code}" != "200" ]]; then
  echo "ERROR: AG-UI recent events fetch failed http=${recent_code}"
  cat /tmp/smoke_agui_recent.json || true
  exit 1
fi

cp /tmp/smoke_agui_recent.json "${ARTIFACTS_DIR}/agui-recent.json"

agui_task_count="$(python3 -c \
  "import json,sys; events=json.load(open('/tmp/smoke_agui_recent.json')); print(sum(1 for e in events if e.get('taskId')=='${task_id}'))" \
  2>/dev/null || echo "0")"

if [[ "${agui_task_count}" -eq "0" ]]; then
  echo "ERROR: No AG-UI events found for taskId=${task_id} runId=${run_id}"
  echo "--- AG-UI recent events snippet ---"
  head -c 2000 /tmp/smoke_agui_recent.json || true
  exit 1
fi

echo "AG-UI stream: ${agui_task_count} event(s) for taskId=${task_id}"

# ---------------------------------------------------------------------------
# Step 8: Write status summary
# ---------------------------------------------------------------------------
STATUS_SUMMARY="${ARTIFACTS_DIR}/status-summary.json"
cat >"${STATUS_SUMMARY}" <<EOF
{
  "status": "pass",
  "runId": "${run_id}",
  "taskId": "${task_id}",
  "taskStatus": "${task_status}",
  "runReplayItems": ${run_items_count},
  "taskReplayItems": ${task_items_count},
  "aguiTaskEvents": ${agui_task_count},
  "arcadeDbEnabled": "${Runtime__ArcadeDbEnabled:-false}",
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
}
EOF

echo "Dogfood smoke passed  runId=${run_id}  taskId=${task_id}"
cat "${STATUS_SUMMARY}"
