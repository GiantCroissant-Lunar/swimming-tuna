using Akka.Actor;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Contracts.Messaging;

namespace SwarmAssistant.Runtime.Actors;

public sealed class CapabilityRegistryActor : ReceiveActor
{
    private readonly ILogger _logger;
    private readonly Dictionary<IActorRef, AgentCapabilityAdvertisement> _agents = new();

    public CapabilityRegistryActor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CapabilityRegistryActor>();

        Receive<AgentCapabilityAdvertisement>(HandleCapabilityAdvertisement);
        Receive<GetBestAgentForRole>(HandleGetBestAgentForRole);
        Receive<GetCapabilitySnapshot>(_ => Sender.Tell(new CapabilitySnapshot(_agents.Values.ToArray())));
        Receive<ExecuteRoleTask>(HandleExecuteRoleTask);
        Receive<Terminated>(HandleTerminated);
    }

    private void HandleCapabilityAdvertisement(AgentCapabilityAdvertisement message)
    {
        if (!_agents.ContainsKey(Sender))
        {
            Context.Watch(Sender);
        }

        _agents[Sender] = message;
    }

    private void HandleGetBestAgentForRole(GetBestAgentForRole message)
    {
        Sender.Tell(new BestAgentForRole(message.Role, SelectAgent(message.Role)));
    }

    private void HandleExecuteRoleTask(ExecuteRoleTask message)
    {
        var candidate = SelectAgent(message.Role);
        if (candidate is null)
        {
            var error = $"No capable swarm agent available for role {message.Role}";
            _logger.LogWarning(error);
            Sender.Tell(new RoleTaskFailed(
                message.TaskId,
                message.Role,
                error,
                DateTimeOffset.UtcNow));
            return;
        }

        if (_agents.TryGetValue(candidate, out var advertised))
        {
            _agents[candidate] = advertised with { CurrentLoad = advertised.CurrentLoad + 1 };
        }

        candidate.Tell(message, Sender);
    }

    private IActorRef? SelectAgent(SwarmRole role)
    {
        return _agents
            .Where(static pair => pair.Key != ActorRefs.Nobody)
            .Where(pair => pair.Value.Capabilities.Contains(role))
            .OrderBy(pair => pair.Value.CurrentLoad)
            .ThenBy(pair => pair.Value.ActorPath, StringComparer.Ordinal)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private void HandleTerminated(Terminated message)
    {
        _agents.Remove(message.ActorRef);
    }
}
