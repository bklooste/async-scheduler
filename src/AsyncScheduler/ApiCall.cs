namespace AsyncScheduler;

/// <summary>
/// The HTTP call a job performs. Serialised into the job store, so it is plain data (headers are
/// <c>string[]</c> rather than <c>StringValues</c>, keeping the stored JSON trivially round-trippable).
/// </summary>
public record ApiCall(byte[]? Body, string Url, Dictionary<string, string[]> Headers, string Method);
