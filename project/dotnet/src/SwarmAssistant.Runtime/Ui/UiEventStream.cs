using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SwarmAssistant.Runtime.Ui;

public sealed class UiEventStream
{
    private readonly ConcurrentDictionary<int, Channel<UiEventEnvelope>> _subscribers = new();
    private readonly Queue<UiEventEnvelope> _recentEvents = new();
    private readonly object _recentLock = new();
    private readonly ILogger<UiEventStream> _logger;
    private long _sequence;
    private int _nextSubscriberId;

    public UiEventStream(ILogger<UiEventStream>? logger = null)
    {
        _logger = logger ?? NullLogger<UiEventStream>.Instance;
    }

    public UiEventEnvelope Publish(string type, string? taskId, object payload)
    {
        var envelope = new UiEventEnvelope(
            Sequence: Interlocked.Increment(ref _sequence),
            Type: type,
            TaskId: taskId,
            At: DateTimeOffset.UtcNow,
            Payload: payload);

        lock (_recentLock)
        {
            _recentEvents.Enqueue(envelope);
            while (_recentEvents.Count > 200)
            {
                var dropped = _recentEvents.Dequeue();
                _logger.LogDebug(
                    "UiEventStream recent-events buffer full; dropped event type={Type} seq={Seq}",
                    dropped.Type,
                    dropped.Sequence);
            }
        }

        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Writer.TryWrite(envelope);
        }

        return envelope;
    }

    public IReadOnlyList<UiEventEnvelope> GetRecent(int count = 50)
    {
        lock (_recentLock)
        {
            var bounded = Math.Clamp(count, 1, 200);
            if (_recentEvents.Count <= bounded)
            {
                return _recentEvents.ToList();
            }

            return _recentEvents.Skip(_recentEvents.Count - bounded).ToList();
        }
    }

    public async IAsyncEnumerable<UiEventEnvelope> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<UiEventEnvelope>();
        var subscriberId = Interlocked.Increment(ref _nextSubscriberId);
        _subscribers[subscriberId] = channel;

        try
        {
            foreach (var recent in GetRecent())
            {
                channel.Writer.TryWrite(recent);
            }

            await foreach (var message in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return message;
            }
        }
        finally
        {
            _subscribers.TryRemove(subscriberId, out _);
            channel.Writer.TryComplete();
        }
    }
}
