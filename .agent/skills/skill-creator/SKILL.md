---
name: skill-creator
description: Guide for creating effective skills. This skill should be used when users want to create a new skill (or update an existing skill) that extends Claude's capabilities with specialized knowledge, workflows, or tool integrations.
tags: [meta, documentation, workflow, guide]
roles: [builder, planner]
scope: global
---

# Skill Creator

This skill provides guidance for creating effective skills.

## About Skills

Skills are modular, self-contained packages that extend Claude's capabilities by providing
specialized knowledge, workflows, and tools. Think of them as "onboarding guides" for specific
domains or tasks—they transform Claude from a general-purpose agent into a specialized agent
equipped with procedural knowledge that no model can fully possess.

### What Skills Provide

1. Specialized workflows - Multi-step procedures for specific domains
2. Tool integrations - Instructions for working with specific file formats or APIs
3. Domain expertise - Company-specific knowledge, schemas, business logic
4. Bundled resources - Scripts, references, and assets for complex and repetitive tasks

## Core Principles

### Concise is Key

The context window is a public good. Skills share the context window with everything else Claude needs: system prompt, conversation history, other Skills' metadata, and the actual user request.

**Default assumption: Claude is already very smart.** Only add context Claude doesn't already have. Challenge each piece of information: "Does Claude really need this explanation?" and "Does this paragraph justify its token cost?"

Prefer concise examples over verbose explanations.

### Set Appropriate Degrees of Freedom

Match the level of specificity to the task's fragility and variability:

**High freedom (text-based instructions)**: Use when multiple approaches are valid, decisions depend on context, or heuristics guide the approach.

**Medium freedom (pseudocode or scripts with parameters)**: Use when a preferred pattern exists, some variation is acceptable, or configuration affects behavior.

**Low freedom (specific scripts, few parameters)**: Use when operations are fragile and error-prone, consistency is critical, or a specific sequence must be followed.

Think of Claude as exploring a path: a narrow bridge with cliffs needs specific guardrails (low freedom), while an open field allows many routes (high freedom).

## Anatomy of a Skill

Every skill consists of a required SKILL.md file and optional bundled resources:

```
skill-name/
├── SKILL.md (required)
│   ├── YAML frontmatter metadata (required)
│   │   ├── name: (required)
│   │   ├── description: (required)
│   │   └── compatibility: (optional, rarely needed)
│   └── Markdown instructions (required)
└── Bundled Resources (optional)
    ├── scripts/ - Executable code (Python/Bash/etc.)
    ├── references/ - Documentation intended to be loaded into context as needed
    └── assets/ - Files used in output (templates, icons, fonts, etc.)
```

### SKILL.md (required)

Every SKILL.md consists of:

- **Frontmatter** (YAML): Contains `name` and `description` fields (required), plus optional fields like `license`, `metadata`, and `compatibility`. Only `name` and `description` are read by Claude to determine when the skill triggers, so be clear and comprehensive about what the skill is and when it should be used.
- **Body** (Markdown): Instructions and guidance for using the skill. Only loaded AFTER the skill triggers (if at all).

### Bundled Resources (optional)

#### Scripts (`scripts/`)

Executable code (Python/Bash/etc.) for tasks that require deterministic reliability or are repeatedly rewritten.

- **When to include**: When the same code is being rewritten repeatedly or deterministic reliability is needed
- **Benefits**: Token efficient, deterministic, may be executed without loading into context

#### References (`references/`)

Documentation and reference material intended to be loaded as needed into context to inform Claude's process and thinking.

- **When to include**: For documentation that Claude should reference while working
- **Examples**: API docs, schemas, policies, detailed workflow guides
- **Benefits**: Keeps SKILL.md lean, loaded only when needed
- **Best practice**: If files are large (>10k words), include grep search patterns in SKILL.md

#### Assets (`assets/`)

Files not intended to be loaded into context, but rather used within the output Claude produces.

- **When to include**: When the skill needs files that will be used in the final output
- **Examples**: Templates, images, icons, boilerplate code, fonts

### What to Not Include in a Skill

A skill should only contain essential files that directly support its functionality. Do NOT create extraneous documentation or auxiliary files, including:

- README.md
- INSTALLATION_GUIDE.md
- QUICK_REFERENCE.md
- CHANGELOG.md
- etc.

## Progressive Disclosure Design Principle

Skills use a three-level loading system to manage context efficiently:

1. **Metadata (name + description)** - Always in context (~100 words)
2. **SKILL.md body** - When skill triggers (<5k words)
3. **Bundled resources** - As needed by Claude

Keep SKILL.md body to the essentials and under 500 lines to minimize context bloat.

## Creating a New Skill

When the user wants to create a skill:

1. **Identify the purpose**: What specialized knowledge or workflow does this skill provide?
2. **Determine structure**: Will it need scripts, references, or assets?
3. **Draft SKILL.md**: Write concise frontmatter and body following the principles above
4. **Create bundled resources**: Add scripts, references, or assets as needed
5. **Review**: Ensure the skill is focused, concise, and follows the degrees of freedom guidelines

## Best Practices

1. **Start with the problem**: What specific task or domain knowledge is needed?
2. **Keep it focused**: One skill should do one thing well
3. **Use examples**: Show, don't just tell
4. **Leverage progressive disclosure**: Put details in references, keep SKILL.md lean
5. **Avoid duplication**: Information should live in SKILL.md OR references, not both
6. **Test the skill**: Verify it works as intended in the target environment
