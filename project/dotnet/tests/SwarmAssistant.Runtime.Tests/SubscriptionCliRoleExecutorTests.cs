using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;
using SwarmAssistant.Runtime.Actors;
using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Execution;

namespace SwarmAssistant.Runtime.Tests;

public sealed class SubscriptionCliRoleExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_LocalEchoOnly_ReturnsDeterministicOutput()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask(
                "task-1",
                SwarmRole.Planner,
                "Implement CLI routing",
                "Use subscription CLIs with fallback",
                null,
                null),
            CancellationToken.None);

        Assert.Equal("local-echo", result.AdapterId);
        Assert.Contains("[LocalEcho/Planner]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_NoKnownAdapters_Throws()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["unknown-adapter"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                new ExecuteRoleTask(
                    "task-1",
                    SwarmRole.Builder,
                    "Implement CLI routing",
                    "Use subscription CLIs with fallback",
                    "plan-output",
                    null),
                CancellationToken.None));

        Assert.Contains("No CLI adapter succeeded", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_WithWorkspacePath_ReturnsOutput()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host"
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask(
                "task-worktree-1",
                SwarmRole.Builder,
                "Build feature",
                "Build in isolated worktree",
                "plan-output",
                null,
                WorkspacePath: "/tmp/worktree-test"),
            CancellationToken.None);

        Assert.Equal("local-echo", result.AdapterId);
        Assert.Contains("[LocalEcho/Builder]", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_WithRoleModelMapping_ResolvesModelForRole()
    {
        var options = new RuntimeOptions
        {
            CliAdapterOrder = ["local-echo"],
            SandboxMode = "host",
            RoleModelMapping = new Dictionary<string, RoleModelPreference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Planner"] = new()
                {
                    Model = "anthropic/claude-sonnet-4-6",
                    Reasoning = "high"
                }
            }
        };

        using var loggerFactory = LoggerFactory.Create(builder => { });
        var executor = new SubscriptionCliRoleExecutor(options, loggerFactory);

        var result = await executor.ExecuteAsync(
            new ExecuteRoleTask(
                "task-model-1",
                SwarmRole.Planner,
                "Plan task",
                "Use role model mapping",
                null,
                null),
            CancellationToken.None);

        Assert.NotNull(result.Model);
        Assert.Equal("claude-sonnet-4-6", result.Model!.Id);
        Assert.Equal("anthropic", result.Model.Provider);
        Assert.Equal("high", result.Reasoning);
    }

    [Fact]
    public void NormalizeOutput_StripsAnsiAndTrims()
    {
        var normalized = SubscriptionCliRoleExecutor.NormalizeOutput("\u001b[0mhi\u001b[0m\r\n");
        Assert.Equal("hi", normalized);
    }

    [Fact]
    public void FindRejectedOutputMatch_ReturnsCommonAuthSnippet()
    {
        var match = SubscriptionCliRoleExecutor.FindRejectedOutputMatch(
            "Authorization failed, please check your login status",
            []);

        Assert.Equal("authorization failed", match);
    }

    [Fact]
    public void FindRejectedOutputMatch_ReturnsAdapterSpecificSnippet()
    {
        var match = SubscriptionCliRoleExecutor.FindRejectedOutputMatch(
            "token expired",
            ["token expired"]);

        Assert.Equal("token expired", match);
    }

    [Fact]
    public void AdapterDefinitions_ContainsPiEntry_WithCorrectConfiguration()
    {
        var adapterDefinitionsField = typeof(SubscriptionCliRoleExecutor)
            .GetField("AdapterDefinitions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(adapterDefinitionsField);

        var adapterDefinitions = adapterDefinitionsField?.GetValue(null);
        Assert.NotNull(adapterDefinitions);

        var containsKeyMethod = adapterDefinitions!.GetType().GetMethod("ContainsKey");
        var indexer = adapterDefinitions.GetType().GetProperty("Item");
        Assert.NotNull(containsKeyMethod);
        Assert.NotNull(indexer);

        var containsPiResult = containsKeyMethod.Invoke(adapterDefinitions, ["pi"]);
        Assert.NotNull(containsPiResult);
        var containsPi = (bool)containsPiResult!;
        Assert.True(containsPi);

        var piAdapter = indexer.GetValue(adapterDefinitions, ["pi"]);
        Assert.NotNull(piAdapter);
        var adapterType = piAdapter!.GetType();

        var idProperty = adapterType.GetProperty("Id");
        var probeCommandProperty = adapterType.GetProperty("ProbeCommand");
        var probeArgsProperty = adapterType.GetProperty("ProbeArgs");
        var executeCommandProperty = adapterType.GetProperty("ExecuteCommand");
        var executeArgsProperty = adapterType.GetProperty("ExecuteArgs");
        var rejectOutputSubstringsProperty = adapterType.GetProperty("RejectOutputSubstrings");
        var modelFlagProperty = adapterType.GetProperty("ModelFlag");
        var reasoningFlagProperty = adapterType.GetProperty("ReasoningFlag");
        var isInternalProperty = adapterType.GetProperty("IsInternal");

        Assert.Equal("pi", idProperty?.GetValue(piAdapter));
        Assert.Equal("pi", probeCommandProperty?.GetValue(piAdapter));

        var probeArgs = probeArgsProperty?.GetValue(piAdapter) as string[];
        Assert.NotNull(probeArgs);
        Assert.Equal(["--help"], probeArgs!);

        Assert.Equal("pi", executeCommandProperty?.GetValue(piAdapter));

        var executeArgs = executeArgsProperty?.GetValue(piAdapter) as string[];
        Assert.NotNull(executeArgs);
        Assert.Equal(["--print", "--prompt", "{{prompt}}"], executeArgs!);

        var rejectOutputSubstrings = rejectOutputSubstringsProperty?.GetValue(piAdapter) as string[];
        Assert.NotNull(rejectOutputSubstrings);
        Assert.Equal(["error: no api key", "error: authentication"], rejectOutputSubstrings!);

        Assert.Equal("--model", modelFlagProperty?.GetValue(piAdapter));
        Assert.Equal("--thinking", reasoningFlagProperty?.GetValue(piAdapter));
        Assert.Equal(false, isInternalProperty?.GetValue(piAdapter));
    }

    [Fact]
    public void DefaultAdapterOrder_ContainsPi_BetweenKiloAndLocalEcho()
    {
        var defaultAdapterOrderField = typeof(SubscriptionCliRoleExecutor)
            .GetField("DefaultAdapterOrder", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(defaultAdapterOrderField);

        var defaultAdapterOrder = defaultAdapterOrderField?.GetValue(null) as string[];
        Assert.NotNull(defaultAdapterOrder);

        var kiloIndex = Array.IndexOf(defaultAdapterOrder!, "kilo");
        var piIndex = Array.IndexOf(defaultAdapterOrder!, "pi");
        var localEchoIndex = Array.IndexOf(defaultAdapterOrder!, "local-echo");

        Assert.True(kiloIndex >= 0, "DefaultAdapterOrder should contain 'kilo'");
        Assert.True(piIndex >= 0, "DefaultAdapterOrder should contain 'pi'");
        Assert.True(localEchoIndex >= 0, "DefaultAdapterOrder should contain 'local-echo'");
        Assert.True(kiloIndex < piIndex && piIndex < localEchoIndex,
            "'pi' should be positioned between 'kilo' and 'local-echo' in DefaultAdapterOrder");
    }
}
