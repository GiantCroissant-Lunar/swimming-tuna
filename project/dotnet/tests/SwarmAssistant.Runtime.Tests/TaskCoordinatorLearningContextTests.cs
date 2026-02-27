using Akka.Actor;
using Akka.TestKit.Xunit2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Contracts.Tasks;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Langfuse;
using SwarmAssistant.Runtime.Planning;
using SwarmAssistant.Runtime.Skills;
using SwarmAssistant.Runtime.Tasks;
using SwarmAssistant.Runtime.Telemetry;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tests;

public sealed class TaskCoordinatorLearningContextTests : TestKit
{
    private readonly RuntimeOptions _options = new()
    {
        AgentFrameworkExecutionMode = "subscription-cli-fallback",
        CliAdapterOrder = ["local-echo"],
        WorkerPoolSize = 1,
        ReviewerPoolSize = 1,
        MaxCliConcurrency = 2,
        SandboxMode = "none",
    };

    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    [Fact]
    public void PlanAndBuildPrompts_IncludeSkillsAndLangfuseContext_AndLangfuseIsCached()
    {
        var parentProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var uiEvents = new UiEventStream();
        var telemetry = new RuntimeTelemetry(_options, _loggerFactory);
        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, telemetry);

        var taskId = $"learn-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Budget dispatch tuning", "Improve budget-aware task dispatch behavior", DateTimeOffset.UtcNow));

        var langfuse = new FakeLangfuseSimilarityQuery("Similar past task: budget dispatch succeeded");
        var matcher = new SkillMatcher(
        [
            new SkillDefinition(
                "budget-guard",
                "Budget lifecycle guidance",
                ["budget", "dispatch"],
                [SwarmRole.Planner, SwarmRole.Builder],
                "global",
                "Prefer healthy-budget agents before low-budget agents.",
                "tests")
        ]);

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId,
                "Budget dispatch tuning",
                "Improve budget-aware task dispatch behavior",
                workerProbe.Ref,
                reviewerProbe.Ref,
                supervisorProbe.Ref,
                blackboardProbe.Ref,
                ActorRefs.Nobody,
                roleEngine,
                goapPlanner,
                _loggerFactory,
                telemetry,
                uiEvents,
                registry,
                _options,
                null,
                null,
                null,
                2,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                langfuse,
                matcher,
                (string?)null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));

        coordinator.Tell(new RoleTaskSucceeded(taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 1.0, null, "worker"));
        var plannerTask = workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));
        Assert.NotNull(plannerTask.Prompt);
        Assert.Contains("--- Agent Skills ---", plannerTask.Prompt!, StringComparison.Ordinal);
        Assert.Contains("Historical Learning (Langfuse)", plannerTask.Prompt!, StringComparison.Ordinal);

        coordinator.Tell(new RoleTaskSucceeded(taskId, SwarmRole.Planner, "plan output with no subtasks", DateTimeOffset.UtcNow, 1.0, null, "worker"));
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));
        coordinator.Tell(new RoleTaskSucceeded(taskId, SwarmRole.Orchestrator, "ACTION: Build", DateTimeOffset.UtcNow, 1.0, null, "worker"));

        var builderTask = workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Builder, TimeSpan.FromSeconds(5));
        Assert.NotNull(builderTask.Prompt);
        Assert.Contains("--- Agent Skills ---", builderTask.Prompt!, StringComparison.Ordinal);
        Assert.Contains("Historical Learning (Langfuse)", builderTask.Prompt!, StringComparison.Ordinal);
        Assert.Equal(1, langfuse.CallCount);
    }

    [Fact]
    public void PlanDispatch_WhenLangfuseQueryFails_ContinuesWithoutBlocking()
    {
        var parentProbe = CreateTestProbe();
        var workerProbe = CreateTestProbe();
        var reviewerProbe = CreateTestProbe();
        var supervisorProbe = CreateTestProbe();
        var blackboardProbe = CreateTestProbe();
        var uiEvents = new UiEventStream();
        var telemetry = new RuntimeTelemetry(_options, _loggerFactory);
        var registry = new TaskRegistry(new NoOpTaskMemoryWriter(), NullLogger<TaskRegistry>.Instance);
        var goapPlanner = new GoapPlanner(SwarmActions.All);
        var roleEngine = new AgentFrameworkRoleEngine(_options, _loggerFactory, telemetry);

        var taskId = $"learn-failopen-{Guid.NewGuid():N}";
        registry.Register(new TaskAssigned(taskId, "Langfuse fallback", "Ensure dispatch is fail-open when Langfuse is unavailable", DateTimeOffset.UtcNow));
        var langfuse = new FakeLangfuseSimilarityQuery(error: new InvalidOperationException("simulated failure"));

        var coordinator = parentProbe.ChildActorOf(
            Props.Create(() => new TaskCoordinatorActor(
                taskId,
                "Langfuse fallback",
                "Ensure dispatch is fail-open when Langfuse is unavailable",
                workerProbe.Ref,
                reviewerProbe.Ref,
                supervisorProbe.Ref,
                blackboardProbe.Ref,
                ActorRefs.Nobody,
                roleEngine,
                goapPlanner,
                _loggerFactory,
                telemetry,
                uiEvents,
                registry,
                _options,
                null,
                null,
                null,
                2,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                langfuse,
                null,
                (string?)null)));

        coordinator.Tell(new TaskCoordinatorActor.StartCoordination());
        workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Orchestrator, TimeSpan.FromSeconds(5));
        coordinator.Tell(new RoleTaskSucceeded(taskId, SwarmRole.Orchestrator, "ACTION: Plan", DateTimeOffset.UtcNow, 1.0, null, "worker"));

        var plannerTask = workerProbe.ExpectMsg<ExecuteRoleTask>(m => m.Role == SwarmRole.Planner, TimeSpan.FromSeconds(5));
        Assert.NotNull(plannerTask.Prompt);
        Assert.DoesNotContain("Historical Learning (Langfuse)", plannerTask.Prompt!, StringComparison.Ordinal);
        Assert.Equal(1, langfuse.CallCount);
    }

    private sealed class FakeLangfuseSimilarityQuery : ILangfuseSimilarityQuery
    {
        private readonly string? _result;
        private readonly Exception? _error;
        public int CallCount { get; private set; }

        public FakeLangfuseSimilarityQuery(string? result = null, Exception? error = null)
        {
            _result = result;
            _error = error;
        }

        public Task<string?> GetSimilarTaskContextAsync(string taskDescription, CancellationToken ct)
        {
            CallCount++;
            if (_error is not null)
            {
                throw _error;
            }

            return Task.FromResult(_result);
        }
    }

    private sealed class NoOpTaskMemoryWriter : ITaskMemoryWriter
    {
        public Task WriteAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
