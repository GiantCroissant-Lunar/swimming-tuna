# RFC-003: Sandbox Spectrum

**Status:** Draft
**Date:** 2026-02-24
**Dependencies:** None
**Implements:** Multi-agent evolution Phase A

## Problem

The current `SandboxMode` is binary: `host` (no isolation) or `docker`/`apple-container`
(full container). But many CLI agents rely on OAuth flows, local credential stores, or
system keychains that don't work inside containers. We need a spectrum of isolation levels
that balances security with practical agent requirements.

## Proposal

Define three sandbox levels. Each agent declares which level it requires; the container
lifecycle manager enforces the appropriate protections.

### Sandbox Levels

```
Level 0: CLI Bare
├── Agent runs as a local process (no container)
├── Protection: workspace branch isolation, file system scoping
├── Use case: OAuth-based CLI agents (Copilot, Cline with auth)
├── Risk: agent has access to host resources
└── Mitigation: read-only mounts for non-workspace paths,
    workspace branch prevents main branch pollution

Level 1: CLI Sandboxed
├── Agent runs as a local process with OS-level restrictions
├── Protection: Level 0 + process sandboxing
│   ├── macOS: sandbox-exec profile or App Sandbox entitlements
│   ├── Linux: seccomp-bpf, namespaces, read-only bind mounts
│   └── Deny: network access to non-allowlisted hosts,
│            file writes outside workspace, process spawning limits
├── Use case: agents that need local credentials but should be restricted
└── Risk: sandbox escape (lower than Level 0)

Level 2: Container
├── Agent runs in Docker or Apple Container
├── Protection: full OS isolation
│   ├── Dedicated filesystem (workspace mounted read-write, rest read-only)
│   ├── Network policy (agent-to-agent A2A allowed, internet configurable)
│   ├── Resource limits (CPU, memory, timeout)
│   └── No access to host credentials or keychain
├── Use case: API-key agents, untrusted adapters
└── Risk: minimal (container escape is the threat model)
```

### Agent Sandbox Declaration

Each agent declares its required sandbox level in its agent card:

```json
{
  "agentId": "builder-01",
  "sandboxLevel": 0,
  "sandboxRequirements": {
    "needsOAuth": true,
    "needsKeychain": false,
    "needsNetwork": ["api.github.com", "copilot-proxy.githubusercontent.com"],
    "needsGpuAccess": false
  }
}
```

### Runtime Enforcement

The container lifecycle manager (RFC-004 agent registry handles discovery;
this RFC handles execution environment):

1. Agent requests spawn with declared sandbox level
2. Runtime checks policy: can this sandbox level run on this host?
3. Runtime provisions the environment:
   - Level 0: spawn process, set workspace branch, scope file access
   - Level 1: spawn process with sandbox profile applied
   - Level 2: build/pull container image, mount workspace, apply network policy
4. Agent starts, registers A2A endpoint
5. On shutdown: cleanup environment, collect artifacts

### Existing Code Mapping

| Current Code | Maps To |
|-------------|---------|
| `SandboxMode = "host"` | Level 0 |
| `SandboxMode = "docker"` | Level 2 |
| `SandboxMode = "apple-container"` | Level 2 |
| `DockerSandboxWrapper` | Level 2 configuration |
| `AppleContainerSandboxWrapper` | Level 2 configuration |
| `WorkspaceBranchEnabled` | Level 0 file isolation (already implemented) |

Level 1 is new and needs implementation.

## Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Levels | 3 discrete levels | Clear boundaries; avoids complex per-permission matrix |
| Level 1 mechanism | OS-native sandboxing | No container overhead; works with OAuth flows |
| Default level | Level 0 for CLI, Level 2 for API | CLI agents often need OAuth; API agents are self-contained |
| Policy enforcement | Runtime-side, not agent-side | Agents shouldn't control their own restrictions |

## Out of Scope

- Container image management (which base images, pre-installed tools)
- Network policy DSL (specific allowlist format)
- GPU passthrough for ML agents

## Open Questions

- Should Level 1 on macOS use `sandbox-exec` (deprecated but functional) or App Sandbox?
- How to handle credential rotation for Level 0 agents (OAuth token refresh)?
- Should the dashboard show sandbox level per agent as a security indicator?
