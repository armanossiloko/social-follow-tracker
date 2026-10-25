namespace Following.Web.Services;

/// <summary>Runs jobs one at a time (scheduled run, Fetch now, uploads), each in its own DI scope.</summary>
public sealed class JobRunner(IServiceScopeFactory scopes, IHostApplicationLifetime lifetime, ILogger<JobRunner> log)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What is running right now, for the Settings page; null when idle.</summary>
    public string? Running { get; private set; }

    /// <summary>Waits for any running job, then runs this one.</summary>
    public async Task<T> RunAsync<T>(string what, Func<FollowJobs, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Running = what;
            using var scope = scopes.CreateScope();
            return await work(scope.ServiceProvider.GetRequiredService<FollowJobs>(), ct);
        }
        finally
        {
            Running = null;
            _gate.Release();
        }
    }

    /// <summary>Starts the job in the background, unless another one is running. Returns whether it started.</summary>
    public bool TryStart(string what, Func<FollowJobs, CancellationToken, Task> work)
    {
        if (!_gate.Wait(0)) return false;
        Running = what;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopes.CreateScope();
                await work(scope.ServiceProvider.GetRequiredService<FollowJobs>(), lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                log.LogError(e, "{What} failed", what);
            }
            finally
            {
                Running = null;
                _gate.Release();
            }
        });
        return true;
    }
}
