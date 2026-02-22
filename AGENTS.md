# Agent Guide

This document provides guidance for AI agents working on the swimming-tuna project.

## Skills

This project uses the [skills system](https://github.com/anthropics/skills) to extend agent capabilities with specialized knowledge and workflows.

### Available Skills

Skills are located in `.agent/skills/` directory:

| Skill | Description | When to Use |
|-------|-------------|-------------|
| [skill-creator](.agent/skills/skill-creator/) | Guide for creating effective skills | When creating or updating project-specific skills |
| [remotion](.agent/skills/remotion/) | Best practices for Remotion video creation | When working with Remotion for video/animation |

### Using Skills

When working on tasks, check if a relevant skill exists in `.agent/skills/` and load it for specialized guidance.

### Adding New Skills

To add a new skill to this project:

1. Create a new directory under `.agent/skills/<skill-name>/`
2. Add a `SKILL.md` file with YAML frontmatter:
   ```yaml
   ---
   name: skill-name
   description: Clear description of what this skill provides and when to use it
   ---
   ```
3. Add optional bundled resources:
   - `scripts/` - Executable code
   - `references/` - Documentation references
   - `assets/` - Templates and files for output

See the [skill-creator](.agent/skills/skill-creator/SKILL.md) skill for detailed guidance on creating effective skills.

## Project Structure

```
.
├── .agent/skills/      # Agent skills for specialized tasks
├── docs/               # Documentation
├── project/            # Main project code
│   ├── dotnet/         # .NET runtime components
│   └── godot-ui/       # Godot UI components
├── build/              # Build outputs
├── Taskfile.yml        # Task runner configuration
├── repomix.config.json # Repomix configuration
└── AGENTS.md           # This file
```

## Common Tasks

```bash
# Initialize development environment
task init

# Build the project
task build

# Run tests
task test

# Pack repository for AI context
task repomix:pack

# Run linters
task lint
task ruff:check        # Python files

# Format code
task fmt
task ruff:format       # Python files
```

## Development Guidelines

- Follow the existing code style in each component
- Run pre-commit hooks before committing: `pre-commit run --all-files`
- Use conventional commit messages
- Update relevant documentation when making changes
