"""Generate a structured diagnostic report from dogfood-diagnostic artifacts.

Usage:
    python3 scripts/generate_diagnostic_report.py <artifacts_dir>

Reads JSON artifacts produced by dogfood-diagnostic.sh and writes
diagnostic-report.json alongside them.  Prints a human-readable summary
to stdout.
"""

import json
import os
import sys


def safe_load(path: str):
    """Load a JSON file, returning None on any error."""
    try:
        with open(path) as f:
            return json.load(f)
    except Exception:
        return None


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: generate_diagnostic_report.py <artifacts_dir>", file=sys.stderr)
        sys.exit(1)

    artifacts_dir = sys.argv[1]

    # Remaining parameters come from environment variables to avoid shell
    # injection when the caller is a bash script.
    run_id = os.environ.get("DIAG_RUN_ID", "")
    task_id = os.environ.get("DIAG_TASK_ID", "")
    task_status = os.environ.get("DIAG_TASK_STATUS", "")
    base_url = os.environ.get("DIAG_BASE_URL", "")

    # Load all artifacts -------------------------------------------------------
    task_snapshot = safe_load(os.path.join(artifacts_dir, "04-task-snapshot.json"))
    run_events_data = safe_load(os.path.join(artifacts_dir, "05-run-events.json"))
    task_events_data = safe_load(os.path.join(artifacts_dir, "06-task-events.json"))
    agui_events = safe_load(os.path.join(artifacts_dir, "07-agui-recent.json"))

    # --- Task output analysis -------------------------------------------------
    outputs: dict[str, str] = {}
    output_lengths: dict[str, int] = {}
    has_output: dict[str, bool] = {}

    if task_snapshot and isinstance(task_snapshot.get("outputs"), dict):
        for key, val in task_snapshot["outputs"].items():
            text = val if isinstance(val, str) else json.dumps(val) if val else ""
            outputs[key] = text
            output_lengths[key] = len(text)
            has_output[key] = len(text) > 0
    elif task_snapshot and isinstance(task_snapshot.get("outputs"), list):
        for i, val in enumerate(task_snapshot["outputs"]):
            key = f"output_{i}"
            text = val if isinstance(val, str) else json.dumps(val) if val else ""
            outputs[key] = text
            output_lengths[key] = len(text)
            has_output[key] = len(text) > 0

    # Check for planning/build/review outputs by searching keys and content
    has_planning = any("plan" in k.lower() for k in outputs) or any(
        "plan" in v[:200].lower() for v in outputs.values() if v
    )
    has_build = any(
        "build" in k.lower() or "code" in k.lower() for k in outputs
    ) or any("build" in v[:200].lower() for v in outputs.values() if v)
    has_review = any("review" in k.lower() for k in outputs) or any(
        "review" in v[:200].lower() for v in outputs.values() if v
    )

    # --- Event analysis -------------------------------------------------------
    run_events = run_events_data.get("items", []) if run_events_data else []
    task_events = task_events_data.get("items", []) if task_events_data else []
    agui_events = agui_events if isinstance(agui_events, list) else []
    agui_task_events = [e for e in agui_events if e.get("taskId") == task_id]

    # Event type counts
    run_event_types: dict[str, int] = {}
    for e in run_events:
        t = e.get("type", e.get("eventType", "unknown"))
        run_event_types[t] = run_event_types.get(t, 0) + 1

    task_event_types: dict[str, int] = {}
    for e in task_events:
        t = e.get("type", e.get("eventType", "unknown"))
        task_event_types[t] = task_event_types.get(t, 0) + 1

    agui_event_types: dict[str, int] = {}
    for e in agui_task_events:
        t = e.get("type", e.get("eventType", "unknown"))
        agui_event_types[t] = agui_event_types.get(t, 0) + 1

    # Diagnostic event counts
    diagnostic_run = sum(
        1
        for e in run_events
        if "diagnostic" in str(e.get("type", "")).lower()
        or "telemetry" in str(e.get("type", "")).lower()
    )
    diagnostic_task = sum(
        1
        for e in task_events
        if "diagnostic" in str(e.get("type", "")).lower()
        or "telemetry" in str(e.get("type", "")).lower()
    )

    # --- Pipeline analysis ----------------------------------------------------
    all_events_text = json.dumps(run_events + task_events)
    planner_received_context = "context" in all_events_text.lower() and (
        "planner" in all_events_text.lower() or "plan" in all_events_text.lower()
    )
    builder_received_plan = "plan" in all_events_text.lower() and (
        "builder" in all_events_text.lower()
        or "build" in all_events_text.lower()
        or "code" in all_events_text.lower()
    )
    reviewer_received_build = "review" in all_events_text.lower() and (
        "build" in all_events_text.lower() or "code" in all_events_text.lower()
    )

    # --- Output excerpts (first 500 chars) ------------------------------------
    output_excerpts: dict[str, str] = {}
    for key, text in outputs.items():
        output_excerpts[key] = text[:500] if text else ""

    # --- Identify issues ------------------------------------------------------
    issues: list[dict[str, str]] = []

    if task_status == "failed":
        issues.append({"level": "CRITICAL", "message": "Task reached failed state"})
    elif task_status == "blocked":
        issues.append({"level": "CRITICAL", "message": "Task reached blocked state"})

    if not outputs:
        issues.append(
            {"level": "CRITICAL", "message": "No outputs found in task snapshot"}
        )

    if not has_planning and outputs:
        issues.append({"level": "WARNING", "message": "No planning output detected"})
    if not has_build and outputs:
        issues.append({"level": "WARNING", "message": "No build/code output detected"})
    if not has_review and outputs:
        issues.append({"level": "WARNING", "message": "No review output detected"})

    if len(run_events) == 0:
        issues.append(
            {
                "level": "WARNING",
                "message": "Run replay feed is empty (ArcadeDB may be disabled)",
            }
        )
    if len(task_events) == 0:
        issues.append(
            {
                "level": "WARNING",
                "message": "Task replay feed is empty (ArcadeDB may be disabled)",
            }
        )
    if len(agui_task_events) == 0:
        issues.append(
            {"level": "CRITICAL", "message": "No AG-UI events found for this task"}
        )

    for key, length in output_lengths.items():
        if 0 < length < 50:
            issues.append(
                {
                    "level": "WARNING",
                    "message": f'Output "{key}" is suspiciously short ({length} chars)',
                }
            )

    # --- Build the report -----------------------------------------------------
    report = {
        "summary": {
            "runId": run_id,
            "taskId": task_id,
            "taskStatus": task_status,
            "baseUrl": base_url,
            "artifactsDir": artifacts_dir,
        },
        "taskOutputs": {
            "hasPlanningOutput": has_planning,
            "hasBuildOutput": has_build,
            "hasReviewOutput": has_review,
            "outputLengths": output_lengths,
        },
        "eventCounts": {
            "runEvents": len(run_events),
            "taskEvents": len(task_events),
            "aguiTotalEvents": len(agui_events),
            "aguiTaskEvents": len(agui_task_events),
            "diagnosticRunEvents": diagnostic_run,
            "diagnosticTaskEvents": diagnostic_task,
        },
        "eventTypes": {
            "runEventTypes": run_event_types,
            "taskEventTypes": task_event_types,
            "aguiEventTypes": agui_event_types,
        },
        "pipelineAnalysis": {
            "plannerReceivedContext": planner_received_context,
            "builderReceivedPlan": builder_received_plan,
            "reviewerReceivedBuild": reviewer_received_build,
        },
        "outputExcerpts": output_excerpts,
        "issues": issues,
    }

    report_path = os.path.join(artifacts_dir, "diagnostic-report.json")
    with open(report_path, "w") as f:
        json.dump(report, f, indent=2)

    # --- Print summary --------------------------------------------------------
    print()
    print("=" * 60)
    print("  DIAGNOSTIC REPORT SUMMARY")
    print("=" * 60)
    print(f"  Run ID:       {run_id}")
    print(f"  Task ID:      {task_id}")
    print(f"  Task Status:  {task_status}")
    print(f"  Artifacts:    {artifacts_dir}")
    print()
    print("  Outputs:")
    print(f"    Planning: {'YES' if has_planning else 'NO'}")
    print(f"    Build:    {'YES' if has_build else 'NO'}")
    print(f"    Review:   {'YES' if has_review else 'NO'}")
    for k, v in output_lengths.items():
        print(f"    {k}: {v} chars")
    print()
    print("  Events:")
    print(f"    Run replay:     {len(run_events)}")
    print(f"    Task replay:    {len(task_events)}")
    print(f"    AG-UI (task):   {len(agui_task_events)}")
    print(f"    Diagnostic run: {diagnostic_run}")
    print(f"    Diagnostic task:{diagnostic_task}")
    print()
    print("  Pipeline:")
    plan_ctx = "YES" if planner_received_context else "NO/UNKNOWN"
    build_plan = "YES" if builder_received_plan else "NO/UNKNOWN"
    review_build = "YES" if reviewer_received_build else "NO/UNKNOWN"
    print(f"    Planner received context: {plan_ctx}")
    print(f"    Builder received plan:    {build_plan}")
    print(f"    Reviewer received build:  {review_build}")
    print()
    if issues:
        print("  Issues:")
        for iss in issues:
            print(f"    [{iss['level']}] {iss['message']}")
    else:
        print("  Issues: None detected")
    print()
    print(f"  Full report: {report_path}")
    print("=" * 60)


if __name__ == "__main__":
    main()
