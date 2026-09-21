using System.ComponentModel.DataAnnotations;

namespace AsyncScheduler;

/// <summary>
/// The service's whole configuration surface. Every property here MUST appear in the README
/// "Configuration" table under its env var name (<c>Service__PropertyName</c>) — the test
/// <c>ReadmeConfigTableTests</c> fails otherwise.
/// </summary>
public sealed class ServiceOptions
{
    public const string SectionName = "Service";

    /// <summary>Prefix for every Redis key this service writes, so several environments can share one Redis.</summary>
    [Required, RegularExpression(@"^[A-Za-z0-9:_-]+$")]
    public string KeyPrefix { get; set; } = "example";

    /// <summary>Redis connection string. Empty disables the Redis health check (quick-start mode).</summary>
    public string RedisConnectionString { get; set; } = "";
}
