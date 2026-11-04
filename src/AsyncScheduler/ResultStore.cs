using Hangfire;
using Hangfire.Storage;

namespace AsyncScheduler;

/// <summary>The final outcome of a kept-result job.</summary>
/// <param name="State"><c>Completed</c> (the target answered; <paramref name="Status"/> and body are its response) or
/// <c>Failed</c> (no usable response; <paramref name="Error"/> says why).</param>
public sealed record StoredResult(string State, int Status, string? ContentType, byte[] Body, string? Error)
{
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>What is stored under a job key: the Hangfire job it maps to, its owner, and the result once there is one.</summary>
public sealed record JobRecord(string JobId, string Owner, StoredResult? Result);

/// <summary>
/// Job keys and kept results, held in the job store itself (one hash per key, under the scheduler's own prefix on
/// Redis) so every storage kind works and every replica sees the same data. Each hash has a TTL.
/// </summary>
public sealed class ResultStore(JobStorage storage)
{
    private const string KeyPrefix = "async-scheduler:job-key:";
    private const string LockPrefix = "async-scheduler:job-key-lock:";

    /// <summary>How long a key lives before its job has a result (the dedupe window for a job that has not finished).</summary>
    public static readonly TimeSpan PendingTtl = TimeSpan.FromDays(1);

    /// <summary>
    /// Returns the job id already registered under <paramref name="key"/>, or calls <paramref name="create"/> and
    /// registers its id. Serialised per key with a distributed lock, so concurrent repeats across replicas create one job.
    /// </summary>
    public string GetOrCreate(string key, string owner, TimeSpan pendingTtl, Func<string> create)
    {
        using var connection = storage.GetConnection();
        using var _ = connection.AcquireDistributedLock(LockPrefix + key, TimeSpan.FromSeconds(10));

        var existing = connection.GetAllEntriesFromHash(KeyPrefix + key);
        if (existing is not null && existing.TryGetValue("jobId", out var id) && !string.IsNullOrEmpty(id))
            return id;

        // Owner first, with the TTL; the job id after the job exists. The job may finish (and write its result and
        // the result's TTL) before the second write, which therefore only adds a field and leaves the TTL alone.
        Write(connection, key, [new("owner", owner)], pendingTtl);
        var jobId = create();
        Write(connection, key, [new("jobId", jobId)], null);
        return jobId;
    }

    public JobRecord? Get(string key)
    {
        using var connection = storage.GetConnection();
        var h = connection.GetAllEntriesFromHash(KeyPrefix + key);
        if (h is null || !h.TryGetValue("jobId", out var jobId) || string.IsNullOrEmpty(jobId)) return null;

        StoredResult? result = null;
        if (h.TryGetValue("state", out var state) && !string.IsNullOrEmpty(state))
            result = new StoredResult(state,
                int.TryParse(h.GetValueOrDefault("status"), out var s) ? s : 0,
                NullIfEmpty(h.GetValueOrDefault("contentType")),
                Convert.FromBase64String(h.GetValueOrDefault("body") ?? ""),
                NullIfEmpty(h.GetValueOrDefault("error")));
        return new JobRecord(jobId, h.GetValueOrDefault("owner") ?? "", result);
    }

    /// <summary>Stores the final outcome and restarts the key's TTL at <paramref name="ttl"/>. Owner and job id are
    /// written again so the record is complete even if the pending entry expired while the job waited.</summary>
    public void Save(string key, string jobId, string owner, StoredResult result, TimeSpan ttl)
    {
        using var connection = storage.GetConnection();
        Write(connection, key,
        [
            new("jobId", jobId), new("owner", owner), new("state", result.State), new("status", result.Status.ToString()),
            new("contentType", result.ContentType ?? ""), new("body", Convert.ToBase64String(result.Body)),
            new("error", result.Error ?? ""),
        ], ttl);
    }

    private static void Write(IStorageConnection connection, string key, KeyValuePair<string, string>[] fields, TimeSpan? ttl)
    {
        using var tx = connection.CreateWriteTransaction();
        tx.SetRangeInHash(KeyPrefix + key, fields);
        if (ttl is { } t)
        {
            if (tx is not JobStorageTransaction expiring)
                throw new NotSupportedException("The job storage cannot expire hashes, so it cannot keep results.");
            expiring.ExpireHash(KeyPrefix + key, t);
        }
        tx.Commit();
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
