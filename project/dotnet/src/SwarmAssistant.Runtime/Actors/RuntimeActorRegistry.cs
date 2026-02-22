using Akka.Actor;

namespace SwarmAssistant.Runtime.Actors;

public sealed class RuntimeActorRegistry
{
    private readonly object _lock = new();
    private IActorRef? _dispatcher;

    [Obsolete("Use SetDispatcher instead. Kept for backward compatibility.")]
    public void SetCoordinator(IActorRef coordinator) => SetDispatcher(coordinator);

    [Obsolete("Use ClearDispatcher instead. Kept for backward compatibility.")]
    public void ClearCoordinator() => ClearDispatcher();

    [Obsolete("Use TryGetDispatcher instead. Kept for backward compatibility.")]
    public bool TryGetCoordinator(out IActorRef? coordinator) => TryGetDispatcher(out coordinator);

    public void SetDispatcher(IActorRef dispatcher)
    {
        lock (_lock)
        {
            _dispatcher = dispatcher;
        }
    }

    public void ClearDispatcher()
    {
        lock (_lock)
        {
            _dispatcher = null;
        }
    }

    public bool TryGetDispatcher(out IActorRef? dispatcher)
    {
        lock (_lock)
        {
            dispatcher = _dispatcher;
            return dispatcher is not null;
        }
    }
}
