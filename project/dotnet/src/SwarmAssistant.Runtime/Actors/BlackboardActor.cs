using Akka.Actor;

namespace SwarmAssistant.Runtime.Actors;

public sealed class BlackboardActor : ReceiveActor
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _boards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _globalBoard = new(StringComparer.Ordinal);

    public BlackboardActor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<BlackboardActor>();

        Receive<UpdateBlackboard>(Handle);
        Receive<GetBlackboardContext>(Handle);
        Receive<RemoveBlackboard>(Handle);

        // Global blackboard handlers for stigmergy
        Receive<UpdateGlobalBlackboard>(Handle);
        Receive<GetGlobalContext>(Handle);
    }

    private void Handle(UpdateBlackboard message)
    {
        if (!_boards.TryGetValue(message.TaskId, out var board))
        {
            board = new Dictionary<string, string>(StringComparer.Ordinal);
            _boards[message.TaskId] = board;
        }

        board[message.Key] = message.Value;

        _logger.LogDebug(
            "Blackboard updated taskId={TaskId} key={Key}",
            message.TaskId,
            message.Key);
    }

    private void Handle(GetBlackboardContext message)
    {
        if (_boards.TryGetValue(message.TaskId, out var board))
        {
            // Return a copy to avoid data races: AsReadOnly() shares the live instance
            Sender.Tell(new BlackboardContext(
                message.TaskId,
                new Dictionary<string, string>(board, StringComparer.Ordinal)));
        }
        else
        {
            Sender.Tell(new BlackboardContext(
                message.TaskId,
                new Dictionary<string, string>()));
        }
    }

    private void Handle(RemoveBlackboard message)
    {
        _boards.Remove(message.TaskId);
        _logger.LogDebug("Blackboard removed taskId={TaskId}", message.TaskId);
    }

    // Global blackboard handlers for stigmergy

    private void Handle(UpdateGlobalBlackboard message)
    {
        _globalBoard[message.Key] = message.Value;

        // Publish change to EventStream for cross-actor coordination
        Context.System.EventStream.Publish(new GlobalBlackboardChanged(message.Key, message.Value));

        _logger.LogDebug(
            "Global blackboard updated key={Key}",
            message.Key);
    }

    private void Handle(GetGlobalContext message)
    {
        // Return a copy to avoid data races
        Sender.Tell(new GlobalBlackboardContext(
            new Dictionary<string, string>(_globalBoard, StringComparer.Ordinal)));
    }
}
