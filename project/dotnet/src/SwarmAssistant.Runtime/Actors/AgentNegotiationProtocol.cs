using Akka.Actor;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

public sealed record NegotiationOffer(string TaskId, SwarmRole Role, string FromAgent);

public sealed record NegotiationAccept(string TaskId, string FromAgent);

public sealed record NegotiationReject(string TaskId, string Reason, string FromAgent);

public sealed record HelpRequest(string TaskId, string Description, string FromAgent);

public sealed record HelpResponse(string TaskId, string Output, string FromAgent);

public sealed record ContractNetCallForProposals(
    string TaskId,
    SwarmRole Role,
    string Description,
    TimeSpan Timeout);

public sealed record ContractNetBid(
    Guid AuctionId,
    string TaskId,
    SwarmRole Role,
    string FromAgent,
    int EstimatedCost,
    int EstimatedTimeMs);

public sealed record ContractNetAward(
    Guid AuctionId,
    string TaskId,
    SwarmRole Role,
    string AwardedAgent);

public sealed record ContractNetNoBid(
    Guid AuctionId,
    string TaskId,
    SwarmRole Role,
    string Reason);

internal sealed record ContractNetBidRequest(
    Guid AuctionId,
    string TaskId,
    SwarmRole Role,
    string Description,
    DateTimeOffset DeadlineUtc);

internal sealed record ContractNetFinalize(Guid AuctionId, string TaskId, SwarmRole Role);
