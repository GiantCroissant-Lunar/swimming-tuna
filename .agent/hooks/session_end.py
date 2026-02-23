#!/usr/bin/env python3
"""Session end hook - save session summary."""

import json
import sys


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        payload = {}

    session_id = payload.get("session_id", "unknown")
    # Placeholder: future Supermemory save of session summary
    print(json.dumps({"result": "ok", "session_id": session_id}))


if __name__ == "__main__":
    main()
