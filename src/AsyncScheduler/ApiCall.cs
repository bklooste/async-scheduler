namespace AsyncScheduler;

/// <summary>
/// The HTTP call a job performs. Serialised into the job store, so it is plain data (headers are
/// <c>string[]</c> rather than <c>StringValues</c>, keeping the stored JSON trivially round-trippable).
/// The optional properties were added in 0.3; jobs stored by older versions deserialise with their defaults,
/// which is exactly the old behaviour.
/// </summary>
public record ApiCall(byte[]? Body, string Url, Dictionary<string, string[]> Headers, string Method)
{
    /// <summary>The client's key for this job (<c>jobKey</c>). Set when the job is deduped or keeps a result.</summary>
    public string? JobKey { get; init; }

    /// <summary>Opaque owner (<c>owner</c>); a result is only returned to a reader passing the same <c>userId</c>.</summary>
    public string Owner { get; init; } = "";

    /// <summary>Store the final outcome under <see cref="JobKey"/> (<c>keepResult=true</c>).</summary>
    public bool KeepResult { get; init; }

    /// <summary>How long a kept result lives (<c>resultTtl</c>).</summary>
    public int ResultTtlSeconds { get; init; }

    /// <summary>Per-job HTTP timeout (<c>timeout</c>); null means <c>CallbackTimeoutSeconds</c>.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Total attempts for a kept-result job (<c>attempts</c>); null means the default.</summary>
    public int? Attempts { get; init; }
}
