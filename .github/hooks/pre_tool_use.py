#!/usr/bin/env python3
"""Pre-tool-use hook - validate tool calls."""

import json
import sys


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        payload = {}

    tool_name = payload.get("tool_name", "unknown")
    # Placeholder: future validation logic
    print(json.dumps({"result": "ok", "tool_name": tool_name}))


if __name__ == "__main__":
    main()
