---
name: security-review
description: Review code for security vulnerabilities, input validation, and OWASP compliance
tags: [security, auth, input, validation, injection, owasp]
roles: [reviewer, builder]
scope: project
---

# Security Review

Review code for common security vulnerabilities and ensure compliance with security best practices.

## Never Commit Secrets

- No API keys, passwords, tokens, or credentials in source code
- Use environment variables for sensitive configuration
- Check `.gitignore` covers secrets files
- Scan for accidentally committed secrets before PR

## Input Validation

Validate all external input at system boundaries:

- HTTP request bodies and query parameters
- File uploads (check type, size, content)
- CLI arguments and user input
- Data from external APIs

Sanitize and validate before using in operations.

## Parameterized Queries

Always use parameterized queries for database operations:

```csharp
// ❌ BAD - SQL injection risk
var query = $"SELECT * FROM Users WHERE Id = {userId}";

// ✅ GOOD - parameterized
var query = "SELECT * FROM Users WHERE Id = @UserId";
```

## OWASP Top 10

Follow OWASP Top 10 guidelines:

1. Broken Access Control - Verify authorization checks
2. Cryptographic Failures - Use strong encryption, secure key storage
3. Injection - Parameterize queries, validate input
4. Insecure Design - Threat modeling, secure defaults
5. Security Misconfiguration - Harden configs, disable debug in prod
6. Vulnerable Components - Keep dependencies updated
7. Authentication Failures - Strong passwords, MFA, session management
8. Data Integrity Failures - Verify data integrity, sign critical data
9. Logging Failures - Log security events, protect log data
10. Server-Side Request Forgery - Validate URLs, restrict network access

## CLI Adapter Security

Check for command injection in CLI adapter calls:

- Never pass unsanitized user input directly to shell commands
- Use argument arrays instead of shell string concatenation
- Validate and whitelist allowed commands and arguments
