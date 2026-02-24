# ui-ux-pro-max with A2UI/GenUI

This document describes how to use the `ui-ux-pro-max` skill as a **design-system generator** while keeping SwarmAssistant runtime logic and protocols stable.

## Scope

- Runtime/UI protocol: **A2UI** payloads (serialized from `GenUiComponent`) and rendered by Godot `GenUiNodeFactory`.
- Skill: `ui-ux-pro-max` (https://github.com/nextlevelbuilder/ui-ux-pro-max-skill)

The goal is:

- Use `ui-ux-pro-max` to generate a consistent design system for your surfaces.
- Encode the resulting design choices into A2UI/GenUI **props** (e.g. `font_color`, `font_size`, `bg_color`) without introducing runtime coupling to the skill.

## Installing the skill (local developer workflow)

Install via the upstream CLI (recommended by the skill project):

```bash
npm install -g uipro-cli
uipro init --ai claude
```

This installs the skill assets under `.claude/skills/ui-ux-pro-max/` (path may vary by assistant).

## Generate a design system

Generate a project design system and persist it:

```bash
python3 .claude/skills/ui-ux-pro-max/scripts/search.py "SwarmAssistant operator dashboard" --design-system --persist -p "SwarmAssistant"
```

This produces:

```text
design-system/
├── MASTER.md
└── pages/
    └── <page>.md
```

## Apply design system choices to A2UI/GenUI

### A. Use semantic tokens and map them to props

Recommended prop keys (supported by the Godot renderer):

- `font_color`: string (e.g. `#E6E6E6`)
- `font_size`: int (e.g. `16`)
- `bg_color`: string (e.g. `#121826`) for `panel` components

### B. Example component JSON

```json
{
  "id": "header-title",
  "type": "text",
  "props": {
    "text": "SwarmAssistant Operator Control Surface",
    "font_size": 18,
    "font_color": "#C7D2FE"
  }
}
```

```json
{
  "id": "info-panel",
  "type": "panel",
  "props": {
    "bg_color": "#0B1220"
  },
  "children": [
    {
      "id": "info-text",
      "type": "rich_text",
      "props": {
        "text": "[b]Connected[/b] to runtime",
        "font_color": "#E5E7EB",
        "font_size": 14
      }
    }
  ]
}
```

### C. C# helper usage

`GenUiComponent.Text(...)` and `GenUiComponent.RichText(...)` accept optional style values which serialize to the same prop keys.

## Notes / guardrails

- Keep the A2UI protocol as the stable contract. Skills should only influence the payload *content*, not require runtime changes.
- Prefer design-system files (`design-system/MASTER.md`, `design-system/pages/*`) as the authoritative style spec.
- Avoid putting skill-specific instructions inside the runtime’s prompt templates unless you intentionally want that coupling.
