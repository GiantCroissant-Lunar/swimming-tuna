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

    private static string SurfaceId(string taskId) => $"task-surface-{taskId}";
}
