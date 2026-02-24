# Unified Agent Skills Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create a unified `.agent/` system with adapters, hooks, rules, and a sync engine that distributes agent config to each CLI/IDE tool's expected locations.

**Architecture:** `.agent/` is the single source of truth. Each tool gets an adapter directory with a `config.yml` declaring sync targets. A Python sync engine reads configs and copies/merges/generates files. Taskfile commands drive the sync.

**Tech Stack:** Python 3.11+, PyYAML, Taskfile v3

---

## Task 1: Create CLAUDE.md pointer file

**Files:**
- Create: `CLAUDE.md`

**Step 1: Create pointer file**

```markdown
# Claude Code

See [AGENTS.md](AGENTS.md) for all project guidance.
```

**Step 2: Verify**

Run: `cat CLAUDE.md`
Expected: pointer content referencing AGENTS.md

**Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "feat: add CLAUDE.md as pointer to AGENTS.md"
```

---

### Task 2: Update AGENTS.md with codebase index

**Files:**
- Modify: `AGENTS.md`

**Step 1: Rewrite AGENTS.md**

Replace current content with comprehensive agent guide including codebase index from `project/ARCHITECTURE.md` and `project/README.md`. Structure:

```markdown
# Agent Guide

## Project Overview
swimming-tuna (SwarmAssistant MVP) - CLI-first swarm assistant.
Tech stack: .NET 9 (Akka.NET, Microsoft Agent Framework), JS/Node CLI, Godot Mono UI, .NET Aspire orchestration, ArcadeDB, Langfuse.

## Project Structure
(directory tree with descriptions)

## Architecture
(condensed from project/ARCHITECTURE.md - actor topology, role pipeline, HTTP endpoints)

## Common Tasks
(build, test, lint, fmt, run:aspire commands)

## Development Guidelines
(conventions, commit style, pre-commit hooks)

## Agent Skills
(existing skills table + new memory skill)

## Agent Adapters
(explain the .agent/adapters/ system and how to sync)
```

Keep under 200 lines. Reference `project/ARCHITECTURE.md` and `project/README.md` for deep details rather than duplicating.

**Step 2: Verify readability**

Run: `wc -l AGENTS.md`
Expected: under 200 lines

**Step 3: Commit**

```bash
git add AGENTS.md
git commit -m "feat: expand AGENTS.md as single source of truth for agent guidance"
```

---

### Task 3: Create adapter configs

**Files:**
- Create: `.agent/adapters/claude/config.yml`
- Create: `.agent/adapters/codex/config.yml`
- Create: `.agent/adapters/cline/config.yml`
- Create: `.agent/adapters/copilot/config.yml`

**Step 1: Create claude adapter**

```yaml
# .agent/adapters/claude/config.yml
name: claude-code
description: Claude Code CLI adapter

capabilities:
  hooks: [SessionStart, Stop, PreCompact]
  mcp: true
  skills: true
  rules: true

sync:
  agents_md:
    target: CLAUDE.md
    action: pointer
    content: |
      # Claude Code

      See [AGENTS.md](AGENTS.md) for all project guidance.

  skills:
    target: .agent/skills/
    action: none

  rules:
    target: .claude/rules/
    action: copy
    source: .agent/rules/
    glob: "*.md"

  hooks:
    target: .claude/settings.json
    action: merge
    format: claude-settings-json
    mapping:
      session_start.py: SessionStart
      session_end.py: Stop
      pre_tool_use.py: PreToolUse
```

**Step 2: Create codex adapter**

```yaml
# .agent/adapters/codex/config.yml
name: codex-cli
description: OpenAI Codex CLI adapter

capabilities:
  hooks: []
  mcp: true
  skills: false
  rules: true

sync:
  agents_md:
    target: AGENTS.md
    action: none

  rules:
    target: .codex/rules/
    action: copy
    source: .agent/rules/
    glob: "*.md"

  hooks:
    action: none
