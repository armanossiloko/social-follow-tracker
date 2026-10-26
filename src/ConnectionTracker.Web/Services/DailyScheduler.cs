using ConnectionTracker.Core;
using ConnectionTracker.Web.Data;
using Microsoft.Extensions.Options;

namespace ConnectionTracker.Web.Services;

public sealed class SchedulerState
{
    public DateTimeOffset? NextRunAt { get; set; }
}

/// <summary>
/// Starts the daily run at <see cref="TrackerOptions.DailyAt"/> (container time zone) plus a random delay.
/// If the PC was off or Docker wasn't running at that time, the run happens shortly after startup instead.
/// </summary>
public sealed class DailyScheduler(
    JobRunner runner,
    IServiceScopeFactory scopes,
    SchedulerState state,
    IOptions<TrackerOptions> options,
    TimeProvider clock,
    ILogger<DailyScheduler> log) : BackgroundService
{
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterCrash = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var at = options.Value.DailyAt;
        var zone = TimeZoneInfo.Local;
        log.LogInformation("Daily run scheduled at {At} {Zone}", at, zone.Id);

        while (!stop.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset? lastRun;
                using (var scope = scopes.CreateScope())
                    lastRun = (await scope.ServiceProvider.GetRequiredService<SnapshotRepository>().LastRunAsync(RunKind.Daily, null, okOnly: false, stop))?.At;

                var now = clock.GetUtcNow();
                var due = RunSchedule.NextRun(now, lastRun, at, zone);
                due = due <= now
                    ? now + CatchUpDelay
                    : due + TimeSpan.FromMinutes(Random.Shared.NextDouble() * options.Value.RandomDelayMinutes);
                state.NextRunAt = due;
                log.LogInformation("Next daily run at {Due}", TimeZoneInfo.ConvertTime(due, zone));

                await Task.Delay(due - now, clock, stop);
                state.NextRunAt = null;
                await runner.RunAsync("Daily run", (jobs, ct) => jobs.DailyAsync(ct), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Usually the database being unreachable. Don't spin; try again in a while.
                log.LogError(e, "Daily run crashed; retrying in {Delay}", RetryAfterCrash);
                state.NextRunAt = clock.GetUtcNow() + RetryAfterCrash;
                try { await Task.Delay(RetryAfterCrash, clock, stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
