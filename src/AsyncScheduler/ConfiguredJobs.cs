using System.ComponentModel.DataAnnotations;
using Hangfire;
using Hangfire.Storage;

namespace AsyncScheduler;

/// <summary>
/// Declarative front door: the top-level <c>Jobs</c> config array is upserted as recurring jobs at startup.
/// Startup is an idempotent upsert keyed on <c>Id</c>; config jobs carry no body and no headers.
/// A malformed entry fails startup (fail-fast) rather than being silently skipped.
/// Reconciliation: the ids this class registered are remembered in the job store, so a job removed from config
/// is removed from the store on the next start. Jobs created through the API are never tracked, so never touched.
/// </summary>
public static class ConfiguredJobs
{
    private const string TrackingKey = "async-scheduler:config-jobs";

    public static void Apply(IConfiguration configuration, IRecurringJobManager recurring, JobStorage storage, ILogger logger,
        bool reconcile = true)
    {
        var jobs = configuration.GetSection("Jobs").Get<JobConfig[]>() ?? [];
        foreach (var job in jobs)
        {
            var results = new List<ValidationResult>();
            if (!Validator.TryValidateObject(job, new ValidationContext(job), results, true)
                || !Uri.TryCreate(job.Url, UriKind.Absolute, out _))
                throw new InvalidOperationException($"Invalid Jobs entry '{job.Id}': Id, Cron and an absolute Url are required.");

            var call = new ApiCall(null, job.Url, new Dictionary<string, string[]>(), job.Method);
            recurring.AddOrUpdate<HttpCallbackSender>(job.Id, s => s.Send(call, default), job.Cron);
            logger.LogInformation("Configured job {Id}: {Method} {Url} on {Cron}", job.Id, job.Method, job.Url, job.Cron);
        }

        using var connection = storage.GetConnection();
        var wanted = jobs.Select(j => j.Id).ToHashSet();
        var tracked = ReadTracked(connection);
        if (reconcile)
            foreach (var stale in tracked.Where(id => !wanted.Contains(id)))
            {
                recurring.RemoveIfExists(stale);
                logger.LogInformation("Removed job {Id}: no longer in Jobs config", stale);
            }

        // Remember exactly what config manages now (also when reconcile is off, so turning it on later is accurate).
        // A single hash field, not a set: the Redis storage writes sets as sorted sets but only reads plain sets.
        connection.SetRangeInHash(TrackingKey, [new("ids", System.Text.Json.JsonSerializer.Serialize(wanted.Order().ToArray()))]);
    }

    private static HashSet<string> ReadTracked(IStorageConnection connection)
    {
        var entries = connection.GetAllEntriesFromHash(TrackingKey);
        return entries is not null && entries.TryGetValue("ids", out var json) && !string.IsNullOrEmpty(json)
            ? System.Text.Json.JsonSerializer.Deserialize<string[]>(json)!.ToHashSet()
            : [];
    }
}
