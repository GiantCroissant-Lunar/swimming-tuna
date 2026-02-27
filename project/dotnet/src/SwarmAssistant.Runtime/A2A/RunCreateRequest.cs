namespace SwarmAssistant.Runtime.A2A;

public sealed record RunCreateRequest(
    string? RunId = null,
    string? Title = null,
    string? Document = null,
    string? BaseBranch = null,
    string? BranchPrefix = null);
