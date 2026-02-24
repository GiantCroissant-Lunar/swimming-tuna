#!/usr/bin/env bash
# Dogfood diagnostic: submit a real scoped task, capture full pipeline trace
# artifacts, and generate a structured diagnostic report.
set -euo pipefail

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
  run_event_count="$(python3 -c \
    "import json,sys; d=json.load(open('${ARTIFACTS_DIR}/05-run-events.json')); print(len(d.get('items',[])))" \
    2>/dev/null || echo "0")"
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
  task_event_count="$(python3 -c \
    "import json,sys; d=json.load(open('${ARTIFACTS_DIR}/06-task-events.json')); print(len(d.get('items',[])))" \
    2>/dev/null || echo "0")"
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
  agui_total="$(python3 -c \
    "import json,sys; events=json.load(open('${ARTIFACTS_DIR}/07-agui-recent.json')); print(len(events))" \
    2>/dev/null || echo "0")"
  agui_task_count="$(python3 -c \
    "import json,sys; events=json.load(open('${ARTIFACTS_DIR}/07-agui-recent.json')); print(sum(1 for e in events if e.get('taskId')=='${task_id}'))" \
    2>/dev/null || echo "0")"
  echo "AG-UI events: ${agui_total} total, ${agui_task_count} for this task"
fi

# ---------------------------------------------------------------------------
# Step 9: Generate diagnostic report
# ---------------------------------------------------------------------------
echo ""
echo "--- Step 9: Generate diagnostic report ---"

python3 -c "
import json, sys, os, glob

artifacts_dir = '${ARTIFACTS_DIR}'
run_id = '${run_id}'
task_id = '${task_id}'
task_status = '${task_status}'
base_url = '${BASE_URL}'

def safe_load(path):
    try:
        with open(path) as f:
            return json.load(f)
    except Exception:
        return None

# Load all artifacts
task_snapshot = safe_load(os.path.join(artifacts_dir, '04-task-snapshot.json'))
run_events_data = safe_load(os.path.join(artifacts_dir, '05-run-events.json'))
task_events_data = safe_load(os.path.join(artifacts_dir, '06-task-events.json'))
agui_events = safe_load(os.path.join(artifacts_dir, '07-agui-recent.json'))

# --- Task output analysis ---
outputs = {}
output_lengths = {}
has_output = {}
if task_snapshot and isinstance(task_snapshot.get('outputs'), dict):
    for key, val in task_snapshot['outputs'].items():
        text = val if isinstance(val, str) else json.dumps(val) if val else ''
        outputs[key] = text
        output_lengths[key] = len(text)
        has_output[key] = len(text) > 0
elif task_snapshot and isinstance(task_snapshot.get('outputs'), list):
    for i, val in enumerate(task_snapshot['outputs']):
        key = f'output_{i}'
        text = val if isinstance(val, str) else json.dumps(val) if val else ''
        outputs[key] = text
        output_lengths[key] = len(text)
        has_output[key] = len(text) > 0

# Check for planning/build/review outputs by searching keys and content
has_planning = any('plan' in k.lower() for k in outputs) or any('plan' in v[:200].lower() for v in outputs.values() if v)
has_build = any('build' in k.lower() or 'code' in k.lower() for k in outputs) or any('build' in v[:200].lower() for v in outputs.values() if v)
has_review = any('review' in k.lower() for k in outputs) or any('review' in v[:200].lower() for v in outputs.values() if v)

# --- Event analysis ---
run_events = run_events_data.get('items', []) if run_events_data else []
task_events = task_events_data.get('items', []) if task_events_data else []
agui_events = agui_events if isinstance(agui_events, list) else []
agui_task_events = [e for e in agui_events if e.get('taskId') == task_id]

# Event type counts
run_event_types = {}
for e in run_events:
    t = e.get('type', e.get('eventType', 'unknown'))
    run_event_types[t] = run_event_types.get(t, 0) + 1

task_event_types = {}
for e in task_events:
    t = e.get('type', e.get('eventType', 'unknown'))
    task_event_types[t] = task_event_types.get(t, 0) + 1

agui_event_types = {}
for e in agui_task_events:
    t = e.get('type', e.get('eventType', 'unknown'))
    agui_event_types[t] = agui_event_types.get(t, 0) + 1

# Diagnostic event counts (events with 'diagnostic' or 'telemetry' in type)
diagnostic_run = sum(1 for e in run_events if 'diagnostic' in str(e.get('type','')).lower() or 'telemetry' in str(e.get('type','')).lower())
diagnostic_task = sum(1 for e in task_events if 'diagnostic' in str(e.get('type','')).lower() or 'telemetry' in str(e.get('type','')).lower())

# --- Pipeline analysis ---
# Check if planner received context (look for context-related events or outputs)
all_events_text = json.dumps(run_events + task_events)
planner_received_context = 'context' in all_events_text.lower() and ('planner' in all_events_text.lower() or 'plan' in all_events_text.lower())
builder_received_plan = 'plan' in all_events_text.lower() and ('builder' in all_events_text.lower() or 'build' in all_events_text.lower() or 'code' in all_events_text.lower())
reviewer_received_build = 'review' in all_events_text.lower() and ('build' in all_events_text.lower() or 'code' in all_events_text.lower())

