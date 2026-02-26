using Akka.Actor;

namespace SwarmAssistant.Runtime.Actors;

public sealed record ConsensusVote(
    string TaskId,
    string VoterId,
    bool Approved,
    double Confidence,
    string Feedback
);

public sealed record ConsensusRequest(
    string TaskId,
    string Artifact,
    int RequiredVotes,
    string Strategy = "majority"
);

public sealed record ConsensusResult(
    string TaskId,
    bool Approved,
    IReadOnlyList<ConsensusVote> Votes
);

public sealed record CancelConsensusSession(string TaskId);

internal sealed record ConsensusSessionTimeout(string TaskId);

public sealed class ConsensusActor : ReceiveActor, IWithTimers
{
    private readonly ILogger<ConsensusActor> _logger;
    private readonly Dictionary<string, ConsensusSession> _activeSessions = new();

    // Buffer votes that arrive before the ConsensusRequest is processed (race condition guard)
    private readonly Dictionary<string, List<ConsensusVote>> _pendingVotes = new();

    public ITimerScheduler Timers { get; set; } = null!;

    public ConsensusActor(ILogger<ConsensusActor> logger)
    {
        _logger = logger;
        Receive<ConsensusRequest>(HandleRequest);
        Receive<ConsensusVote>(HandleVote);
        Receive<CancelConsensusSession>(HandleCancel);
        Receive<ConsensusSessionTimeout>(HandleSessionTimeout);
    }

    private void HandleRequest(ConsensusRequest request)
    {
        if (_activeSessions.ContainsKey(request.TaskId))
        {
            _logger.LogWarning("Consensus session already active for task {TaskId}", request.TaskId);
            return;
        }

        _logger.LogInformation("Starting consensus session for task {TaskId}, requiring {RequiredVotes} votes, strategy: {Strategy}", request.TaskId, request.RequiredVotes, request.Strategy);
        _activeSessions[request.TaskId] = new ConsensusSession(request, Sender);

        Timers.StartSingleTimer(
            $"consensus-timeout-{request.TaskId}",
            new ConsensusSessionTimeout(request.TaskId),
            TimeSpan.FromMinutes(5));

        // Replay any votes that arrived before the session was created
        if (_pendingVotes.Remove(request.TaskId, out var buffered))
        {
            foreach (var v in buffered)
            {
                HandleVote(v);
            }
        }
    }

    private void HandleVote(ConsensusVote vote)
    {
        if (!_activeSessions.TryGetValue(vote.TaskId, out var session))
        {
            // Session not yet created — buffer the vote to avoid losing it in the race
            _logger.LogDebug("Buffering early vote from {VoterId} for task {TaskId}", vote.VoterId, vote.TaskId);
            if (!_pendingVotes.TryGetValue(vote.TaskId, out var pending))
            {
                pending = new List<ConsensusVote>();
                _pendingVotes[vote.TaskId] = pending;
            }
            pending.Add(vote);
            return;
        }

        _logger.LogInformation("Received vote from {VoterId} for task {TaskId}: Approved={Approved}, Confidence={Confidence}", vote.VoterId, vote.TaskId, vote.Approved, vote.Confidence);

        if (!session.VoterIds.Add(vote.VoterId))
        {
            _logger.LogWarning("Duplicate vote from {VoterId} for task {TaskId} ignored", vote.VoterId, vote.TaskId);
            return;
        }

        // Clamp confidence to [0, 1] to prevent negative values from inverting weighted results
        var sanitizedVote = vote with { Confidence = Math.Clamp(vote.Confidence, 0.0, 1.0) };
        session.Votes.Add(sanitizedVote);

        if (session.Votes.Count >= session.Request.RequiredVotes)
        {
            EvaluateConsensus(vote.TaskId, session);
        }
    }

    private void HandleCancel(CancelConsensusSession message)
    {
        _pendingVotes.Remove(message.TaskId);
        if (_activeSessions.Remove(message.TaskId))
        {
            _logger.LogInformation("Consensus session cancelled for task {TaskId}", message.TaskId);
        }
    }

    private void HandleSessionTimeout(ConsensusSessionTimeout message)
    {
        if (_activeSessions.TryGetValue(message.TaskId, out var session))
        {
            _logger.LogWarning("Consensus session timed out for task {TaskId}", message.TaskId);
            EvaluateConsensus(message.TaskId, session);
        }
    }

    private void EvaluateConsensus(string taskId, ConsensusSession session)
    {
        _activeSessions.Remove(taskId);

        if (session.Votes.Count == 0)
        {
            _logger.LogError("Consensus failed for task {TaskId}: No votes received", taskId);
            session.ReplyTo.Tell(new ConsensusResult(taskId, false, []));
            return;
        }

        bool approved = session.Request.Strategy.ToLowerInvariant() switch
        {
            "unanimous" => session.Votes.All(v => v.Approved),
            "weighted" => EvaluateWeighted(session.Votes),
            _ => EvaluateMajority(session.Votes) // default majority
        };

        _logger.LogInformation("Consensus reached for task {TaskId}: Approved={Approved} ({VoteCount} votes)", taskId, approved, session.Votes.Count);
        session.ReplyTo.Tell(new ConsensusResult(taskId, approved, session.Votes));
    }

    private static bool EvaluateMajority(List<ConsensusVote> votes)
    {
        int approvals = votes.Count(v => v.Approved);
        return approvals > votes.Count / 2;
    }

    private static bool EvaluateWeighted(List<ConsensusVote> votes)
    {
        double approvalWeight = votes.Where(v => v.Approved).Sum(v => v.Confidence);
        double rejectionWeight = votes.Where(v => !v.Approved).Sum(v => v.Confidence);
        return approvalWeight > rejectionWeight;
    }

    private class ConsensusSession
    {
        public ConsensusRequest Request { get; }
        public IActorRef ReplyTo { get; }
        public List<ConsensusVote> Votes { get; } = new();
        public HashSet<string> VoterIds { get; } = new(StringComparer.Ordinal);

        public ConsensusSession(ConsensusRequest request, IActorRef replyTo)
        {
            Request = request;
            ReplyTo = replyTo;
        }
    }
}
