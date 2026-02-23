#!/usr/bin/env python3
"""Post-tool-use hook - observe tool call results."""

import json
import sys


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError):
        payload = {}

    # No output needed; placeholder for future observation logic
    _ = payload


if __name__ == "__main__":
    main()
