#!/usr/bin/env python3
"""Session start hook — inject context from prior sessions.

Reads config.json (synced by adapter) to decide whether to call Supermemory.
Claude Code sets enabled=false (plugin handles it); Cline/Copilot set enabled=true.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path


def _get_config_path() -> Path:
    """Return path to config.json next to this script."""
    return Path(__file__).resolve().parent / "config.json"


def _load_supermemory_context(project_root: str, adapter: str) -> str:
    """Search Supermemory and return formatted context string."""
    # Add lib dir to path for supermemory_client import
    lib_dir = str(Path(__file__).resolve().parent.parent / "lib")
    if lib_dir not in sys.path:
        sys.path.insert(0, lib_dir)

    from supermemory_client import (
        combine_contexts,
        format_context,
        get_api_key,
        get_container_tag,
        get_profile,
        get_repo_container_tag,
    )

    api_key = get_api_key(project_root)
    if not api_key:
        return ""

    cwd = project_root
    personal_tag = get_container_tag(cwd)
    repo_tag = get_repo_container_tag(cwd)

    query = "session start context"
    personal_result = get_profile(api_key, personal_tag, query)
    repo_result = get_profile(api_key, repo_tag, query)

    personal_ctx = format_context(personal_result, "Personal Memories")
    repo_ctx = format_context(repo_result, "Project Knowledge (Shared across team)")

    return combine_contexts(personal_ctx, repo_ctx)


def main() -> None:
    try:
        payload = json.load(sys.stdin)
    except (json.JSONDecodeError, EOFError, ValueError):
        payload = {}

    session_id = payload.get("session_id", "unknown")
    result: dict = {"result": "ok", "session_id": session_id}

    # Read config to decide whether to use Supermemory
    config_path = _get_config_path()
    try:
        config = json.loads(config_path.read_text()) if config_path.exists() else {}
    except (json.JSONDecodeError, OSError):
        config = {}

    supermemory_enabled = config.get("supermemory", {}).get("enabled", False)

    if supermemory_enabled:
        try:
            # Resolve project root: two levels up from hooks dir
            project_root = str(Path(__file__).resolve().parent.parent.parent)
            adapter = config.get("adapter", "unknown")
            context = _load_supermemory_context(project_root, adapter)
            if context:
                result["additionalContext"] = context
        except Exception:
            pass  # Never block the session

    print(json.dumps(result))


if __name__ == "__main__":
    main()
