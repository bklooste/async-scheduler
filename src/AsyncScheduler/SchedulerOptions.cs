using System.ComponentModel.DataAnnotations;

namespace AsyncScheduler;

public enum StorageKind { InMemory, Redis, Postgres }

public enum DashboardAuth { Basic, None }

/// <summary>
/// The service's configuration surface. Every property must appear in the README config table under its
/// env var name (<c>Scheduler__PropertyName</c>) — <c>ReadmeConfigTableTests</c> enforces it.
/// </summary>
public sealed class SchedulerOptions : IValidatableObject
{
    public const string SectionName = "Scheduler";

    /// <summary><c>InMemory</c> (default; jobs are lost on restart — quick start / tests only), or durable <c>Redis</c> / <c>Postgres</c>.</summary>
    public StorageKind Storage { get; set; } = StorageKind.InMemory;

    /// <summary>Redis connection string. Required when <c>Storage=Redis</c>.</summary>
    public string RedisConnectionString { get; set; } = "";

    /// <summary>Npgsql connection string. Required when <c>Storage=Postgres</c>. Hangfire creates its own schema.</summary>
    public string PostgresConnectionString { get; set; } = "";

    /// <summary>
    /// Redis key prefix. MUST contain the literal <c>{hangfire}</c> hash tag (needed on clustered Redis) —
    /// deliberately not an interpolated string.
    /// </summary>
    public string RedisPrefix { get; set; } = "scheduler:{hangfire}:";

    /// <summary>Concurrent job executions per instance.</summary>
    [Range(1, 1000)]
    public int WorkerCount { get; set; } = 5;

    /// <summary>Seconds without a heartbeat before a worker is considered dead and its jobs are re-queued.</summary>
    [Range(5, 3600)]
    public int ServerTimeoutSeconds { get; set; } = 30;

    /// <summary>How often (seconds) due scheduled/recurring jobs are picked up. Bounds fire-lag.</summary>
    [Range(1, 300)]
    public int PollIntervalSeconds { get; set; } = 1;

    /// <summary>Per-callback HTTP timeout in seconds.</summary>
    [Range(1, 3600)]
    public int CallbackTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// After a worker crashes mid-job, how long (seconds) before the job is re-queued and run again. Must exceed
    /// <see cref="CallbackTimeoutSeconds"/> plus a margin, otherwise a healthy slow job would be re-run concurrently.
    /// Durable storages only.
    /// </summary>
    [Range(30, 86400)]
    public int InvisibilityTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// On startup, remove recurring jobs that an earlier start registered from <c>Jobs</c> config but that are no longer
    /// in it. Only ever touches config-registered jobs, never ones created through the API.
    /// </summary>
    public bool ReconcileConfigJobs { get; set; } = true;

    /// <summary>
    /// Serve the Hangfire dashboard at <c>/hangfire</c> (failure inspection, retry, trigger, delete). On by default,
    /// and never open: see <see cref="DashboardAuthMode"/> and <see cref="DashboardPassword"/>. Set false to remove it.
    /// </summary>
    public bool DashboardEnabled { get; set; } = true;

    /// <summary><c>Basic</c> (default) or <c>None</c> (you front it with your own auth proxy — the dashboard is then open to whoever reaches the port).</summary>
    public DashboardAuth DashboardAuthMode { get; set; } = DashboardAuth.Basic;

    /// <summary>Basic-auth user.</summary>
    public string DashboardUsername { get; set; } = "admin";

    /// <summary>
    /// Basic-auth password. If empty, a random one is generated at startup and written once to the log, so the
    /// out-of-the-box dashboard is never unauthenticated. Every replica generates its own; set this in real deployments.
    /// </summary>
    public string DashboardPassword { get; set; } = "";

    public IEnumerable<ValidationResult> Validate(ValidationContext _)
    {
        if (Storage == StorageKind.Redis && string.IsNullOrWhiteSpace(RedisConnectionString))
            yield return new("Scheduler:RedisConnectionString is required when Storage=Redis.", [nameof(RedisConnectionString)]);
        if (Storage == StorageKind.Postgres && string.IsNullOrWhiteSpace(PostgresConnectionString))
            yield return new("Scheduler:PostgresConnectionString is required when Storage=Postgres.", [nameof(PostgresConnectionString)]);
        if (InvisibilityTimeoutSeconds < CallbackTimeoutSeconds + 30)
            yield return new("Scheduler:InvisibilityTimeoutSeconds must be at least CallbackTimeoutSeconds + 30, " +
                             "or a healthy in-flight callback could be re-run concurrently.", [nameof(InvisibilityTimeoutSeconds)]);
        if (!RedisPrefix.Contains("{hangfire}", StringComparison.Ordinal))
            yield return new("Scheduler:RedisPrefix must contain the literal '{hangfire}' hash tag.", [nameof(RedisPrefix)]);
    }
}

/// <summary>One entry of the declarative top-level <c>Jobs</c> array: a recurring job upserted at startup.</summary>
public sealed class JobConfig
{
    [Required] public string Id { get; set; } = "";
    [Required] public string Cron { get; set; } = "";
    [Required] public string Url { get; set; } = "";
    public string Method { get; set; } = "POST";
}