# --- Output excerpts (first 500 chars) ---
output_excerpts = {}
for key, text in outputs.items():
    output_excerpts[key] = text[:500] if text else ''

# --- Identify issues ---
issues = []

if task_status == 'failed':
    issues.append({'level': 'CRITICAL', 'message': 'Task reached failed state'})
elif task_status == 'blocked':
    issues.append({'level': 'CRITICAL', 'message': 'Task reached blocked state'})

if not outputs:
    issues.append({'level': 'CRITICAL', 'message': 'No outputs found in task snapshot'})

if not has_planning and outputs:
    issues.append({'level': 'WARNING', 'message': 'No planning output detected'})
if not has_build and outputs:
    issues.append({'level': 'WARNING', 'message': 'No build/code output detected'})
if not has_review and outputs:
    issues.append({'level': 'WARNING', 'message': 'No review output detected'})

if len(run_events) == 0:
    issues.append({'level': 'WARNING', 'message': 'Run replay feed is empty (ArcadeDB may be disabled)'})
if len(task_events) == 0:
    issues.append({'level': 'WARNING', 'message': 'Task replay feed is empty (ArcadeDB may be disabled)'})
if len(agui_task_events) == 0:
    issues.append({'level': 'CRITICAL', 'message': 'No AG-UI events found for this task'})

for key, length in output_lengths.items():
    if length > 0 and length < 50:
        issues.append({'level': 'WARNING', 'message': f'Output \"{key}\" is suspiciously short ({length} chars)'})

# --- Build the report ---
report = {
    'summary': {
        'runId': run_id,
        'taskId': task_id,
        'taskStatus': task_status,
        'baseUrl': base_url,
        'artifactsDir': artifacts_dir,
    },
    'taskOutputs': {
        'hasPlanningOutput': has_planning,
        'hasBuildOutput': has_build,
        'hasReviewOutput': has_review,
        'outputLengths': output_lengths,
    },
    'eventCounts': {
        'runEvents': len(run_events),
        'taskEvents': len(task_events),
        'aguiTotalEvents': len(agui_events),
        'aguiTaskEvents': len(agui_task_events),
        'diagnosticRunEvents': diagnostic_run,
        'diagnosticTaskEvents': diagnostic_task,
    },
    'eventTypes': {
        'runEventTypes': run_event_types,
        'taskEventTypes': task_event_types,
        'aguiEventTypes': agui_event_types,
    },
    'pipelineAnalysis': {
        'plannerReceivedContext': planner_received_context,
        'builderReceivedPlan': builder_received_plan,
        'reviewerReceivedBuild': reviewer_received_build,
    },
    'outputExcerpts': output_excerpts,
    'issues': issues,
}

report_path = os.path.join(artifacts_dir, 'diagnostic-report.json')
with open(report_path, 'w') as f:
    json.dump(report, f, indent=2)

# --- Print summary ---
print()
print('=' * 60)
print('  DIAGNOSTIC REPORT SUMMARY')
print('=' * 60)
print(f'  Run ID:       {run_id}')
print(f'  Task ID:      {task_id}')
print(f'  Task Status:  {task_status}')
print(f'  Artifacts:    {artifacts_dir}')
print()
print('  Outputs:')
print(f'    Planning: {\"YES\" if has_planning else \"NO\"}')
print(f'    Build:    {\"YES\" if has_build else \"NO\"}')
print(f'    Review:   {\"YES\" if has_review else \"NO\"}')
for k, v in output_lengths.items():
    print(f'    {k}: {v} chars')
print()
print('  Events:')
print(f'    Run replay:     {len(run_events)}')
print(f'    Task replay:    {len(task_events)}')
print(f'    AG-UI (task):   {len(agui_task_events)}')
print(f'    Diagnostic run: {diagnostic_run}')
print(f'    Diagnostic task:{diagnostic_task}')
print()
print('  Pipeline:')
print(f'    Planner received context: {\"YES\" if planner_received_context else \"NO/UNKNOWN\"}')
print(f'    Builder received plan:    {\"YES\" if builder_received_plan else \"NO/UNKNOWN\"}')
print(f'    Reviewer received build:  {\"YES\" if reviewer_received_build else \"NO/UNKNOWN\"}')
print()
if issues:
    print('  Issues:')
    for iss in issues:
        print(f'    [{iss[\"level\"]}] {iss[\"message\"]}')
else:
    print('  Issues: None detected')
print()
print(f'  Full report: {report_path}')
print('=' * 60)
"

echo ""
echo "Dogfood diagnostic complete."
echo "Artifacts saved to: ${ARTIFACTS_DIR}"
echo ""
ls -la "${ARTIFACTS_DIR}/"
