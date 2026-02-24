# Coding Conventions

- C#: file-scoped namespaces, sealed records for messages, init-only properties, PascalCase
- JavaScript: ES modules, camelCase, Node 20+
- Python: ruff for linting/formatting, snake_case
- Conventional commit messages (feat:, fix:, docs:, refactor:, test:)
- Branch naming: feat/, fix/, bugfix/, hotfix/, setup/, pr-N
- Run pre-commit hooks before committing

# Security Rules

- Never commit secrets, API keys, passwords, or tokens
- Use environment variables for sensitive configuration
- Validate all external input at system boundaries
- Follow OWASP top 10 guidelines
- Use parameterized queries for database operations
