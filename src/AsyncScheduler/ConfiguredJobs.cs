using System.ComponentModel.DataAnnotations;
using Hangfire;

namespace AsyncScheduler;

/// <summary>
/// Declarative front door: the top-level <c>Jobs</c> config array is upserted as recurring jobs at startup.
/// Startup is an idempotent upsert keyed on <c>Id</c>; config jobs carry no body and no headers.
/// A malformed entry fails startup (fail-fast) rather than being silently skipped.
/// </summary>
public static class ConfiguredJobs
{
    public static void Apply(IConfiguration configuration, IRecurringJobManager recurring, ILogger logger)
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
    }
}
