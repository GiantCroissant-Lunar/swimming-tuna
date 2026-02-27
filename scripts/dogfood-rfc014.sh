#!/usr/bin/env bash
# Submit the RFC-014 task set as a swarm run via /runs + /a2a/tasks.
set -euo pipefail

command -v curl >/dev/null 2>&1 || { echo "ERROR: curl is required"; exit 1; }
command -v python3 >/dev/null 2>&1 || { echo "ERROR: python3 is required"; exit 1; }

BASE_URL="${1:-http://127.0.0.1:5080}"
RUN_TITLE="${RUN_TITLE:-rfc-014-run-orchestration-git-tree}"
TMP_DIR="$(mktemp -d /tmp/rfc014-run-XXXXXX)"

cleanup() {
  rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

echo "=== RFC-014 Swarm Kickoff ==="
echo "Base URL : ${BASE_URL}"
echo "Run title: ${RUN_TITLE}"
echo

health_code="$(curl -s -o /dev/null -w '%{http_code}' "${BASE_URL}/healthz" || true)"
if [[ "${health_code}" != "200" ]]; then
  echo "ERROR: runtime is not healthy at ${BASE_URL} (http=${health_code:-none})"
  exit 1
fi

if [[ "${ALLOW_ACTIVE_TASKS:-0}" != "1" ]]; then
  if ! active_tasks_json="$(curl -fsS "${BASE_URL}/a2a/tasks" 2>"${TMP_DIR}/active-tasks.err")"; then
    echo "ERROR: failed to fetch task list from ${BASE_URL}/a2a/tasks"
    cat "${TMP_DIR}/active-tasks.err" 2>/dev/null || true
    exit 1
  fi
  if ! active_count="$(printf '%s' "${active_tasks_json}" | python3 -c 'import json,sys; items=json.load(sys.stdin); print(sum(1 for t in items if t.get("status") not in ("done","failed","blocked")))')"; then
    echo "ERROR: failed to parse /a2a/tasks response for active task count."
    exit 1
  fi
  if [[ "${active_count}" != "0" ]]; then
    echo "ERROR: found ${active_count} active task(s)."
    echo "Wait for tasks to reach done/failed/blocked before starting a new RFC-014 kickoff run."
    echo "Override (not recommended): ALLOW_ACTIVE_TASKS=1 bash scripts/dogfood-rfc014.sh"
    exit 1
  fi
fi

run_payload="$(RUN_TITLE="${RUN_TITLE}" python3 - <<'PY'
import json, os
print(json.dumps({"title": os.environ["RUN_TITLE"]}))
PY
)"

run_code="$(curl -s -o "${TMP_DIR}/run-create.json" -w '%{http_code}' \
  -X POST "${BASE_URL}/runs" \
  -H 'content-type: application/json' \
  -d "${run_payload}")"

if [[ "${run_code}" != "201" ]]; then
  echo "ERROR: run creation failed (http=${run_code})"
  cat "${TMP_DIR}/run-create.json" 2>/dev/null || true
  exit 1
fi

run_id="$(python3 -c "import json,sys; print(json.load(sys.stdin)['runId'])" < "${TMP_DIR}/run-create.json")"
if [[ -z "${run_id}" ]]; then
  echo "ERROR: failed to parse runId"
  cat "${TMP_DIR}/run-create.json" 2>/dev/null || true
  exit 1
fi

echo "Created runId: ${run_id}"
echo

tasks=(
  "RunSpan and RunSpanStatus contracts|Add RunSpan/RunSpanStatus shared types aligned with RFC-013 hierarchy contracts."
  "Add SwarmRole.Decomposer|Introduce Decomposer role enum value and wiring for role dispatch."
  "Decomposer prompt template|Add role prompt template that outputs strict JSON task definitions."
  "RunCoordinatorActor lifecycle|Implement per-run actor with accepted->decomposing->executing->merging->ready-for-pr->done transitions."
  "Add /a2a/runs endpoints|Implement POST /a2a/runs and GET run/list endpoints with status URLs."
  "Dispatcher run integration|Create RunCoordinatorActor from run submissions and route run-scoped tasks."
  "Worktree parent branch support|Extend EnsureWorktreeAsync(taskId,parentBranch) preserving backward compatibility."
  "Merge-back API in WorkspaceBranchManager|Implement MergeTaskBranchAsync(taskId,targetBranch) with conflict detection."
  "Feature branch lifecycle|Create feature branch at run acceptance and push branch on ready-for-pr."
  "Run-level AG-UI events|Emit agui.run.accepted/decomposing/executing/task-merged/merge-conflict/ready-for-pr/done."
  "Task completion callback to run actor|Notify RunCoordinatorActor when run-scoped TaskCoordinatorActor instances complete."
  "RunCoordinatorActor unit tests|Cover lifecycle transitions, merge gate serialization, and conflict paths."
  "WorkspaceBranchManager merge tests|Unit test merge-back success/conflict/branch-not-found behavior."
  "Run integration test|End-to-end test for one run with two tasks validating feature branch + merge order."
  "Dogfood validation + notes|Dogfood RFC-014 run and capture findings/risks in docs/dogfooding/runs."
)

submitted=0
for i in "${!tasks[@]}"; do
  item="${tasks[$i]}"
  title="${item%%|*}"
  description="${item#*|}"

  task_payload="$(TITLE="${title}" DESCRIPTION="${description}" RUN_ID="${run_id}" python3 - <<'PY'
import json, os
print(json.dumps({
    "title": os.environ["TITLE"],
    "description": os.environ["DESCRIPTION"],
    "runId": os.environ["RUN_ID"]
}))
PY
)"

  task_json="${TMP_DIR}/task-$((i+1)).json"
  task_code="$(curl -s -o "${task_json}" -w '%{http_code}' \
    -X POST "${BASE_URL}/a2a/tasks" \
    -H 'content-type: application/json' \
    -d "${task_payload}")"

  if [[ "${task_code}" != "202" ]]; then
    echo "ERROR: task submission failed at index $((i+1)) (http=${task_code})"
    cat "${task_json}" 2>/dev/null || true
    exit 1
  fi

  task_id="$(python3 -c "import json,sys; print(json.load(sys.stdin)['taskId'])" < "${task_json}")"
  echo "[$((i+1))/$((${#tasks[@]}))] ${task_id}  ${title}"
  submitted=$((submitted + 1))
done

echo
echo "Submitted ${submitted} tasks to run ${run_id}."
echo "Monitor:"
echo "  curl -N ${BASE_URL}/ag-ui/events"
echo "  curl -s ${BASE_URL}/runs/${run_id}"
echo "  curl -s ${BASE_URL}/runs/${run_id}/tasks"
echo "  curl -s ${BASE_URL}/runs/${run_id}/events"
