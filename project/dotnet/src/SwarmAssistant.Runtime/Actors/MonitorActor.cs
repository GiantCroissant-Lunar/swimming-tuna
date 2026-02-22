using Akka.Actor;
using Akka.Pattern;
using Microsoft.Extensions.Logging;
using SwarmAssistant.Runtime.Telemetry;

namespace SwarmAssistant.Runtime.Actors;

/// <summary>
/// Tier 2 fleet monitor that periodically checks system health.
/// Queries the supervisor for snapshot data, detects stalls (no progress
/// between consecutive ticks), and logs warnings for investigation.
/// </summary>
public sealed class MonitorActor : ReceiveActor
{
    private readonly IActorRef _supervisorActor;
    private readonly RuntimeTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly TimeSpan _tickInterval;

    private ICancelable? _tickScheduler;
    private SupervisorSnapshot? _previousSnapshot;
    private int _stallCount;

    public MonitorActor(
        IActorRef supervisorActor,
        ILoggerFactory loggerFactory,
        RuntimeTelemetry telemetry,
        int tickIntervalSeconds)
    {
        _supervisorActor = supervisorActor;
        _telemetry = telemetry;
        _logger = loggerFactory.CreateLogger<MonitorActor>();
        _tickInterval = TimeSpan.FromSeconds(Math.Max(5, tickIntervalSeconds));

        Receive<MonitorTick>(_ => OnTick());
        Receive<SupervisorSnapshot>(OnSnapshot);
    }

    protected override void PreStart()
    {
        base.PreStart();
        ScheduleNextTick();
        _logger.LogInformation(
            "MonitorActor started with tick interval {TickInterval}s",
            _tickInterval.TotalSeconds);
    }

    protected override void PostStop()
    {
        _tickScheduler?.Cancel();
        base.PostStop();
    }

    private void OnTick()
    {
        using var activity = _telemetry.StartActivity("monitor.tick");

        try
        {
            _supervisorActor.Tell(new GetSupervisorSnapshot());
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Monitor tick failed to query supervisor.");
        }

        ScheduleNextTick();
    }

    private void OnSnapshot(SupervisorSnapshot snapshot)
    {
        using var activity = _telemetry.StartActivity(
            "monitor.snapshot",
            tags: new Dictionary<string, object?>
            {
                ["swarm.tasks.started"] = snapshot.Started,
                ["swarm.tasks.completed"] = snapshot.Completed,
                ["swarm.tasks.failed"] = snapshot.Failed,
                ["swarm.tasks.escalations"] = snapshot.Escalations,
            });

        if (_previousSnapshot is not null)
        {
            var activeCurrent = snapshot.Started - snapshot.Completed - snapshot.Failed;

            // Stall detection: active tasks exist but no progress (no new completions or failures)
            if (activeCurrent > 0
                && snapshot.Completed == _previousSnapshot.Completed
                && snapshot.Failed == _previousSnapshot.Failed
                && snapshot.Started == _previousSnapshot.Started)
            {
                _stallCount++;
                _logger.LogWarning(
                    "Stall detected: {ActiveTasks} active tasks with no progress for {StallCount} ticks",
                    activeCurrent,
                    _stallCount);

                activity?.SetTag("monitor.stall_detected", true);
                activity?.SetTag("monitor.stall_count", _stallCount);
            }
            else
            {
                if (_stallCount > 0)
                {
                    _logger.LogInformation(
                        "Stall resolved after {StallCount} ticks",
                        _stallCount);
                }

                _stallCount = 0;
            }

            // Log if failure rate is high
            if (snapshot.Failed > _previousSnapshot.Failed)
            {
                var newFailures = snapshot.Failed - _previousSnapshot.Failed;
                _logger.LogWarning(
                    "New failures detected count={NewFailures} total={TotalFailed}",
                    newFailures,
                    snapshot.Failed);
            }
        }

        _previousSnapshot = snapshot;
    }

    private void ScheduleNextTick()
    {
        _tickScheduler = Context.System.Scheduler.ScheduleTellOnceCancelable(
            _tickInterval,
            Self,
            new MonitorTick(),
            Self);
    }
}
