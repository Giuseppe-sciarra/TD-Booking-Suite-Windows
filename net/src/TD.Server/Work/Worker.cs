using TD.Core.Data;
using TD.Core.Domain;

namespace TD.Server.Work;

/// <summary>The background work, one loop per job as the Python worker's threads: email and messages out, reminders,
/// the WordPress site, the waiting list, backups, the licence online, retention.</summary>
public sealed class Worker(Server s, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        WpSync.Install();
        var jobs = Jobs.All(s, log).Select(j => Loop(j, stop)).ToList();
        await Task.WhenAll(jobs);
    }

    async Task Loop(Job job, CancellationToken stop)
    {
        try { await Task.Delay(job.First, stop); } catch (TaskCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var wait = job.Every;
            try { wait = await job.Run(stop) ?? job.Every; }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "{Job}", job.Name); }
            try { await Task.Delay(wait, stop); } catch (TaskCanceledException) { return; }
        }
    }
}

/// <summary>A job of the worker: its first run after the start, how often, what it does (null keeps the period).</summary>
public sealed record Job(string Name, TimeSpan First, TimeSpan Every, Func<CancellationToken, Task<TimeSpan?>> Run);

public static partial class Jobs
{
    public static IEnumerable<Job> All(Server s, ILogger log)
    {
        var list = new List<Job>();
        Add(list, s, log);
        return list;
    }
    static partial void Add(List<Job> list, Server s, ILogger log);
}
