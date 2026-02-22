using Akka.Actor;

namespace SwarmAssistant.Runtime.Actors;

public sealed class RuntimeActorRegistry
{
    private readonly object _lock = new();
    private IActorRef? _coordinator;

    public void SetCoordinator(IActorRef coordinator)
    {
        lock (_lock)
        {
            _coordinator = coordinator;
        }
    }

    public void ClearCoordinator()
    {
        lock (_lock)
        {
            _coordinator = null;
        }
    }

    public bool TryGetCoordinator(out IActorRef? coordinator)
    {
        lock (_lock)
        {
            coordinator = _coordinator;
            return coordinator is not null;
        }
    }
}
