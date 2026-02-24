#!/usr/bin/env bash
# Dogfood diagnostic: submit a real scoped task, capture full pipeline trace
# artifacts, and generate a structured diagnostic report.
set -euo pipefail

# ---------------------------------------------------------------------------
# Prerequisite checks
# ---------------------------------------------------------------------------
command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 is required"; exit 1; }
command -v curl >/dev/null 2>&1 || { echo "ERROR: curl is required"; exit 1; }

BASE_URL="${1:-http://127.0.0.1:5080}"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
ARTIFACTS_DIR="/tmp/dogfood-diagnostic-${TIMESTAMP}"
TASK_POLL_ITERATIONS="${TASK_POLL_ITERATIONS:-90}"

mkdir -p "${ARTIFACTS_DIR}"

echo "=== Dogfood Diagnostic ==="
echo "Base URL:      ${BASE_URL}"
echo "Artifacts dir: ${ARTIFACTS_DIR}"
echo ""

# ---------------------------------------------------------------------------
# Step 1: Health-check the runtime
# ---------------------------------------------------------------------------
echo "--- Step 1: Health-check the runtime ---"

health_ready="false"
for i in $(seq 1 10); do
  code="$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/healthz" 2>/dev/null)" || true
  if [[ "${code}" == "200" ]]; then
    health_ready="true"
    break
  fi
  echo "  health check attempt ${i}: http=${code:-none}"
  sleep 2
done

if [[ "${health_ready}" != "true" ]]; then
  echo "ERROR: Runtime not healthy at ${BASE_URL} (last http_code=${code:-none})"
  exit 1
fi

echo "Runtime healthy at ${BASE_URL}"

# ---------------------------------------------------------------------------
# Step 2: Create a run
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 2: Create a run ---"

run_code="$(curl -s -o "${ARTIFACTS_DIR}/01-run-create.json" -w '%{http_code}' \
  -X POST "${BASE_URL}/runs" \
  -H 'content-type: application/json' \
  -d '{"title":"dogfood-diagnostic-run"}')"

if [[ "${run_code}" != "201" ]]; then
  echo "ERROR: Run creation failed http=${run_code}"
  cat "${ARTIFACTS_DIR}/01-run-create.json" 2>/dev/null || true
  exit 1
fi

run_id="$(python3 -c "import json,sys; print(json.load(sys.stdin)['runId'])" < "${ARTIFACTS_DIR}/01-run-create.json")"

if [[ -z "${run_id}" ]]; then
  echo "ERROR: Could not parse runId from response"
  cat "${ARTIFACTS_DIR}/01-run-create.json"
  exit 1
fi

echo "Created run: ${run_id}"

# ---------------------------------------------------------------------------
# Step 3: Submit a real scoped task
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 3: Submit a real scoped task ---"

TASK_DESC="Add a unit test for LegacyRunId.Resolve verifying null-input returns legacy prefix"

task_code="$(curl -s -o "${ARTIFACTS_DIR}/02-task-submit.json" -w '%{http_code}' \
  -X POST "${BASE_URL}/a2a/tasks" \
  -H 'content-type: application/json' \
  -d "{\"title\":\"dogfood diagnostic task\",\"description\":\"${TASK_DESC}\",\"runId\":\"${run_id}\"}")"

if [[ "${task_code}" != "202" ]]; then
  echo "ERROR: Task submit failed http=${task_code}"
  cat "${ARTIFACTS_DIR}/02-task-submit.json" 2>/dev/null || true
  exit 1
fi

task_id="$(python3 -c "import json,sys; print(json.load(sys.stdin)['taskId'])" < "${ARTIFACTS_DIR}/02-task-submit.json")"

if [[ -z "${task_id}" ]]; then
  echo "ERROR: Could not parse taskId from submit response"
  cat "${ARTIFACTS_DIR}/02-task-submit.json"
  exit 1
fi

echo "Submitted task: ${task_id}"
echo "Task description: ${TASK_DESC}"

# ---------------------------------------------------------------------------
# Step 4: Poll for terminal state (done/failed/blocked)
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 4: Poll for terminal state ---"

task_terminal="false"
task_status=""
for i in $(seq 1 "${TASK_POLL_ITERATIONS}"); do
  task_query_code="$(curl -s -o "${ARTIFACTS_DIR}/03-task-poll-latest.json" -w '%{http_code}' \
    "${BASE_URL}/a2a/tasks/${task_id}")" || true

  if [[ "${task_query_code}" == "200" ]]; then
    task_status="$(python3 -c "import json,sys; print(json.load(sys.stdin).get('status',''))" < "${ARTIFACTS_DIR}/03-task-poll-latest.json" 2>/dev/null || echo "")"
    if [[ "${task_status}" == "done" || "${task_status}" == "failed" || "${task_status}" == "blocked" ]]; then
      task_terminal="true"
      break
    fi
  fi

  if (( i % 10 == 0 )); then
    echo "  poll iteration ${i}/${TASK_POLL_ITERATIONS}: status=${task_status:-unknown}"
  fi
  sleep 2
