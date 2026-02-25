using System.Security.Cryptography;
using System.Text;

namespace SwarmAssistant.Runtime.Tasks;

public static class TaskArtifactTypes
{
    public const string File = "file";
    public const string Design = "design";
    public const string Trace = "trace";
    public const string Message = "message";
}

public sealed record TaskArtifact(
    string ArtifactId,
    string RunId,
    string TaskId,
    string AgentId,
    string Type,
    string? Path,
    string ContentHash,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    private const int ArtifactIdPrefixLength = 24;

    public static string ComputeContentHash(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return ComputeContentHash(bytes);
    }

    public static string ComputeContentHash(byte[] content)
    {
        var hashBytes = SHA256.HashData(content);
        return $"sha256:{Convert.ToHexStringLower(hashBytes)}";
    }

    public static string BuildArtifactId(string contentHash)
    {
        var normalized = contentHash.StartsWith("sha256:", StringComparison.Ordinal)
            ? contentHash["sha256:".Length..]
            : contentHash;
        var prefixLength = Math.Min(ArtifactIdPrefixLength, normalized.Length);
        return $"art-{normalized[..prefixLength]}";
    }
}
