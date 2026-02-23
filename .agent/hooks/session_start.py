#!/usr/bin/env python3
"""Session start hook - inject context from prior sessions."""

import json
import sys


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        payload = {}

    session_id = payload.get("session_id", "unknown")
    # Placeholder: future Supermemory search for relevant context
    print(json.dumps({"result": "ok", "session_id": session_id}))


if __name__ == "__main__":
    main()
