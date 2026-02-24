using System.Text.Json;

namespace SwarmAssistant.Runtime.Tasks;

internal static class TaskArtifactJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
