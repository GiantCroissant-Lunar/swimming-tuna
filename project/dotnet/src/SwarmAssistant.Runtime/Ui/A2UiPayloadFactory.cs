using TaskState = SwarmAssistant.Contracts.Tasks.TaskStatus;

namespace SwarmAssistant.Runtime.Ui;

internal static class A2UiPayloadFactory
{
    public static object CreateSurface(string taskId, string title, string description, TaskState status)
    {
        return new
        {
            protocol = "a2ui/v0.8",
            operation = "createSurface",
            surface = new
            {
                id = SurfaceId(taskId),
                title = "Swarm Task Monitor",
                dataModel = new
                {
                    taskId,
                    title,
                    description,
                    status = status.ToString().ToLowerInvariant()
                },
                components = new object[]
                {
                    new
                    {
                        id = "task-title",
                        type = "text",
                        props = new { text = $"Task: {title}" }
                    },
                    new
                    {
                        id = "task-status",
                        type = "text",
                        props = new { text = $"Status: {status}" }
                    },
                    new
                    {
                        id = "request-snapshot",
                        type = "button",
                        props = new { label = "Request Snapshot", actionId = "request_snapshot" }
                    },
                    new
                    {
                        id = "refresh-surface",
                        type = "button",
                        props = new { label = "Refresh Surface", actionId = "refresh_surface" }
                    },
                    new
                    {
                        id = "load-memory",
                        type = "button",
                        props = new { label = "Load Memory", actionId = "load_memory" }
                    }
                }
            }
        };
    }

    public static object UpdateStatus(string taskId, TaskState status, string? detail = null)
    {
        return new
        {
            protocol = "a2ui/v0.8",
            operation = "updateDataModel",
            surfaceId = SurfaceId(taskId),
            dataModelPatch = new
            {
                status = status.ToString().ToLowerInvariant(),
                detail = detail ?? string.Empty,
                updatedAt = DateTimeOffset.UtcNow
            }
        };
    }

    public static object AppendMessage(string taskId, string role, string message)
    {
        return new
        {
            protocol = "a2ui/v0.8",
            operation = "updateDataModel",
            surfaceId = SurfaceId(taskId),
            dataModelPatch = new
            {
                lastRole = role,
                lastMessage = message,
                updatedAt = DateTimeOffset.UtcNow
            }
        };
    }

    /// <summary>
    /// Creates a surface with a nested GenUiComponent tree.
    /// The frontend GenUiNodeFactory recursively renders the children.
    /// </summary>
    public static object CreateSurface(string taskId, string title, GenUiComponent[] components)
    {
        return new
        {
            protocol = "a2ui/v0.8",
            operation = "createSurface",
            surface = new
            {
                id = SurfaceId(taskId),
                title,
                components = components.Select(MapComponent).ToArray()
            }
        };
    }

    /// <summary>
    /// Creates a rich nested layout version of the task surface.
    /// Drop-in replacement for the flat CreateSurface when richer layouts are desired.
    /// </summary>
    public static object CreateRichSurface(string taskId, string title, string description, TaskState status)
    {
        var components = new[]
        {
            GenUiComponent.VBox("task-layout",
                GenUiComponent.Text("task-title", $"Task: {title}", themeVariation: "HeaderLabel"),
                GenUiComponent.Text("task-status", $"Status: {status}"),
                GenUiComponent.Text("task-desc", description),
                GenUiComponent.Separator("task-sep"),
                GenUiComponent.HBox("task-actions",
                    GenUiComponent.Button("request-snapshot", "Request Snapshot", "request_snapshot"),
                    GenUiComponent.Button("refresh-surface", "Refresh Surface", "refresh_surface"),
                    GenUiComponent.Button("load-memory", "Load Memory", "load_memory")
                )
            )
        };
        return CreateSurface(taskId, "Swarm Task Monitor", components);
    }

    private static object MapComponent(GenUiComponent c)
    {
        var result = new Dictionary<string, object?>
        {
            ["id"] = c.Id,
            ["type"] = c.Type
        };
        if (c.Props is { Count: > 0 })
            result["props"] = c.Props;
        if (c.ThemeTypeVariation is not null)
            result["theme_type_variation"] = c.ThemeTypeVariation;
        if (c.Children is { Length: > 0 })
            result["children"] = c.Children.Select(MapComponent).ToArray();
        return result;
    }

    private static string SurfaceId(string taskId) => $"task-surface-{taskId}";
}
