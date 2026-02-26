using Microsoft.Extensions.Logging.Abstractions;
using SwarmAssistant.Runtime.Langfuse;
using Xunit;

namespace SwarmAssistant.Runtime.Tests;

public sealed class LangfuseIntegrationTests
{
    [Fact]
    public async Task LangfuseScoreWriter_WriteReviewerVerdict_Approved_PostsScore1()
    {
        var fakeClient = new FakeLangfuseApiClient();
        var writer = new LangfuseScoreWriter(fakeClient, NullLogger<LangfuseScoreWriter>.Instance);

        await writer.WriteReviewerVerdictAsync("trace-123", "obs-456", approved: true, comment: "Looks good", CancellationToken.None);

        Assert.Single(fakeClient.PostedScores);
        var score = fakeClient.PostedScores[0];
        Assert.Equal("trace-123", score.TraceId);
        Assert.Equal("reviewer_verdict", score.Name);
        Assert.Equal(1.0, score.Value);
        Assert.Equal("obs-456", score.ObservationId);
        Assert.Equal("Looks good", score.Comment);
    }

    [Fact]
    public async Task LangfuseScoreWriter_WriteReviewerVerdict_Rejected_PostsScore0()
    {
        var fakeClient = new FakeLangfuseApiClient();
        var writer = new LangfuseScoreWriter(fakeClient, NullLogger<LangfuseScoreWriter>.Instance);

        await writer.WriteReviewerVerdictAsync("trace-789", "obs-101", approved: false, comment: null, CancellationToken.None);

        Assert.Single(fakeClient.PostedScores);
        var score = fakeClient.PostedScores[0];
        Assert.Equal("trace-789", score.TraceId);
        Assert.Equal("reviewer_verdict", score.Name);
        Assert.Equal(0.0, score.Value);
        Assert.Equal("obs-101", score.ObservationId);
        Assert.Null(score.Comment);
    }

    [Fact]
    public async Task LangfuseScoreWriter_WriteGatekeeperFixes_PostsCorrectCount()
    {
        var fakeClient = new FakeLangfuseApiClient();
        var writer = new LangfuseScoreWriter(fakeClient, NullLogger<LangfuseScoreWriter>.Instance);

        await writer.WriteGatekeeperFixCountAsync("trace-999", fixCount: 5, CancellationToken.None);

        Assert.Single(fakeClient.PostedScores);
        var score = fakeClient.PostedScores[0];
        Assert.Equal("trace-999", score.TraceId);
        Assert.Equal("gatekeeper_fixes", score.Name);
        Assert.Equal(5.0, score.Value);
        Assert.Null(score.ObservationId);
    }

    [Fact]
    public async Task LangfuseSimilarityQuery_NoTraces_ReturnsNull()
    {
        var fakeClient = new FakeLangfuseApiClient
        {
            TracesToReturn = Array.Empty<LangfuseTrace>()
        };
        var query = new LangfuseSimilarityQuery(fakeClient, NullLogger<LangfuseSimilarityQuery>.Instance);

        var result = await query.GetSimilarTaskContextAsync("Add unit tests", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task LangfuseSimilarityQuery_MatchingTraces_ReturnsFormattedContext()
    {
        var traces = new List<LangfuseTrace>
        {
            new LangfuseTrace(
                Id: "trace-1",
                Name: "Add unit tests for authentication",
                Metadata: new Dictionary<string, object?> { ["outcome"] = "success" },
                CreatedAt: DateTime.UtcNow.AddDays(-5)
            ),
            new LangfuseTrace(
                Id: "trace-2",
                Name: "Implement unit test framework",
                Metadata: new Dictionary<string, object?> { ["outcome"] = "failed" },
                CreatedAt: DateTime.UtcNow.AddDays(-3)
            ),
            new LangfuseTrace(
                Id: "trace-3",
                Name: "Refactor database schema",
                Metadata: new Dictionary<string, object?>(),
                CreatedAt: DateTime.UtcNow.AddDays(-1)
            )
        };

        var fakeClient = new FakeLangfuseApiClient
        {
            TracesToReturn = traces
        };
        var query = new LangfuseSimilarityQuery(fakeClient, NullLogger<LangfuseSimilarityQuery>.Instance);

        var result = await query.GetSimilarTaskContextAsync("Add unit tests for API", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains("Langfuse Learning Context", result);
        Assert.Contains("Add unit tests for authentication", result);
        Assert.Contains("success", result);
        Assert.Contains("Implement unit test framework", result);
        Assert.Contains("failed", result);
    }

    [Fact]
    public async Task LangfuseScoreWriter_WriteReviewerVerdict_WhenApiThrows_LogsErrorAndContinues()
    {
        var fakeClient = new FakeLangfuseApiClient { ShouldThrow = true };
        var writer = new LangfuseScoreWriter(fakeClient, NullLogger<LangfuseScoreWriter>.Instance);

        await writer.WriteReviewerVerdictAsync("trace-error", "obs-error", true, null, CancellationToken.None);

        Assert.Empty(fakeClient.PostedScores);
    }

    [Fact]
    public async Task LangfuseScoreWriter_WriteGatekeeperFixCount_WhenApiThrows_LogsErrorAndContinues()
    {
        var fakeClient = new FakeLangfuseApiClient { ShouldThrow = true };
        var writer = new LangfuseScoreWriter(fakeClient, NullLogger<LangfuseScoreWriter>.Instance);

        await writer.WriteGatekeeperFixCountAsync("trace-error", 3, CancellationToken.None);

        Assert.Empty(fakeClient.PostedScores);
    }

    [Fact]
    public async Task LangfuseScoreWriter_WriteReviewerVerdict_WithComment_IncludesComment()
    {
        var fakeClient = new FakeLangfuseApiClient();
        var writer = new LangfuseScoreWriter(fakeClient, NullLogger<LangfuseScoreWriter>.Instance);

        await writer.WriteReviewerVerdictAsync("trace-123", "obs-456", true, "Looks good!", CancellationToken.None);

        Assert.Single(fakeClient.PostedScores);
        var score = fakeClient.PostedScores[0];
        Assert.Equal("Looks good!", score.Comment);
    }

    private sealed class FakeLangfuseApiClient : ILangfuseApiClient
    {
        public List<LangfuseScore> PostedScores { get; } = new();
        public List<LangfuseComment> PostedComments { get; } = new();
        public IReadOnlyList<LangfuseTrace> TracesToReturn { get; set; } = Array.Empty<LangfuseTrace>();
        public bool ShouldThrow { get; set; }

        public Task PostScoreAsync(LangfuseScore score, CancellationToken ct)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated API failure");
            }
            PostedScores.Add(score);
            return Task.CompletedTask;
        }

        public Task<LangfuseTraceList> GetTracesAsync(LangfuseTraceQuery query, CancellationToken ct)
        {
            return Task.FromResult(new LangfuseTraceList(TracesToReturn));
        }

        public Task PostCommentAsync(LangfuseComment comment, CancellationToken ct)
        {
            PostedComments.Add(comment);
            return Task.CompletedTask;
        }
    }
}