done

if [[ "${task_terminal}" != "true" ]]; then
  echo "ERROR: Task did not reach terminal state within polling window"
  echo "  runId=${run_id} taskId=${task_id} lastStatus=${task_status:-unknown}"
  cat "${ARTIFACTS_DIR}/03-task-poll-latest.json" 2>/dev/null || true
  exit 1
fi

echo "Task reached terminal state: ${task_status}"

# ---------------------------------------------------------------------------
# Step 5: Fetch task snapshot with outputs
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 5: Fetch task snapshot with outputs ---"

snapshot_code="$(curl -s -o "${ARTIFACTS_DIR}/04-task-snapshot.json" -w '%{http_code}' \
  "${BASE_URL}/a2a/tasks/${task_id}")"

if [[ "${snapshot_code}" != "200" ]]; then
  echo "WARNING: Task snapshot fetch failed http=${snapshot_code}"
else
  echo "Task snapshot saved."
fi

# ---------------------------------------------------------------------------
# Step 6: Fetch replay events — run events
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 6: Fetch run replay events ---"

run_events_code="$(curl -s -o "${ARTIFACTS_DIR}/05-run-events.json" -w '%{http_code}' \
  "${BASE_URL}/runs/${run_id}/events")"

if [[ "${run_events_code}" != "200" ]]; then
  echo "WARNING: Run events fetch failed http=${run_events_code}"
else
  run_event_count="$(DIAG_ARTIFACT_PATH="${ARTIFACTS_DIR}/05-run-events.json" \
    python3 -c "
import json, os
d = json.load(open(os.environ['DIAG_ARTIFACT_PATH']))
print(len(d.get('items', [])))
" 2>/dev/null || echo "0")"
  echo "Run events: ${run_event_count} item(s)"
fi

# ---------------------------------------------------------------------------
# Step 7: Fetch replay events — task events
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 7: Fetch task replay events ---"

task_events_code="$(curl -s -o "${ARTIFACTS_DIR}/06-task-events.json" -w '%{http_code}' \
  "${BASE_URL}/memory/tasks/${task_id}/events")"

if [[ "${task_events_code}" != "200" ]]; then
  echo "WARNING: Task events fetch failed http=${task_events_code}"
else
  task_event_count="$(DIAG_ARTIFACT_PATH="${ARTIFACTS_DIR}/06-task-events.json" \
    python3 -c "
import json, os
d = json.load(open(os.environ['DIAG_ARTIFACT_PATH']))
print(len(d.get('items', [])))
" 2>/dev/null || echo "0")"
  echo "Task events: ${task_event_count} item(s)"
fi

# ---------------------------------------------------------------------------
# Step 8: Fetch AG-UI recent events
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 8: Fetch AG-UI recent events ---"

agui_code="$(curl -s -o "${ARTIFACTS_DIR}/07-agui-recent.json" -w '%{http_code}' \
  "${BASE_URL}/ag-ui/recent?count=100")"

if [[ "${agui_code}" != "200" ]]; then
  echo "WARNING: AG-UI recent events fetch failed http=${agui_code}"
else
  agui_total="$(DIAG_ARTIFACT_PATH="${ARTIFACTS_DIR}/07-agui-recent.json" \
    python3 -c "
import json, os
events = json.load(open(os.environ['DIAG_ARTIFACT_PATH']))
print(len(events))
" 2>/dev/null || echo "0")"
  agui_task_count="$(DIAG_ARTIFACT_PATH="${ARTIFACTS_DIR}/07-agui-recent.json" \
    DIAG_TASK_ID="${task_id}" \
    python3 -c "
import json, os
events = json.load(open(os.environ['DIAG_ARTIFACT_PATH']))
tid = os.environ['DIAG_TASK_ID']
print(sum(1 for e in events if e.get('taskId') == tid))
" 2>/dev/null || echo "0")"
  echo "AG-UI events: ${agui_total} total, ${agui_task_count} for this task"
fi

# ---------------------------------------------------------------------------
# Step 9: Generate diagnostic report
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 9: Generate diagnostic report ---"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

DIAG_RUN_ID="${run_id}" \
DIAG_TASK_ID="${task_id}" \
DIAG_TASK_STATUS="${task_status}" \
DIAG_BASE_URL="${BASE_URL}" \
python3 "${SCRIPT_DIR}/generate_diagnostic_report.py" "${ARTIFACTS_DIR}"

echo ""
echo "Dogfood diagnostic complete."
echo "Artifacts saved to: ${ARTIFACTS_DIR}"
echo ""
ls -la "${ARTIFACTS_DIR}/"
