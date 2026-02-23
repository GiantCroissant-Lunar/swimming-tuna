#!/usr/bin/env python3
"""Pre-tool-use hook - validate tool calls."""

import json
import os
import sys


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        payload = {}

    tool_name = payload.get("tool_name", "unknown")
    adapter = os.environ.get("ADAPTER") or os.environ.get("PRE_TOOL_ADAPTER") or "default"

    # Placeholder: future validation logic would set approved = False to block
    approved = True

    if adapter == "copilot":
        if approved:
            print(json.dumps({"permissionDecision": "approve", "permissionDecisionReason": ""}))
        else:
            print(json.dumps({"permissionDecision": "deny", "permissionDecisionReason": f"tool '{tool_name}' not permitted"}))
    else:
        print(json.dumps({"result": "ok", "tool_name": tool_name}))


if __name__ == "__main__":
    main()
