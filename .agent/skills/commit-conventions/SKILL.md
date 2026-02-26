---
name: commit-conventions
description: Follow conventional commit messages, branch naming, and pre-commit workflows
tags: [commit, git, convention, message, changelog]
roles: [builder, reviewer]
scope: project
---

# Commit Conventions

Follow standardized commit message formats, branch naming patterns, and always run pre-commit hooks.

## Commit Message Format

Use conventional commit format:

- `feat:` - New feature
- `fix:` - Bug fix
- `docs:` - Documentation only
- `refactor:` - Code change that neither fixes a bug nor adds a feature
- `test:` - Adding or updating tests

Example: `feat: add retry logic to API client`

## Branch Naming

Use prefixed branch names:

- `feat/` - Feature branches
- `fix/` or `bugfix/` - Bug fix branches
- `hotfix/` - Urgent production fixes
- `setup/` - Infrastructure or tooling setup
- `pr-N` - Pull request branches (where N is the PR number)

Example: `feat/add-error-envelope`

## Pre-Commit Hooks

Always run pre-commit hooks before committing:

```bash
git commit
```

Hooks will automatically run. Do not bypass with `--no-verify` unless absolutely necessary.

## Attribution

Credit swarm and gatekeeper agents in commit messages when they contributed to the work.

Example: `feat: implement caching layer (swarm-assisted)`
