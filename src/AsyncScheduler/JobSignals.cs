using System.Collections.Concurrent;
using Hangfire.States;
using Hangfire.Storage;

namespace AsyncScheduler;

/// <summary>
/// In-process "this job's state changed" signal, so a result read with <c>wait</c> returns as soon as the job finishes
/// rather than on its next poll. Only state changes applied in this process are seen; readers also re-read the store
/// every second, which covers jobs run by another replica and a signal raised before the state is committed.
/// </summary>
public static class JobSignals
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Waiting = new();

    /// <summary>A task that completes on the next state change of <paramref name="jobId"/>. Shared by concurrent readers.</summary>
    public static Task Next(string jobId) =>
        Waiting.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    public static void Raise(string jobId)
    {
        if (Waiting.TryRemove(jobId, out var tcs)) tcs.TrySetResult();
    }

    /// <summary>Drops the entry for <paramref name="jobId"/> if it is still <paramref name="task"/> (the reader gave up).</summary>
    public static void Forget(string jobId, Task task)
    {
        if (Waiting.TryGetValue(jobId, out var tcs) && tcs.Task == task)
            ((ICollection<KeyValuePair<string, TaskCompletionSource>>)Waiting).Remove(new(jobId, tcs));
    }
}

/// <summary>Hangfire state filter that raises <see cref="JobSignals"/> on every state applied in this process.</summary>
public sealed class JobStateSignalFilter : IApplyStateFilter
{
    public static readonly JobStateSignalFilter Instance = new();

    public void OnStateApplied(ApplyStateContext context, IWriteOnlyTransaction transaction) =>
        JobSignals.Raise(context.BackgroundJob.Id);

    public void OnStateUnapplied(ApplyStateContext context, IWriteOnlyTransaction transaction) { }
}
