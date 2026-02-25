# RFC-009: Agent Skills & Structural Knowledge

**Status:** Draft
**Date:** 2026-02-25
**Dependencies:** RFC-004 (Agent Registry), RFC-006 (Langfuse Replay)
**Implements:** Multi-agent evolution Phase D

## Problem

Today, SwarmAssistant agents execute roles with hardcoded prompt templates
(`RolePromptFactory.cs`). Agents have no reusable skills, no structural knowledge
beyond what fits in the prompt, and no way to learn from past executions. The RFC-004
dogfooding cycle showed that the reviewer agent approved spec-vs-code mismatches
because it lacked a "spec consistency" skill.

Meanwhile, reference implementations (bosun, pi-agent) demonstrate that lightweight
file-based skills with tag matching dramatically improve agent output quality.

## Proposal

Add three layers to the agent knowledge architecture:

```
Layer 1: Skills         — reusable markdown instructions, matched by tags
Layer 2: Structural Knowledge — indexed codebase/docs via qmd hybrid search
Layer 3: Shared Knowledge     — append-only cross-agent learning (blackboard evolution)
```

### Layer 1: Agent Skills

Skills are markdown files with YAML frontmatter, stored in `.agent/skills/` or a
runtime-configurable directory. Each skill is matched to a task+role by tag similarity.

```
.agent/skills/
  spec-consistency/
    SKILL.md          # Frontmatter: name, tags, roles, description
  commit-conventions/
    SKILL.md
  tdd-pattern/
    SKILL.md
  error-recovery/
    SKILL.md
  security-review/
    SKILL.md
```

#### Skill Format

```yaml
---
name: spec-consistency
description: Cross-validate OpenAPI specs, JSON schemas, and DTO signatures
tags: [openapi, schema, dto, nullability, spec, api, validation]
roles: [reviewer, builder]
scope: global
---

## Spec Consistency Checklist

When modifying OpenAPI specs or JSON schemas:
1. Verify field types match DTO property types (nullable `?` vs non-nullable)
2. Ensure `required` arrays include all non-nullable DTO properties
3. Confirm enum values in spec match code constants
4. Check generated models need regeneration (`task models:verify`)
...
```

#### Skill Discovery & Matching

```
Task submitted: "Add GET /a2a/registry/agents endpoint"
Role: reviewer

1. Tokenize task title + description → ["api", "endpoint", "registry", "agents"]
2. Match against skill tags (overlap scoring)
3. Filter by role: keep skills where `roles` includes "reviewer"
4. Rank by relevance score
5. Inject top-N skills (budget: 4000 chars) into prompt as Layer 5

Result: spec-consistency (tags: openapi, api, validation → 3 matches)
        security-review (tags: api, endpoint → 2 matches)
```

#### Injection into RolePromptFactory

Add a 5th context layer:

```
BuildPrompt():
  Layer 1: Base role prompt (existing)
  Layer 2: Project context / AGENTS.md (existing)
  Layer 3: Historical insights from StrategyAdvisor (existing)
  Layer 4: Code context from CodeIndex (existing)
  Layer 5: Matched skills (NEW)
```

```csharp
// New in RolePromptFactory
private static string BuildSkillContext(IReadOnlyList<MatchedSkill> skills)
{
    // "--- Agent Skills ---"
    // For each skill: "### {name}\n{content}"
    // "--- End Agent Skills ---"
    // "Apply these skills to your review/implementation."
}
```

### Layer 2: Structural Knowledge via qmd

