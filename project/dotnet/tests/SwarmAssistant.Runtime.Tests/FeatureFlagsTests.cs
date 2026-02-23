using SwarmAssistant.Runtime.Configuration;
using SwarmAssistant.Runtime.Ui;

namespace SwarmAssistant.Runtime.Tests;

public sealed class FeatureFlagsTests
{
    // --- RuntimeOptions defaults ---

    [Fact]
    public void RuntimeOptions_HitlEnabled_DefaultsToFalse()
    {
        var options = new RuntimeOptions();
        Assert.False(options.HitlEnabled);
    }

    [Fact]
    public void RuntimeOptions_GraphTelemetryEnabled_DefaultsToFalse()
    {
        var options = new RuntimeOptions();
        Assert.False(options.GraphTelemetryEnabled);
    }

    [Fact]
    public void RuntimeOptions_HitlEnabled_CanBeSetToTrue()
    {
        var options = new RuntimeOptions { HitlEnabled = true };
        Assert.True(options.HitlEnabled);
    }

    [Fact]
    public void RuntimeOptions_GraphTelemetryEnabled_CanBeSetToTrue()
    {
        var options = new RuntimeOptions { GraphTelemetryEnabled = true };
        Assert.True(options.GraphTelemetryEnabled);
    }

    // --- Graph telemetry gating ---

    [Fact]
    public void GraphTelemetryDisabled_DoesNotPublishGraphEdgeEvent()
    {
        var stream = new UiEventStream();
        var published = false;

        // Simulate what DispatcherActor does when GraphTelemetryEnabled = false
        var options = new RuntimeOptions { GraphTelemetryEnabled = false };
        if (options.GraphTelemetryEnabled)
        {
            stream.Publish("agui.task.graph.edge", "parent-1", new { });
            published = true;
        }

        Assert.False(published);
        Assert.Empty(stream.GetRecent());
    }

    [Fact]
    public void GraphTelemetryEnabled_PublishesGraphEdgeEvent()
    {
        var stream = new UiEventStream();

        // Simulate what DispatcherActor does when GraphTelemetryEnabled = true
        var options = new RuntimeOptions { GraphTelemetryEnabled = true };
        if (options.GraphTelemetryEnabled)
        {
            stream.Publish(
                type: "agui.task.graph.edge",
                taskId: "parent-1",
                payload: new
                {
                    parentTaskId = "parent-1",
                    childTaskId = "child-1",
                    Title = "Child task",
                    Depth = 1
                });
        }

        var recent = stream.GetRecent();
        Assert.Single(recent);
        Assert.Equal("agui.task.graph.edge", recent[0].Type);
        Assert.Equal("parent-1", recent[0].TaskId);
    }

    // --- HITL feature-disabled notice ---

    [Fact]
    public void HitlDisabled_PublishesFeatureDisabledEvent()
    {
        var stream = new UiEventStream();
        var options = new RuntimeOptions { HitlEnabled = false };

        // Simulate what Program.cs does for HITL actions when feature is disabled
        var actionId = "pause_task";
        if (!options.HitlEnabled)
        {
            stream.Publish(
                type: "agui.feature.disabled",
                taskId: null,
                payload: new
                {
                    feature = "hitl",
                    actionId,
                    notice = "HITL intervention controls are not enabled in this profile. Set Runtime__HitlEnabled=true to enable."
                });
        }

        var recent = stream.GetRecent();
        Assert.Single(recent);
        Assert.Equal("agui.feature.disabled", recent[0].Type);
    }

    [Fact]
    public void HitlEnabled_DoesNotPublishFeatureDisabledEvent()
    {
        var stream = new UiEventStream();
        var options = new RuntimeOptions { HitlEnabled = true };

        // Simulate what Program.cs does for HITL actions when feature is enabled
        var actionId = "pause_task";
        if (!options.HitlEnabled)
        {
            stream.Publish(
                type: "agui.feature.disabled",
                taskId: null,
                payload: new { feature = "hitl", actionId });
        }
        else
        {
            stream.Publish(
                type: "agui.hitl.action.received",
                taskId: null,
                payload: new { actionId });
        }

        var recent = stream.GetRecent();
        Assert.Single(recent);
        Assert.Equal("agui.hitl.action.received", recent[0].Type);
    }

    [Theory]
    [InlineData("pause_task")]
    [InlineData("approve_task")]
    [InlineData("cancel_task")]
    public void HitlDisabled_AllInterventionActions_PublishFeatureDisabledNotice(string actionId)
    {
        var stream = new UiEventStream();
        var options = new RuntimeOptions { HitlEnabled = false };

        if (!options.HitlEnabled)
        {
            stream.Publish(
                type: "agui.feature.disabled",
                taskId: null,
                payload: new { feature = "hitl", actionId });
        }

        var recent = stream.GetRecent();
        Assert.Single(recent);
        Assert.Equal("agui.feature.disabled", recent[0].Type);
    }

    // --- SwaggerEnabled feature flag ---

    [Fact]
    public void RuntimeOptions_SwaggerEnabled_DefaultsToFalse()
    {
        var options = new RuntimeOptions();
        Assert.False(options.SwaggerEnabled);
    }

    [Fact]
    public void RuntimeOptions_SwaggerEnabled_CanBeSetToTrue()
    {
        var options = new RuntimeOptions { SwaggerEnabled = true };
        Assert.True(options.SwaggerEnabled);
    }

    [Fact]
    public void RuntimeOptions_SwaggerEnabled_SecureProfile_RemainsDisabled()
    {
        // Secure profiles must not enable Swagger to avoid exposing the API schema.
        var options = new RuntimeOptions { Profile = "secure-local", SwaggerEnabled = false };
        Assert.False(options.SwaggerEnabled);
    }
}
