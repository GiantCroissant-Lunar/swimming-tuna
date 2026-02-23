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