```

**Step 3: Create cline adapter**

```yaml
# .agent/adapters/cline/config.yml
name: cline-cli
description: Cline CLI adapter

capabilities:
  hooks: [TaskStart, TaskResume, PreToolUse, PostToolUse]
  mcp: true
  skills: false
  rules: true

sync:
  agents_md:
    target: .clinerules/AGENTS.md
    action: copy
    source: AGENTS.md

  rules:
    target: .clinerules/
    action: copy
    source: .agent/rules/
    glob: "*.md"

  hooks:
    target: .clinerules/hooks/
    action: copy
    source: .agent/hooks/
    mapping:
      session_start.py: TaskStart
      session_end.py: TaskCancel
      pre_tool_use.py: PreToolUse
```

**Step 4: Create copilot adapter**

```yaml
# .agent/adapters/copilot/config.yml
name: copilot-cli
description: GitHub Copilot CLI adapter

capabilities:
  hooks: [sessionStart, sessionEnd, preToolUse, postToolUse]
  mcp: true
  skills: false
  rules: true

sync:
  agents_md:
    target: AGENTS.md
    action: none

  rules:
    target: .github/copilot-instructions.md
    action: concatenate
    source: .agent/rules/
    glob: "*.md"

  hooks:
    target: .github/hooks/
    action: copy
    source: .agent/hooks/
    mapping:
      session_start.py: sessionStart
      session_end.py: sessionEnd
      pre_tool_use.py: preToolUse
```

**Step 5: Commit**

```bash
git add .agent/adapters/
git commit -m "feat: add adapter configs for claude, codex, cline, copilot"
```

---

### Task 4: Create shared rules

**Files:**
- Create: `.agent/rules/conventions.md`
- Create: `.agent/rules/security.md`

**Step 1: Create conventions rule**

```markdown
# Coding Conventions

- C#: file-scoped namespaces, sealed records for messages, init-only properties, PascalCase
- JavaScript: ES modules, camelCase, Node 20+
- Python: ruff for linting/formatting, snake_case
- Conventional commit messages (feat:, fix:, docs:, refactor:, test:)
- Branch naming: feat/, fix/, bugfix/, hotfix/, setup/, pr-N
- Run pre-commit hooks before committing
```

**Step 2: Create security rule**

```markdown
# Security Rules

