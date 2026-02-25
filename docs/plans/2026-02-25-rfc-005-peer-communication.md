# RFC-005: Peer Communication — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable bilateral peer-to-peer messaging between agents, with registry-based discovery and a new HTTP endpoint for external callers.

**Architecture:** Sender-side lookup via `AgentRegistryActor._agentIdToRef` + direct Akka `Tell` for internal agents. External callers use `POST /a2a/messages` which routes through the registry. Blackboard signals extended for task coordination. All peer messages traced via OpenTelemetry/Langfuse.

**Tech Stack:** .NET 9, Akka.NET, Minimal API, OpenTelemetry, xUnit + Akka.TestKit

---

### Task 1: PeerMessage Contract Types

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Contracts/Messaging/PeerMessages.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/PeerMessageContractTests.cs`

**Step 1: Write the failing test**

```csharp
// project/dotnet/tests/SwarmAssistant.Runtime.Tests/PeerMessageContractTests.cs
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Tests;

public sealed class PeerMessageContractTests
{
    [Fact]
    public void PeerMessage_RoundTrips_AllProperties()
    {
        var msg = new PeerMessage(
            MessageId: "msg-001",
            FromAgentId: "planner-01",
            ToAgentId: "builder-01",
            Type: PeerMessageType.TaskRequest,
            Payload: """{"taskId":"task-01"}""",
            ReplyTo: "http://localhost:5090/a2a/messages",
            Timestamp: new DateTimeOffset(2026, 2, 25, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("msg-001", msg.MessageId);
        Assert.Equal("planner-01", msg.FromAgentId);
        Assert.Equal("builder-01", msg.ToAgentId);
        Assert.Equal(PeerMessageType.TaskRequest, msg.Type);
        Assert.NotNull(msg.ReplyTo);
        Assert.NotNull(msg.Timestamp);
    }

    [Fact]
    public void PeerMessageAck_Accepted()
    {
        var ack = new PeerMessageAck("msg-001", Accepted: true);
        Assert.True(ack.Accepted);
        Assert.Null(ack.Reason);
    }

    [Fact]
    public void PeerMessageAck_Rejected_WithReason()
    {
        var ack = new PeerMessageAck("msg-001", Accepted: false, Reason: "agent_not_found");
        Assert.False(ack.Accepted);
        Assert.Equal("agent_not_found", ack.Reason);
    }

    [Theory]
    [InlineData(PeerMessageType.TaskRequest)]
    [InlineData(PeerMessageType.TaskResponse)]
    [InlineData(PeerMessageType.HelpRequest)]
    [InlineData(PeerMessageType.HelpResponse)]
    [InlineData(PeerMessageType.Broadcast)]
    public void PeerMessageType_HasExpectedValues(PeerMessageType type)
    {
        Assert.True(Enum.IsDefined(type));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~PeerMessageContractTests" --verbosity quiet`
Expected: FAIL — `PeerMessage` and `PeerMessageType` not defined.

**Step 3: Write minimal implementation**

```csharp
// project/dotnet/src/SwarmAssistant.Contracts/Messaging/PeerMessages.cs
namespace SwarmAssistant.Contracts.Messaging;

public enum PeerMessageType
{
    TaskRequest,
    TaskResponse,
    HelpRequest,
    HelpResponse,
    Broadcast
}

public sealed record PeerMessage(
    string MessageId,
    string FromAgentId,
    string ToAgentId,
    PeerMessageType Type,
    string Payload,
    string? ReplyTo = null,
    DateTimeOffset? Timestamp = null);

public sealed record PeerMessageAck(
    string MessageId,
    bool Accepted,
    string? Reason = null);
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~PeerMessageContractTests" --verbosity quiet`
Expected: PASS (4 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Contracts/Messaging/PeerMessages.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/PeerMessageContractTests.cs
git commit -m "feat(rfc-005): add PeerMessage and PeerMessageAck contract types"
```

---

### Task 2: Blackboard Signal Keys for Peer Coordination

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/GlobalBlackboardKeys.cs`
- Test: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/GlobalBlackboardKeysTests.cs`

**Step 1: Write the failing test**

```csharp
// project/dotnet/tests/SwarmAssistant.Runtime.Tests/GlobalBlackboardKeysTests.cs
using SwarmAssistant.Runtime.Actors;

namespace SwarmAssistant.Runtime.Tests;

public sealed class GlobalBlackboardKeysTests
{
    [Fact]
    public void TaskAvailable_FormatsCorrectly()
    {
        Assert.Equal("task.available:task-01", GlobalBlackboardKeys.TaskAvailable("task-01"));
    }

    [Fact]
    public void TaskClaimed_FormatsCorrectly()
    {
        Assert.Equal("task.claimed:task-01", GlobalBlackboardKeys.TaskClaimed("task-01"));
    }

    [Fact]
    public void TaskComplete_FormatsCorrectly()
    {
        Assert.Equal("task.complete:task-01", GlobalBlackboardKeys.TaskComplete("task-01"));
    }

    [Fact]
    public void ArtifactProduced_FormatsCorrectly()
    {
        Assert.Equal("artifact.produced:task-01", GlobalBlackboardKeys.ArtifactProduced("task-01"));
    }

    [Fact]
    public void HelpNeeded_FormatsCorrectly()
    {
        Assert.Equal("help.needed:agent-01", GlobalBlackboardKeys.HelpNeeded("agent-01"));
    }
}
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~GlobalBlackboardKeysTests" --verbosity quiet`
Expected: FAIL — methods not defined.

**Step 3: Write minimal implementation**

Add to the end of `GlobalBlackboardKeys.cs`, before the closing brace:

```csharp
    // Peer coordination signals (RFC-005)
    internal const string TaskAvailablePrefix    = "task.available:";
    internal const string TaskClaimedPrefix      = "task.claimed:";
    internal const string TaskCompletePrefix     = "task.complete:";
    internal const string ArtifactProducedPrefix = "artifact.produced:";
    internal const string HelpNeededPrefix       = "help.needed:";

    internal static string TaskAvailable(string taskId) => $"{TaskAvailablePrefix}{taskId}";
    internal static string TaskClaimed(string taskId)   => $"{TaskClaimedPrefix}{taskId}";
    internal static string TaskComplete(string taskId)  => $"{TaskCompletePrefix}{taskId}";
    internal static string ArtifactProduced(string taskId) => $"{ArtifactProducedPrefix}{taskId}";
    internal static string HelpNeeded(string agentId)   => $"{HelpNeededPrefix}{agentId}";
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~GlobalBlackboardKeysTests" --verbosity quiet`
Expected: PASS (5 tests)

**Step 5: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/GlobalBlackboardKeys.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/GlobalBlackboardKeysTests.cs
git commit -m "feat(rfc-005): add peer coordination signal keys to GlobalBlackboardKeys"
```

---

### Task 3: ResolvePeerAgent Message in AgentRegistryActor

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/InternalMessages.cs` (add message types)
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs` (add handler)
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryActorTests.cs` (add tests)

**Step 1: Write the failing test**

Add to `AgentRegistryActorTests.cs`:

```csharp
    [Fact]
    public void ResolvePeerAgent_ReturnsRefForRegisteredAgent()
    {
        var (registry, _, _) = CreateRegistry();

        var probe = CreateTestProbe();
        registry.Tell(new AgentCapabilityAdvertisement(
            probe.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), probe);

        registry.Tell(new ResolvePeerAgent("builder-01"));
        var result = ExpectMsg<PeerAgentResolved>();
        Assert.True(result.Found);
        Assert.Equal(probe.Ref, result.AgentRef);
        Assert.Equal("builder-01", result.AgentId);
    }

    [Fact]
    public void ResolvePeerAgent_ReturnsNotFoundForUnknownAgent()
    {
        var (registry, _, _) = CreateRegistry();

        registry.Tell(new ResolvePeerAgent("nonexistent-agent"));
        var result = ExpectMsg<PeerAgentResolved>();
        Assert.False(result.Found);
        Assert.Null(result.AgentRef);
    }
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests.ResolvePeerAgent" --verbosity quiet`
Expected: FAIL — `ResolvePeerAgent` and `PeerAgentResolved` not defined.

**Step 3: Write minimal implementation**

Add to `InternalMessages.cs` (before the closing namespace or at the end):

```csharp
// Peer communication: agent resolution (RFC-005)
internal sealed record ResolvePeerAgent(string AgentId);

internal sealed record PeerAgentResolved(
    string AgentId,
    bool Found,
    IActorRef? AgentRef = null,
    string? EndpointUrl = null);
```

Add to `AgentRegistryActor.cs` constructor, after the `Receive<QueryAgents>(HandleQuery);` line:

```csharp
        Receive<ResolvePeerAgent>(HandleResolvePeerAgent);
```

Add handler method to `AgentRegistryActor.cs` (after `HandleQuery`):

```csharp
    private void HandleResolvePeerAgent(ResolvePeerAgent message)
    {
        if (_agentIdToRef.TryGetValue(message.AgentId, out var actorRef) &&
            _agents.TryGetValue(actorRef, out var ad))
        {
            Sender.Tell(new PeerAgentResolved(
                message.AgentId,
                Found: true,
                AgentRef: actorRef,
                EndpointUrl: ad.EndpointUrl));
        }
        else
        {
            Sender.Tell(new PeerAgentResolved(
                message.AgentId,
                Found: false));
        }
    }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests.ResolvePeerAgent" --verbosity quiet`
Expected: PASS (2 tests)

**Step 5: Run all existing tests to ensure no regressions**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: All 454+ tests PASS.

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/InternalMessages.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryActorTests.cs
git commit -m "feat(rfc-005): add ResolvePeerAgent lookup to AgentRegistryActor"
```

---

### Task 4: PeerMessage Handler in SwarmAgentActor

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/SwarmAgentActorTests.cs`

**Step 1: Read existing SwarmAgentActorTests to understand the test helper pattern**

Check `SwarmAgentActorTests.cs` for how the actor is created in tests — it likely needs `RuntimeOptions`, `AgentFrameworkRoleEngine`, `RuntimeTelemetry`, and `IActorRef` for the registry. Use the same factory pattern.

**Step 2: Write the failing tests**

Add to `SwarmAgentActorTests.cs`:

```csharp
    [Fact]
    public void PeerMessage_TaskRequest_RepliesWithAck()
    {
        // Use existing CreateAgent helper or build inline
        var agent = CreateAgent(SwarmRole.Builder);

        var msg = new PeerMessage(
            MessageId: "msg-001",
            FromAgentId: "planner-01",
            ToAgentId: "builder-01",
            Type: PeerMessageType.TaskRequest,
            Payload: """{"taskId":"task-01","description":"Build endpoint"}""");

        agent.Tell(msg, TestActor);
        var ack = ExpectMsg<PeerMessageAck>();
        Assert.Equal("msg-001", ack.MessageId);
        Assert.True(ack.Accepted);
    }

    [Fact]
    public void PeerMessage_HelpRequest_RepliesWithAck()
    {
        var agent = CreateAgent(SwarmRole.Reviewer);

        var msg = new PeerMessage(
            MessageId: "msg-002",
            FromAgentId: "builder-01",
            ToAgentId: "reviewer-01",
            Type: PeerMessageType.HelpRequest,
            Payload: """{"description":"Review this code"}""");

        agent.Tell(msg, TestActor);
        var ack = ExpectMsg<PeerMessageAck>();
        Assert.True(ack.Accepted);
    }

    [Fact]
    public void PeerMessage_Broadcast_RepliesWithAck()
    {
        var agent = CreateAgent(SwarmRole.Builder);

        var msg = new PeerMessage(
            MessageId: "msg-003",
            FromAgentId: "planner-01",
            ToAgentId: "builder-01",
            Type: PeerMessageType.Broadcast,
            Payload: """{"signal":"task.complete","taskId":"task-01"}""");

        agent.Tell(msg, TestActor);
        var ack = ExpectMsg<PeerMessageAck>();
        Assert.True(ack.Accepted);
    }
```

**Step 3: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~SwarmAgentActorTests.PeerMessage" --verbosity quiet`
Expected: FAIL — `SwarmAgentActor` has no handler for `PeerMessage`.

**Step 4: Write minimal implementation**

Add to `SwarmAgentActor` constructor, after the `Receive<ContractNetAward>(HandleContractNetAward);` line:

```csharp
        Receive<PeerMessage>(HandlePeerMessage);
```

Add `using SwarmAssistant.Contracts.Messaging;` to the top of `SwarmAgentActor.cs` if `PeerMessage` is not already resolvable (it's in the Contracts project, which should already be referenced).

Add handler method:

```csharp
    private void HandlePeerMessage(PeerMessage message)
    {
        _logger.LogInformation(
            "Peer message received messageId={MessageId} from={From} type={Type}",
            message.MessageId, message.FromAgentId, message.Type);

        Sender.Tell(new PeerMessageAck(message.MessageId, Accepted: true));
    }
```

**Step 5: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~SwarmAgentActorTests.PeerMessage" --verbosity quiet`
Expected: PASS (3 tests)

**Step 6: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: All tests PASS.

**Step 7: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/SwarmAgentActorTests.cs
git commit -m "feat(rfc-005): add PeerMessage handler to SwarmAgentActor"
```

---

### Task 5: ForwardPeerMessage in AgentRegistryActor

The registry doesn't relay messages (approach B), but it needs to support a `ForwardPeerMessage` for the HTTP endpoint — look up the target agent by ID and forward the `PeerMessage` directly. This keeps the HTTP layer thin.

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/InternalMessages.cs`
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryActorTests.cs`

**Step 1: Write the failing tests**

Add to `AgentRegistryActorTests.cs`:

```csharp
    [Fact]
    public void ForwardPeerMessage_DeliversToTargetAgent()
    {
        var (registry, _, _) = CreateRegistry();

        var targetProbe = CreateTestProbe();
        registry.Tell(new AgentCapabilityAdvertisement(
            targetProbe.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), targetProbe);

        var peerMsg = new PeerMessage(
            MessageId: "msg-fwd-01",
            FromAgentId: "planner-01",
            ToAgentId: "builder-01",
            Type: PeerMessageType.TaskRequest,
            Payload: """{"taskId":"task-01"}""");

        registry.Tell(new ForwardPeerMessage(peerMsg));
        var ack = ExpectMsg<PeerMessageAck>();
        Assert.True(ack.Accepted);

        // Target agent should have received the PeerMessage
        targetProbe.ExpectMsg<PeerMessage>(msg =>
            msg.MessageId == "msg-fwd-01" && msg.FromAgentId == "planner-01");
    }

    [Fact]
    public void ForwardPeerMessage_ReturnsNotFoundForUnknownAgent()
    {
        var (registry, _, _) = CreateRegistry();

        var peerMsg = new PeerMessage(
            MessageId: "msg-fwd-02",
            FromAgentId: "planner-01",
            ToAgentId: "nonexistent",
            Type: PeerMessageType.TaskRequest,
            Payload: "{}");

        registry.Tell(new ForwardPeerMessage(peerMsg));
        var ack = ExpectMsg<PeerMessageAck>();
        Assert.False(ack.Accepted);
        Assert.Equal("agent_not_found", ack.Reason);
    }
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests.ForwardPeerMessage" --verbosity quiet`
Expected: FAIL — `ForwardPeerMessage` not defined.

**Step 3: Write minimal implementation**

Add to `InternalMessages.cs`:

```csharp
// Peer communication: forward message to target agent (RFC-005)
internal sealed record ForwardPeerMessage(PeerMessage Message);
```

Note: `ForwardPeerMessage` needs `using SwarmAssistant.Contracts.Messaging;` in `InternalMessages.cs` — check if already present (it is, via `SwarmRole` usage).

Add to `AgentRegistryActor.cs` constructor, after `Receive<ResolvePeerAgent>(HandleResolvePeerAgent);`:

```csharp
        Receive<ForwardPeerMessage>(HandleForwardPeerMessage);
```

Add handler:

```csharp
    private void HandleForwardPeerMessage(ForwardPeerMessage message)
    {
        var targetId = message.Message.ToAgentId;
        if (_agentIdToRef.TryGetValue(targetId, out var targetRef))
        {
            targetRef.Tell(message.Message, Sender);
            Sender.Tell(new PeerMessageAck(message.Message.MessageId, Accepted: true));
        }
        else
        {
            _logger.LogWarning("Peer message target not found: {TargetAgentId}", targetId);
            Sender.Tell(new PeerMessageAck(message.Message.MessageId, Accepted: false, Reason: "agent_not_found"));
        }
    }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests.ForwardPeerMessage" --verbosity quiet`
Expected: PASS (2 tests)

**Step 5: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: All tests PASS.

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/InternalMessages.cs \
       project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryActorTests.cs
git commit -m "feat(rfc-005): add ForwardPeerMessage routing in AgentRegistryActor"
```

---

### Task 6: PeerMessage DTO and OpenAPI Schema

**Files:**
- Create: `project/dotnet/src/SwarmAssistant.Runtime/Dto/PeerMessageDto.cs`
- Modify: `project/docs/openapi/runtime.v1.yaml`

**Step 1: Create DTO record**

```csharp
// project/dotnet/src/SwarmAssistant.Runtime/Dto/PeerMessageDto.cs
namespace SwarmAssistant.Runtime.Dto;

public sealed record PeerMessageSubmitDto(
    string? MessageId,
    string FromAgentId,
    string ToAgentId,
    string Type,
    string Payload,
    string? ReplyTo);

public sealed record PeerMessageAckDto(
    string MessageId,
    bool Accepted,
    string? Reason);
```

**Step 2: Add OpenAPI schema and endpoint**

Add to `runtime.v1.yaml` schemas section (after `AgentRegistryEntry`):

```yaml
    # ── Peer message (RFC-005) ────────────────────────────────────────────
    PeerMessageSubmit:
      type: object
      description: Submit a peer-to-peer message to a specific agent.
      required:
        - fromAgentId
        - toAgentId
        - type
        - payload
      properties:
        messageId:
          type: string
          description: Optional caller-assigned message ID. Auto-generated if omitted.
        fromAgentId:
          type: string
          description: Agent ID of the sender.
        toAgentId:
          type: string
          description: Agent ID of the target recipient.
        type:
          type: string
          enum:
            - task-request
            - task-response
            - help-request
            - help-response
            - broadcast
          description: Message type.
        payload:
          type: string
          description: JSON-encoded message payload. Maximum 64KB.
          maxLength: 65536
        replyTo:
          type: string
          description: Optional callback URL for the sender's message endpoint.

    PeerMessageAck:
      type: object
      description: Acknowledgement of a peer message delivery attempt.
      required:
        - messageId
        - accepted
      properties:
        messageId:
          type: string
        accepted:
          type: boolean
        reason:
          type:
            - string
            - "null"
          description: Reason for rejection (only present when accepted is false).
```

Add endpoint to paths section (after `/a2a/registry/agents`):

```yaml
  /a2a/messages:
    post:
      operationId: sendPeerMessage
      summary: Send a peer-to-peer message
      description: >
        Routes a message from one agent to another via the agent registry.
        The target agent must be registered and healthy.
      tags:
        - A2A
      security:
        - ApiKeyAuth: []
        - {}
      requestBody:
        required: true
        content:
          application/json:
            schema:
              $ref: "#/components/schemas/PeerMessageSubmit"
      responses:
        "202":
          description: Message accepted and forwarded to the target agent.
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/PeerMessageAck"
        "400":
          $ref: "#/components/responses/BadRequest"
        "401":
          $ref: "#/components/responses/Unauthorized"
        "404":
          description: Target agent not found in the registry.
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/PeerMessageAck"
        "413":
          description: Payload exceeds 64KB size limit.
          content:
            application/json:
              schema:
                $ref: "#/components/schemas/ErrorEnvelope"
        "503":
          $ref: "#/components/responses/ServiceUnavailable"
        "504":
          $ref: "#/components/responses/GatewayTimeout"
```

**Step 3: Verify build compiles**

Run: `dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: Build succeeded.

**Step 4: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Dto/PeerMessageDto.cs \
       project/docs/openapi/runtime.v1.yaml
git commit -m "feat(rfc-005): add PeerMessage DTO and OpenAPI schema for POST /a2a/messages"
```

---

### Task 7: POST /a2a/messages HTTP Endpoint

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Program.cs`

**Step 1: Add the endpoint**

Insert in Program.cs inside the `if (options.A2AEnabled)` block, after the `/a2a/registry/agents` endpoint mapping (after line 944):

```csharp
    const int MaxPeerMessagePayloadBytes = 65_536;

    app.MapPost("/a2a/messages", async (
        PeerMessageSubmitDto body,
        RuntimeActorRegistry actorRegistry,
        CancellationToken cancellationToken) =>
    {
        if (!actorRegistry.TryGetAgentRegistry(out var registry))
        {
            return Results.Problem(
                detail: "Agent registry is not available",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (string.IsNullOrWhiteSpace(body.FromAgentId) ||
            string.IsNullOrWhiteSpace(body.ToAgentId) ||
            string.IsNullOrWhiteSpace(body.Type) ||
            string.IsNullOrWhiteSpace(body.Payload))
        {
            return Results.BadRequest(new { error = "fromAgentId, toAgentId, type, and payload are required" });
        }

        if (body.Payload.Length > MaxPeerMessagePayloadBytes)
        {
            return Results.Problem(
                detail: $"Payload exceeds {MaxPeerMessagePayloadBytes} byte limit",
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        if (!Enum.TryParse<PeerMessageType>(
                body.Type.Replace("-", ""),
                ignoreCase: true,
                out var messageType))
        {
            return Results.BadRequest(new
            {
                error = "Invalid message type",
                type = body.Type,
                validValues = new[] { "task-request", "task-response", "help-request", "help-response", "broadcast" }
            });
        }

        var messageId = body.MessageId ?? $"msg-{Guid.NewGuid():N}"[..20];
        var peerMessage = new PeerMessage(
            MessageId: messageId,
            FromAgentId: body.FromAgentId,
            ToAgentId: body.ToAgentId,
            Type: messageType,
            Payload: body.Payload,
            ReplyTo: body.ReplyTo,
            Timestamp: DateTimeOffset.UtcNow);

        try
        {
            var ack = await registry.Ask<PeerMessageAck>(
                new ForwardPeerMessage(peerMessage),
                TimeSpan.FromSeconds(5),
                cancellationToken);

            var dto = new PeerMessageAckDto(ack.MessageId, ack.Accepted, ack.Reason);
            return ack.Accepted
                ? Results.Accepted(value: dto)
                : Results.NotFound(dto);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Results.Problem(
                detail: "Request was cancelled",
                statusCode: StatusCodes.Status499ClientClosedRequest);
        }
        catch (TaskCanceledException)
        {
            return Results.Problem(
                detail: "Peer message routing timed out",
                statusCode: StatusCodes.Status504GatewayTimeout);
        }
    }).AddEndpointFilter(requireApiKey);
```

Add `using SwarmAssistant.Runtime.Dto;` and `using SwarmAssistant.Contracts.Messaging;` to the top of Program.cs if not already present.

Add `using SwarmAssistant.Runtime.Actors;` if `ForwardPeerMessage` is internal — note: `ForwardPeerMessage` is `internal` in the Runtime project, so it should be directly accessible in Program.cs (same assembly).

**Step 2: Build to verify**

Run: `dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: Build succeeded.

**Step 3: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: All tests PASS.

**Step 4: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Program.cs
git commit -m "feat(rfc-005): add POST /a2a/messages endpoint for peer communication"
```

---

### Task 8: Peer Message Tracing (Langfuse/OpenTelemetry)

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs`

**Step 1: Read RuntimeTelemetry to understand the tracing API**

Check `project/dotnet/src/SwarmAssistant.Runtime/Telemetry/RuntimeTelemetry.cs` for the `StartActivity` method signature.

**Step 2: Add tracing to the PeerMessage handler**

Update `HandlePeerMessage` in `SwarmAgentActor.cs`:

```csharp
    private void HandlePeerMessage(PeerMessage message)
    {
        using var activity = _telemetry.StartActivity(
            "swarm-agent.peer.message",
            tags: new Dictionary<string, object?>
            {
                ["peer.messageId"] = message.MessageId,
                ["peer.fromAgentId"] = message.FromAgentId,
                ["peer.toAgentId"] = message.ToAgentId,
                ["peer.type"] = message.Type.ToString(),
                ["actor.name"] = Self.Path.Name,
            });

        _logger.LogInformation(
            "Peer message received messageId={MessageId} from={From} type={Type}",
            message.MessageId, message.FromAgentId, message.Type);

        Sender.Tell(new PeerMessageAck(message.MessageId, Accepted: true));
    }
```

**Step 3: Build and run tests**

Run: `dotnet build project/dotnet/SwarmAssistant.sln --verbosity quiet && dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: Build + all tests PASS.

**Step 4: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/SwarmAgentActor.cs
git commit -m "feat(rfc-005): add OpenTelemetry tracing for peer messages"
```

---

### Task 9: AG-UI Dashboard Events for Peer Messages

**Files:**
- Modify: `project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs`
- Modify: `project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryActorTests.cs`

**Step 1: Write the failing test**

Add to `AgentRegistryActorTests.cs`:

```csharp
    [Fact]
    public void ForwardPeerMessage_EmitsAgUiDashboardEvent()
    {
        var (registry, _, uiEvents) = CreateRegistry();

        var targetProbe = CreateTestProbe();
        registry.Tell(new AgentCapabilityAdvertisement(
            targetProbe.Ref.Path.ToString(),
            new[] { SwarmRole.Builder },
            0, agentId: "builder-01"), targetProbe);

        var peerMsg = new PeerMessage(
            MessageId: "msg-ui-01",
            FromAgentId: "planner-01",
            ToAgentId: "builder-01",
            Type: PeerMessageType.TaskRequest,
            Payload: "{}");

        registry.Tell(new ForwardPeerMessage(peerMsg));
        ExpectMsg<PeerMessageAck>();

        AwaitAssert(() =>
        {
            var recent = uiEvents.GetRecent(10);
            Assert.Contains(recent, e => e.Type == "agui.dashboard.messages");
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100));
    }
```

**Step 2: Run test to verify it fails**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests.ForwardPeerMessage_EmitsAgUiDashboardEvent" --verbosity quiet`
Expected: FAIL — no `agui.dashboard.messages` event emitted.

**Step 3: Update HandleForwardPeerMessage to emit dashboard event**

```csharp
    private void HandleForwardPeerMessage(ForwardPeerMessage message)
    {
        var targetId = message.Message.ToAgentId;
        if (_agentIdToRef.TryGetValue(targetId, out var targetRef))
        {
            targetRef.Tell(message.Message, Sender);
            Sender.Tell(new PeerMessageAck(message.Message.MessageId, Accepted: true));
            EmitPeerMessageDashboardEvent(message.Message);
        }
        else
        {
            _logger.LogWarning("Peer message target not found: {TargetAgentId}", targetId);
            Sender.Tell(new PeerMessageAck(message.Message.MessageId, Accepted: false, Reason: "agent_not_found"));
        }
    }

    private void EmitPeerMessageDashboardEvent(PeerMessage message)
    {
        _uiEvents?.Publish("agui.dashboard.messages", null, new
        {
            protocol = "a2ui/v0.8",
            operation = "updateDataModel",
            surfaceType = "dashboard",
            layer = "peer-messages",
            data = new
            {
                messageId = message.MessageId,
                fromAgentId = message.FromAgentId,
                toAgentId = message.ToAgentId,
                type = message.Type.ToString(),
                timestamp = message.Timestamp ?? DateTimeOffset.UtcNow
            }
        });
    }
```

**Step 4: Run test to verify it passes**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --filter "FullyQualifiedName~AgentRegistryActorTests.ForwardPeerMessage" --verbosity quiet`
Expected: PASS (3 tests including the new dashboard event test).

**Step 5: Run all tests**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: All tests PASS.

**Step 6: Commit**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Actors/AgentRegistryActor.cs \
       project/dotnet/tests/SwarmAssistant.Runtime.Tests/AgentRegistryActorTests.cs
git commit -m "feat(rfc-005): add AG-UI dashboard events for peer messages"
```

---

### Task 10: Regenerate Models and Final Verification

**Files:**
- Modify: generated model files (via `task models:generate`)

**Step 1: Regenerate models from updated OpenAPI spec**

Run: `task models:generate`
Expected: Success — models regenerated from `runtime.v1.yaml`.

**Step 2: Verify models are not stale**

Run: `task models:verify`
Expected: "Models are up to date."

**Step 3: Run full test suite**

Run: `dotnet test project/dotnet/SwarmAssistant.sln --verbosity quiet`
Expected: All tests PASS (454+ original + ~15 new = 469+ total).

**Step 4: Run linters**

Run: `task lint`
Expected: No errors.

**Step 5: Commit generated models**

```bash
git add project/dotnet/src/SwarmAssistant.Runtime/Models/
git commit -m "chore(rfc-005): regenerate models from updated OpenAPI spec"
```

---

### Task 11: Update Handover Documentation

**Files:**
- Modify: `docs/handover/2026-02-25-rfc-004-to-005.md` (mark RFC-005 foundation as done)

**Step 1: Update the handover doc**

Mark the foundation tasks section as completed and note the test count. Update the RFC roadmap section to show RFC-005 in progress.

**Step 2: Commit**

```bash
git add docs/handover/2026-02-25-rfc-004-to-005.md
git commit -m "docs(rfc-005): update handover with foundation task completion"
```

---

## Summary

| Task | What | New Tests |
|------|------|-----------|
| 1 | PeerMessage + PeerMessageAck contracts | 4 |
| 2 | GlobalBlackboardKeys for peer signals | 5 |
| 3 | ResolvePeerAgent in registry | 2 |
| 4 | PeerMessage handler in SwarmAgentActor | 3 |
| 5 | ForwardPeerMessage routing in registry | 2 |
| 6 | DTO + OpenAPI schema | 0 (build check) |
| 7 | POST /a2a/messages endpoint | 0 (integration) |
| 8 | OpenTelemetry tracing | 0 (build check) |
| 9 | AG-UI dashboard events | 1 |
| 10 | Model regeneration + verification | 0 |
| 11 | Handover doc update | 0 |
| **Total** | | **~17 new tests** |

## Post-Foundation: Swarm Tasks

After these 11 foundation tasks are committed, the handover doc describes 5 swarm tasks to submit via `POST /a2a/tasks` for deeper peer behavior. Those are separate from this plan.
