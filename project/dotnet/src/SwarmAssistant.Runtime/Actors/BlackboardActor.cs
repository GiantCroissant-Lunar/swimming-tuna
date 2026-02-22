using Akka.Actor;
using Microsoft.Extensions.Logging;

namespace SwarmAssistant.Runtime.Actors;

public sealed class BlackboardActor : ReceiveActor
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _boards = new(StringComparer.Ordinal);

    public BlackboardActor(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<BlackboardActor>();

        Receive<UpdateBlackboard>(Handle);
        Receive<GetBlackboardContext>(Handle);
        Receive<RemoveBlackboard>(Handle);
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
}
