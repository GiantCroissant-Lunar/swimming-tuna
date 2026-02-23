#!/usr/bin/env python3
"""Search Supermemory for cross-session memories."""

from __future__ import annotations

import json
import sys
import urllib.request
from pathlib import Path

API_URL = "https://api.supermemory.ai/v3/search"


def get_api_key() -> str | None:
    """Read API key from project or global config."""
    locations = [
        Path(".claude/.supermemory-claude/config.json"),
        Path.home() / ".supermemory-claude" / "config.json",
        Path.home() / ".supermemory-claude" / "credentials.json",
    ]
    for loc in locations:
        if loc.exists():
            data = json.loads(loc.read_text())
            key = data.get("apiKey") or data.get("api_key")
            if key:
                return key
    return None


def main() -> None:
    if len(sys.argv) < 2:
        print("Usage: search.py <query>", file=sys.stderr)
        sys.exit(1)

    query = " ".join(sys.argv[1:])
    api_key = get_api_key()
    if not api_key:
        print("Error: no Supermemory API key found", file=sys.stderr)
        sys.exit(1)

    req = urllib.request.Request(
        API_URL,
        data=json.dumps({"q": query, "limit": 10}).encode(),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req) as resp:
        result = json.loads(resp.read())
        for mem in result.get("results", []):
            content = mem.get("content") or mem.get("memory") or ""
            similarity = mem.get("similarity", 0)
            print(f"[{similarity:.0%}] {content[:200]}")

    if not result.get("results"):
        print("No memories found.")


if __name__ == "__main__":
    main()