[qmd](https://github.com/tobi/qmd) is an on-device hybrid search engine that indexes
markdown documents with FTS5 + vector embeddings + LLM reranking. It runs locally with
no cloud dependencies.

#### Why qmd

| Need | Current approach | qmd approach |
|------|-----------------|--------------|
| Find relevant code patterns | CodeIndexActor (AST chunks, 40KB budget) | Hybrid search across docs + code + skills |
| Search AGENTS.md | Full file injection | Semantic query returns relevant sections |
| Cross-reference RFCs | Manual | `qmd search "sandbox level isolation"` |
| Skill matching | Tag overlap (simple) | Semantic similarity (deeper) |

#### Integration

```
qmd collection add project \
  --path docs/ \
  --path .agent/skills/ \
  --path AGENTS.md \
  --glob "*.md" \
  --description "SwarmAssistant project knowledge"

qmd collection add codebase \
  --path project/dotnet/src/ \
  --glob "*.cs" \
  --description "Runtime source code"
```

Before role execution, query qmd for context:

```
qmd search "agent registry heartbeat eviction" --collection project --limit 5 --format json
```

Returns ranked chunks with scores. Inject as an additional context source alongside
CodeIndexActor results.

#### qmd vs CodeIndexActor

They serve different purposes:

- **CodeIndexActor**: AST-aware, understands code structure (classes, methods, namespaces).
  Best for "find the implementation of X".
- **qmd**: Document-aware, understands prose + code. Best for "what does the project say
  about X" and "find skills related to X".

Both feed into the prompt. CodeIndexActor for code chunks, qmd for docs/skills/knowledge.

### Layer 3: Shared Knowledge (Blackboard Evolution)

Evolve the current key-value blackboard into a structured knowledge store, inspired by
bosun's shared-knowledge pattern.

#### Current Blackboard

```csharp
// Key-value pairs, no structure
GlobalBlackboard.Set("agent.joined.builder-01", "2026-02-25T10:00:00Z");
```

#### Proposed Knowledge Entries

```csharp
public sealed record KnowledgeEntry
{
    public required string Content { get; init; }          // "OpenAPI nullable types must match DTO"
    public required string Category { get; init; }         // pattern | gotcha | convention | tip
    public required string ContributorAgentId { get; init; }
    public required string Scope { get; init; }            // global | task:{taskId}
    public string? TaskRef { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string ContentHash { get; init; }               // SHA-256 for dedup
}
```

#### How Agents Contribute Knowledge

After a reviewer rejects work with a specific reason, the reviewer agent can emit a
knowledge entry:

```
KnowledgeEntry:
  content: "When adding OpenAPI endpoints, always verify nullable fields match DTO signatures"
  category: gotcha
  contributorAgentId: reviewer-01
  scope: global
```

Before a builder starts work, query knowledge entries matching the task keywords.
Inject as additional context alongside historical insights.

#### Deduplication

SHA-256 hash of `content + scope`. Duplicates silently dropped. Rate limit: 1 entry
per agent per 30 seconds (prevents flooding).

## CLI Mode & Phase Awareness

Modern CLI tools support different execution modes that map naturally to SwarmRole:

| SwarmRole | Copilot flag | Cline mode | Aider mode | Claude Code |
|-----------|-------------|------------|------------|-------------|
| Planner | (default) | plan | architect | (default) |
| Builder | (default) | act | code | (default) |
| Reviewer | (default) | plan | architect | (default) |
| Debugger | (default) | act | code | (default) |
| Researcher | (default) | plan | architect | (default) |

Some CLIs also support `--model` for explicit model selection:

```
copilot --model claude-sonnet-4-6 --prompt "..."
cline --model gpt-4o --oneshot "..."
```

The adapter template system should pass mode and model hints when available.
See RFC-010 for the full provider abstraction.

## Implementation Tasks

1. Skill file format parser (YAML frontmatter + markdown body)
2. Skill index builder (scan directory, build tag index)
3. Skill matcher (task description + role → ranked skills)
4. RolePromptFactory Layer 5 integration
5. qmd collection setup for project + codebase
6. qmd query integration in prompt pipeline
7. KnowledgeEntry model + blackboard evolution
8. Knowledge contribution from reviewer agent
9. Knowledge query before role execution
10. Seed initial skills from gatekeeper guide patterns

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Skill format | Markdown + YAML frontmatter | Same as pi-agent and bosun; low barrier |
| Skill matching | Tag overlap + optional qmd semantic | Fast default, deep when qmd available |
| Knowledge storage | In-memory blackboard (phase 1), qmd-indexed (phase 2) | Incremental complexity |
| qmd integration | Optional dependency | Not all environments have qmd installed |
| Skill budget in prompt | 4000 chars max | Preserve room for code context |

## Out of Scope

- Automatic skill generation from past traces (future: use Langfuse feedback)
- Skill versioning or A/B testing
- Remote skill repositories

## Open Questions

- Should skills be version-controlled alongside the codebase or managed separately?
- How to prevent skill bloat (too many low-quality skills diluting the prompt)?
- Should qmd index be rebuilt on every run or maintained incrementally?
