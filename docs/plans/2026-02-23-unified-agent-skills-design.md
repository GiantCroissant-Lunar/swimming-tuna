# Unified Agent Skills Architecture

Date: 2026-02-23

## Problem

AI coding CLIs (Claude Code, Codex, Cline, Copilot, Droid, Cursor, etc.) each have their own locations for skills, rules, hooks, and agent guidance files. Maintaining separate configs per tool is fragile and duplicative. We need a single source of truth under `.agent/` that syncs to each tool's expected locations.

Additionally, codebase knowledge should live in project files (not Supermemory), and CLAUDE.md should be a pointer to AGENTS.md so we maintain one file.

## Decisions

- **AGENTS.md** is the single source of truth for codebase index, architecture, conventions, and agent guidance.
- **CLAUDE.md** is a pointer file that references AGENTS.md.
- **Supermemory** is used only for cross-session decision/debugging memory, not codebase indexing.
- **Skills** use the existing SKILL.md format (universal, tool-agnostic).
- **Adapters** are YAML configs per tool, declaring sync targets and capabilities.
- **Hooks** are Python scripts (portable, project already uses ruff/Python tooling).
- **Sync** is driven by a Python script invoked via Taskfile commands.

## Directory Structure

```
.agent/
├── adapters/                    # Per-tool sync configurations
│   ├── claude/
│   │   └── config.yml
│   ├── codex/
│   │   └── config.yml
│   ├── cline/
│   │   └── config.yml
│   └── copilot/
│       └── config.yml
├── hooks/                       # Shared Python hook scripts
│   ├── session_start.py         # Inject context at session start
│   ├── session_end.py           # Save session summary at end
│   └── pre_tool_use.py          # Validate tool calls
├── rules/                       # Shared rules (tool-agnostic markdown)
│   ├── conventions.md
│   └── security.md
├── skills/                      # Shared skills (SKILL.md format)
│   ├── remotion/                # (existing)
│   ├── skill-creator/           # (existing)
│   └── memory/                  # Supermemory for decisions/debugging only
│       ├── SKILL.md
│       └── scripts/
│           ├── save.py
│           └── search.py
└── tools/
    └── sync.py                  # Sync engine called by Taskfile
```

Project root:
- `AGENTS.md` -- single source of truth
- `CLAUDE.md` -- pointer to AGENTS.md

## Adapter Config Format

Each adapter is a directory under `.agent/adapters/<tool>/` containing a `config.yml`.

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
    type: pointer
    content: |
      See AGENTS.md for all project guidance.

  skills:
    target: .agent/skills/
    action: none  # Claude reads from .agent/skills/ directly

  rules:
    target: .claude/rules/
    action: copy
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

### Sync Actions

| Action | Behavior |
|--------|----------|
| `none` | Tool reads from source directly, no sync needed |
| `copy` | Copy files from `.agent/<source>/` to target path |
| `pointer` | Generate a pointer file with specified content |
| `merge` | Merge hook entries into existing tool config file |

## Sync Engine

`.agent/tools/sync.py` reads adapter configs and executes sync actions.

- `python .agent/tools/sync.py --all` syncs all adapters
- `python .agent/tools/sync.py --adapter claude` syncs one adapter
- Idempotent, safe to run repeatedly
- Prints actions taken (created, copied, skipped, merged)

## Taskfile Integration

```yaml
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

## Hook Scripts

Python scripts under `.agent/hooks/` receive tool-specific input via stdin and perform shared logic. Each adapter's hook mapping translates between tool-specific event names and the shared scripts.

## Memory Strategy

- **AGENTS.md**: Codebase index, architecture, conventions (universal, always in context)
- **Supermemory**: Cross-session decisions, debugging insights, user preferences (semantic search, not codebase structure)
- **MEMORY.md**: Deprecated in favor of the above two