- Never commit secrets, API keys, passwords, or tokens
- Use environment variables for sensitive configuration
- Validate all external input at system boundaries
- Follow OWASP top 10 guidelines
- Use parameterized queries for database operations
```

**Step 3: Commit**

```bash
git add .agent/rules/
git commit -m "feat: add shared agent rules for conventions and security"
```

---

### Task 5: Create shared hook scripts

**Files:**
- Create: `.agent/hooks/session_start.py`
- Create: `.agent/hooks/session_end.py`
- Create: `.agent/hooks/pre_tool_use.py`

**Step 1: Create session_start.py**

Minimal hook that reads stdin JSON (tool-specific format), prints context to stdout. Placeholder for Supermemory integration later.

```python
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
```

**Step 2: Create session_end.py**

```python
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
```

**Step 3: Create pre_tool_use.py**

```python
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
```

**Step 4: Make executable**

Run: `chmod +x .agent/hooks/*.py`

**Step 5: Lint**

Run: `ruff check .agent/hooks/ && ruff format .agent/hooks/`
Expected: no errors

**Step 6: Commit**

```bash
git add .agent/hooks/
git commit -m "feat: add shared Python hook scripts for session lifecycle"
```

---

### Task 6: Write the sync engine test

**Files:**
- Create: `.agent/tools/sync_test.py`

**Step 1: Write failing tests**

```python
#!/usr/bin/env python3
"""Tests for the agent sync engine."""

import json
import os
import shutil
import tempfile
from pathlib import Path
from unittest import TestCase, main

# Tests will import from sync module once it exists
# For now, define expected behavior


class TestLoadAdapterConfig(TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.agent_dir = self.tmp / ".agent"
        self.agent_dir.mkdir()
        (self.agent_dir / "adapters" / "claude").mkdir(parents=True)
        (self.agent_dir / "rules").mkdir()
        (self.agent_dir / "hooks").mkdir()

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_load_adapter_config(self):
        from sync import load_adapter_config

        config_path = self.agent_dir / "adapters" / "claude" / "config.yml"
        config_path.write_text(
            "name: claude-code\ndescription: test\nsync:\n  agents_md:\n    action: none\n"
        )
        cfg = load_adapter_config(config_path)
        assert cfg["name"] == "claude-code"

    def test_load_nonexistent_config_raises(self):
        from sync import load_adapter_config

        with self.assertRaises(FileNotFoundError):
            load_adapter_config(self.tmp / "nonexistent.yml")


class TestPointerAction(TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_creates_pointer_file(self):
        from sync import execute_pointer

        target = self.tmp / "CLAUDE.md"
        execute_pointer(target, "See AGENTS.md")
        assert target.read_text() == "See AGENTS.md"

    def test_overwrites_existing_pointer(self):
        from sync import execute_pointer

        target = self.tmp / "CLAUDE.md"
        target.write_text("old content")
        execute_pointer(target, "new content")
        assert target.read_text() == "new content"


class TestCopyAction(TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.source = self.tmp / "source"
        self.source.mkdir()
        (self.source / "conventions.md").write_text("# Conventions")
        (self.source / "security.md").write_text("# Security")
        (self.source / "ignore.txt").write_text("should not copy")

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_copies_matching_glob(self):
        from sync import execute_copy

        target = self.tmp / "target"
        execute_copy(self.source, target, "*.md")
        assert (target / "conventions.md").read_text() == "# Conventions"
        assert (target / "security.md").read_text() == "# Security"
        assert not (target / "ignore.txt").exists()


class TestMergeAction(TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_merge_hooks_into_claude_settings(self):
        from sync import execute_merge_claude_hooks

        settings_path = self.tmp / "settings.json"
        settings_path.write_text(json.dumps({"enabledPlugins": {}}))
        hooks_dir = self.tmp / "hooks"
        hooks_dir.mkdir()
        (hooks_dir / "session_start.py").write_text("# hook")
        mapping = {"session_start.py": "SessionStart"}
        execute_merge_claude_hooks(settings_path, hooks_dir, mapping)
        result = json.loads(settings_path.read_text())
        assert "hooks" in result

    def test_merge_preserves_existing_settings(self):
        from sync import execute_merge_claude_hooks

        settings_path = self.tmp / "settings.json"
        settings_path.write_text(
            json.dumps({"enabledPlugins": {"foo": True}, "customKey": 42})
        )
        hooks_dir = self.tmp / "hooks"
        hooks_dir.mkdir()
        (hooks_dir / "session_start.py").write_text("# hook")
        mapping = {"session_start.py": "SessionStart"}
        execute_merge_claude_hooks(settings_path, hooks_dir, mapping)
        result = json.loads(settings_path.read_text())
        assert result["customKey"] == 42
        assert result["enabledPlugins"]["foo"] is True


class TestDiscoverAdapters(TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.adapters_dir = self.tmp / "adapters"
        (self.adapters_dir / "claude").mkdir(parents=True)
        (self.adapters_dir / "codex").mkdir(parents=True)
        (self.adapters_dir / "claude" / "config.yml").write_text("name: claude-code\n")
        (self.adapters_dir / "codex" / "config.yml").write_text("name: codex-cli\n")

    def tearDown(self):
        shutil.rmtree(self.tmp)

    def test_discovers_all_adapters(self):
        from sync import discover_adapters

        adapters = discover_adapters(self.adapters_dir)
        names = sorted(a.name for a in adapters)
        assert names == ["claude", "codex"]


if __name__ == "__main__":
    main()
```

**Step 2: Run test to verify it fails**

Run: `cd .agent/tools && python -m pytest sync_test.py -v`
Expected: FAIL with `ModuleNotFoundError: No module named 'sync'`

**Step 3: Commit**

```bash
git add .agent/tools/sync_test.py
git commit -m "test: add sync engine tests (red phase)"
```

---

### Task 7: Write the sync engine

**Files:**
- Create: `.agent/tools/sync.py`

**Step 1: Implement sync.py**

```python
#!/usr/bin/env python3
"""Agent sync engine - distributes .agent/ config to each CLI/IDE tool."""

from __future__ import annotations

import argparse
import fnmatch
import json
import shutil
import sys
from pathlib import Path

import yaml


def load_adapter_config(config_path: Path) -> dict:
    if not config_path.exists():
        raise FileNotFoundError(f"Adapter config not found: {config_path}")
    return yaml.safe_load(config_path.read_text())


def discover_adapters(adapters_dir: Path) -> list[Path]:
    if not adapters_dir.exists():
        return []
    return sorted(
        d for d in adapters_dir.iterdir()
        if d.is_dir() and (d / "config.yml").exists()
    )


def execute_pointer(target: Path, content: str) -> str:
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(content)
    return f"  pointer: {target}"


def execute_copy(source: Path, target: Path, glob: str) -> list[str]:
    target.mkdir(parents=True, exist_ok=True)
    actions = []
    for src_file in source.iterdir():
        if src_file.is_file() and fnmatch.fnmatch(src_file.name, glob):
            dst = target / src_file.name
            shutil.copy2(src_file, dst)
            actions.append(f"  copy: {src_file} -> {dst}")
    return actions


def execute_copy_file(source: Path, target: Path) -> str:
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, target)
    return f"  copy: {source} -> {target}"


def execute_merge_claude_hooks(
    settings_path: Path,
    hooks_dir: Path,
    mapping: dict[str, str],
) -> str:
    if settings_path.exists():
        settings = json.loads(settings_path.read_text())
    else:
        settings = {}

    hooks: dict[str, list] = settings.get("hooks", {})
    for script_name, event_name in mapping.items():
        script_path = hooks_dir / script_name
        if not script_path.exists():
            continue
        abs_path = str(script_path.resolve())
        hook_entry = {
            "type": "command",
            "command": f'python "{abs_path}"',
        }
        existing = hooks.get(event_name, [])
        # Check if this hook already exists (by command)
        if not any(h.get("command") == hook_entry["command"] for h in existing):
            existing.append(hook_entry)
        hooks[event_name] = existing

    settings["hooks"] = hooks
    settings_path.parent.mkdir(parents=True, exist_ok=True)
    settings_path.write_text(json.dumps(settings, indent=2) + "\n")
    return f"  merge: hooks -> {settings_path}"


def sync_adapter(adapter_dir: Path, project_root: Path) -> list[str]:
    config = load_adapter_config(adapter_dir / "config.yml")
    name = config.get("name", adapter_dir.name)
    actions = [f"Syncing adapter: {name}"]

    sync_entries = config.get("sync", {})
    for key, entry in sync_entries.items():
        action = entry.get("action", "none")

        if action == "none":
            actions.append(f"  skip: {key} (action=none)")
            continue

        if action == "pointer":
            content = entry.get("content", "")
            target = project_root / entry["target"]
            actions.append(execute_pointer(target, content))
            continue

        if action == "copy":
            source_path = entry.get("source", "")
            glob_pattern = entry.get("glob", "*")
            target = project_root / entry["target"]
            source = project_root / source_path

            if source.is_file():
                actions.append(execute_copy_file(source, target))
            elif source.is_dir():
                actions.extend(execute_copy(source, target, glob_pattern))
            else:
                actions.append(f"  warn: source not found: {source}")
            continue

        if action == "merge":
            fmt = entry.get("format", "")
            if fmt == "claude-settings-json":
                target = project_root / entry["target"]
                hooks_dir = project_root / ".agent" / "hooks"
                mapping = entry.get("mapping", {})
                actions.append(
                    execute_merge_claude_hooks(target, hooks_dir, mapping)
                )
            else:
                actions.append(f"  warn: unknown merge format: {fmt}")
            continue

        if action == "concatenate":
            source = project_root / entry.get("source", "")
            target = project_root / entry["target"]
            glob_pattern = entry.get("glob", "*")
            target.parent.mkdir(parents=True, exist_ok=True)
            parts = []
            if source.is_dir():
                for f in sorted(source.iterdir()):
                    if f.is_file() and fnmatch.fnmatch(f.name, glob_pattern):
                        parts.append(f.read_text())
            target.write_text("\n\n".join(parts))
            actions.append(f"  concatenate: {source} -> {target}")
            continue

        actions.append(f"  warn: unknown action: {action}")

    return actions


def main() -> None:
    parser = argparse.ArgumentParser(description="Sync agent config to CLI/IDE tools")
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--all", action="store_true", help="Sync all adapters")
    group.add_argument("--adapter", type=str, help="Sync a specific adapter")
    parser.add_argument(
        "--project-root",
        type=Path,
        default=None,
        help="Project root (default: auto-detect from .agent/ location)",
    )
    args = parser.parse_args()

    if args.project_root:
        project_root = args.project_root.resolve()
    else:
        # Walk up from script location to find .agent/
        current = Path(__file__).resolve().parent
        while current != current.parent:
            if (current / ".agent").is_dir():
                project_root = current
                break
            current = current.parent
        else:
            print("Error: could not find .agent/ directory", file=sys.stderr)
            sys.exit(1)

    adapters_dir = project_root / ".agent" / "adapters"

    if args.all:
        adapter_dirs = discover_adapters(adapters_dir)
        if not adapter_dirs:
            print("No adapters found.")
            return
        for adapter_dir in adapter_dirs:
            for line in sync_adapter(adapter_dir, project_root):
                print(line)
            print()
    else:
        adapter_dir = adapters_dir / args.adapter
        if not (adapter_dir / "config.yml").exists():
            print(f"Error: adapter '{args.adapter}' not found", file=sys.stderr)
            sys.exit(1)
        for line in sync_adapter(adapter_dir, project_root):
            print(line)


if __name__ == "__main__":
    main()
```

**Step 2: Run tests**

Run: `cd .agent/tools && python -m pytest sync_test.py -v`
Expected: all tests PASS

**Step 3: Lint**

Run: `ruff check .agent/tools/ && ruff format .agent/tools/`
Expected: no errors

**Step 4: Commit**

```bash
git add .agent/tools/sync.py
git commit -m "feat: implement agent sync engine"
```

---

### Task 8: Add Taskfile sync commands

**Files:**
- Modify: `Taskfile.yml`

**Step 1: Add agent sync tasks**

Append after the existing `skills:new` task:

```yaml
  # Agent sync tasks
  agent:sync:
    desc: Sync agent skills/rules/hooks to all configured tools
    cmds:
      - python .agent/tools/sync.py --all

  agent:sync:claude:
    desc: Sync agent config to Claude Code
    cmds:
      - python .agent/tools/sync.py --adapter claude

  agent:sync:codex:
    desc: Sync agent config to Codex CLI
    cmds:
      - python .agent/tools/sync.py --adapter codex

  agent:sync:cline:
    desc: Sync agent config to Cline CLI
    cmds:
      - python .agent/tools/sync.py --adapter cline

  agent:sync:copilot:
    desc: Sync agent config to Copilot CLI
    cmds:
      - python .agent/tools/sync.py --adapter copilot
```

**Step 2: Verify**

Run: `task --list | grep agent`
Expected: all 5 agent sync tasks listed

**Step 3: Commit**

```bash
git add Taskfile.yml
git commit -m "feat: add agent:sync Taskfile commands"
```

---

### Task 9: Create memory skill (Supermemory for decisions only)

**Files:**
- Create: `.agent/skills/memory/SKILL.md`
- Create: `.agent/skills/memory/scripts/save.py`
- Create: `.agent/skills/memory/scripts/search.py`

**Step 1: Create SKILL.md**

```markdown
---
name: memory
description: Save and search cross-session memory for architectural decisions, debugging insights, and user preferences. Uses Supermemory API. NOT for codebase indexing (use AGENTS.md for that).
---

# Memory Skill

Save and search cross-session memory. Use this for:
- Architectural decisions and rationale
- Debugging insights and solutions
- User preferences and workflow patterns
- Cross-session continuity

Do NOT use for codebase structure or conventions (those belong in AGENTS.md).

## Save

```bash
python .agent/skills/memory/scripts/save.py "description of what to remember"
```

## Search

```bash
python .agent/skills/memory/scripts/search.py "query about past decisions"
```
```

**Step 2: Create save.py**

```python
#!/usr/bin/env python3
"""Save a memory to Supermemory for cross-session persistence."""

from __future__ import annotations

import json
import sys
import urllib.request

API_URL = "https://api.supermemory.ai/v3/memories"


def get_api_key() -> str | None:
    """Read API key from project or global config."""
    from pathlib import Path

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
        print("Usage: save.py <memory content>", file=sys.stderr)
        sys.exit(1)

    content = " ".join(sys.argv[1:])
    api_key = get_api_key()
    if not api_key:
        print("Error: no Supermemory API key found", file=sys.stderr)
        sys.exit(1)

    req = urllib.request.Request(
        API_URL,
        data=json.dumps({"content": content}).encode(),
        headers={
            "Authorization": f"Bearer {api_key}",
            "Content-Type": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(req) as resp:
        result = json.loads(resp.read())
        print(f"Saved memory: {result.get('id', 'ok')}")


if __name__ == "__main__":
    main()
```

**Step 3: Create search.py**

```python
#!/usr/bin/env python3
"""Search Supermemory for cross-session memories."""

from __future__ import annotations

import json
import sys
import urllib.request
import urllib.parse


API_URL = "https://api.supermemory.ai/v3/search"


def get_api_key() -> str | None:
    """Read API key from project or global config."""
    from pathlib import Path

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
```

**Step 4: Make executable and lint**

Run: `chmod +x .agent/skills/memory/scripts/*.py && ruff check .agent/skills/memory/ && ruff format .agent/skills/memory/`

**Step 5: Commit**

```bash
git add .agent/skills/memory/
git commit -m "feat: add memory skill for cross-session Supermemory access"
```

---

### Task 10: End-to-end sync test

**Step 1: Run sync for claude adapter**

Run: `task agent:sync:claude`
Expected output includes:
- `Syncing adapter: claude-code`
- `pointer: .../CLAUDE.md`
- hook merge actions

**Step 2: Verify CLAUDE.md was created/updated**

Run: `cat CLAUDE.md`
Expected: pointer content

**Step 3: Run sync for all adapters**

Run: `task agent:sync`
Expected: all 4 adapters sync without errors

**Step 4: Run full test suite**

Run: `cd .agent/tools && python -m pytest sync_test.py -v`
Expected: all tests PASS

**Step 5: Lint everything**

Run: `ruff check .agent/ && ruff format .agent/`
Expected: no errors

**Step 6: Final commit**

```bash
git add -A
git commit -m "feat: unified agent skills architecture with adapter sync system"
```

---

### Task 11: Update AGENTS.md to document the new system

**Files:**
- Modify: `AGENTS.md`

**Step 1: Add agent adapters section**

Add section documenting the adapter system, available sync commands, and how to add new adapters.

**Step 2: Update skills table**

Add memory skill to the skills table.

**Step 3: Update project structure**

Reflect the new `.agent/` directory layout including adapters, hooks, rules, tools.

**Step 4: Commit**

```bash
git add AGENTS.md
git commit -m "docs: update AGENTS.md with adapter system documentation"
```
