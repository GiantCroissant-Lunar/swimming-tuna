using System.Text.Json.Serialization;

namespace SwarmAssistant.Runtime.Memvid;

/// <summary>Input document for the memvid <c>put</c> command.</summary>
public sealed record MemvidDocument(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata = null
);

/// <summary>Individual search result from the <c>find</c> command.</summary>
public sealed record MemvidResult(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("score")] double Score
);

/// <summary>Envelope returned by <c>find</c>.</summary>
public sealed record MemvidFindResponse(
    [property: JsonPropertyName("results")] List<MemvidResult> Results
);

/// <summary>Response from the <c>create</c> command.</summary>
public sealed record MemvidCreateResponse(
    [property: JsonPropertyName("created")] string Created
);

/// <summary>Response from the <c>put</c> command.</summary>
public sealed record MemvidPutResponse(
    [property: JsonPropertyName("frame_id")] int FrameId
);

/// <summary>Individual entry from the <c>timeline</c> command.</summary>
public sealed record MemvidTimelineEntry(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("text")] string Text
);

/// <summary>Envelope returned by <c>timeline</c>.</summary>
public sealed record MemvidTimelineResponse(
    [property: JsonPropertyName("entries")] List<MemvidTimelineEntry> Entries
);

/// <summary>Response from the <c>info</c> command.</summary>
public sealed record MemvidInfoResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("frames")] int Frames,
    [property: JsonPropertyName("size_bytes")] long SizeBytes
);

/// <summary>Error envelope returned on stderr when the CLI exits with code 1.</summary>
public sealed record MemvidErrorResponse(
    [property: JsonPropertyName("error")] string Error
);

/// <summary>Source-generated JSON serializer context for all memvid types.</summary>
[JsonSerializable(typeof(MemvidDocument))]
[JsonSerializable(typeof(MemvidResult))]
[JsonSerializable(typeof(MemvidFindResponse))]
[JsonSerializable(typeof(MemvidCreateResponse))]
[JsonSerializable(typeof(MemvidPutResponse))]
[JsonSerializable(typeof(MemvidTimelineEntry))]
[JsonSerializable(typeof(MemvidTimelineResponse))]
[JsonSerializable(typeof(MemvidInfoResponse))]
[JsonSerializable(typeof(MemvidErrorResponse))]
internal sealed partial class MemvidJsonContext : JsonSerializerContext;
