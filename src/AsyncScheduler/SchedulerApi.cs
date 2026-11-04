using System.Text.RegularExpressions;
using System.Web;
using Hangfire;
using Hangfire.States;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AsyncScheduler;

public static partial class SchedulerApi
{
    public const string DefaultQueue = "default";
    public const string AsyncQueue = "async";
    public const int DefaultResultTtlSeconds = 3600;
    public const int MaxResultTtlSeconds = 86400;
    public const int MaxAttempts = 10;
    private const string RetryAfterSeconds = "1";
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    public static void MapSchedulerApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/jobs/http/schedule", async (HttpRequest request, IBackgroundJobClient jobs,
            [FromQuery] DateTime scheduledTime, [FromQuery] string destinationUrl, [FromQuery] string method = "POST") =>
        {
            if (!IsHttpUrl(HttpUtility.UrlDecode(destinationUrl))) return BadUrl();
            var call = await ReadCall(request, HttpUtility.UrlDecode(destinationUrl), method);
            return Results.Ok(jobs.Schedule<HttpCallbackSender>(s => s.Send(call, default), CeilToSecond(new DateTimeOffset(scheduledTime))));
        })
        .Produces<string>().WithName("ScheduleJobV1").WithTags("Scheduler-V1");

        app.MapPost("/v1/jobs/http/enqueue", async (HttpRequest request, IBackgroundJobClient jobs, ResultStore results,
            IOptions<SchedulerOptions> options, [FromQuery] int? delay, [FromQuery] string destinationUrl,
            [FromQuery] string method = "POST", [FromQuery] string? jobKey = null, [FromQuery] string? owner = null,
            [FromQuery] bool keepResult = false, [FromQuery] int? resultTtl = null, [FromQuery] int? timeout = null,
            [FromQuery] string? queue = null, [FromQuery] int? attempts = null) =>
        {
            if (!IsHttpUrl(HttpUtility.UrlDecode(destinationUrl))) return BadUrl();
            if (ValidateEnqueue(options.Value, jobKey, owner, keepResult, resultTtl, timeout, queue, attempts) is { } error)
                return Results.BadRequest(error);

            var call = await ReadCall(request, HttpUtility.UrlDecode(destinationUrl), method) with
            {
                JobKey = jobKey, Owner = owner ?? "", KeepResult = keepResult,
                ResultTtlSeconds = keepResult ? resultTtl ?? DefaultResultTtlSeconds : 0,
                TimeoutSeconds = timeout, Attempts = attempts,
            };
            string Create() => (keepResult, delay, queue is null or DefaultQueue) switch
            {
                // The 0.2 forms, unchanged: a plain job on the default queue.
                (false, null, true) => jobs.Enqueue<HttpCallbackSender>(s => s.Send(call, default)),
                (false, { } d, true) => jobs.Schedule<HttpCallbackSender>(s => s.Send(call, default), DueIn(d)),
                (false, null, false) => jobs.Enqueue<HttpCallbackSender>(queue, s => s.Send(call, default)),
                (false, { } d, false) => jobs.Schedule<HttpCallbackSender>(queue, s => s.Send(call, default), DueIn(d)),
                (true, null, _) => jobs.Enqueue<HttpCallbackSender>(queue ?? DefaultQueue, s => s.Run(call, null, default)),
                (true, { } d, _) => jobs.Schedule<HttpCallbackSender>(queue ?? DefaultQueue, s => s.Run(call, null, default), DueIn(d)),
            };
            return Results.Ok(jobKey is null
                ? Create()
                : results.GetOrCreate(jobKey, call.Owner, ResultStore.PendingTtl + TimeSpan.FromSeconds(delay ?? 0), Create));
        })
        .Produces<string>().WithName("EnqueueJobV1").WithTags("Scheduler-V1");

        app.MapGet("/v1/jobs/{jobKey}/result", async (HttpContext http, ResultStore results, JobStorage storage,
            IOptions<SchedulerOptions> options, [FromRoute] string jobKey, [FromQuery] string? userId, [FromQuery] int? wait) =>
        {
            if (!JobKeyPattern().IsMatch(jobKey)) return Results.NotFound();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, options.Value.MaxWaitSeconds));
            string? listeningTo = null;
            Task? signal = null;
            try
            {
                while (true)
                {
                    var record = results.Get(jobKey);
                    // Unknown key and someone else's key answer the same, so neither existence nor ownership leaks.
                    if (record is null || !string.Equals(record.Owner, userId ?? "", StringComparison.Ordinal))
                        return Results.NotFound();
                    if (record.Result is { } stored) return new StoredResultHttp(jobKey, stored);

                    var state = JobState(storage, record.JobId);
                    // Final without a kept result: a plain job that only used jobKey, or one Hangfire gave up on.
                    if (state is null || state == DeletedState.StateName) return Results.NotFound();
                    if (state == SucceededState.StateName) return JobStateResult(jobKey, state, StatusCodes.Status204NoContent);
                    if (state == FailedState.StateName) return JobStateResult(jobKey, state, StatusCodes.Status502BadGateway, "JobFailed");

                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero)
                    {
                        http.Response.Headers.RetryAfter = RetryAfterSeconds;
                        return JobStateResult(jobKey, state, StatusCodes.Status202Accepted);
                    }
                    if (listeningTo is null)
                    {
                        // Listen first, then re-read at once, so a change between the read and the listen is not missed.
                        signal = JobSignals.Next(listeningTo = record.JobId);
                        continue;
                    }
                    await Task.WhenAny(signal!, Task.Delay(left < OneSecond ? left : OneSecond, http.RequestAborted));
                    http.RequestAborted.ThrowIfCancellationRequested();
                    if (signal!.IsCompleted) signal = JobSignals.Next(listeningTo);
                }
            }
            finally
            {
                if (listeningTo is not null) JobSignals.Forget(listeningTo, signal!);
            }
        })
        .Produces(StatusCodes.Status202Accepted).Produces(StatusCodes.Status404NotFound)
        .WithName("GetJobResultV1").WithTags("Scheduler-V1");

        app.MapPost("/v1/jobs/http/recurring", async (HttpRequest request, IRecurringJobManager recurring,
            [FromQuery] string? name, [FromQuery] string? id, [FromQuery] string cron,
            [FromQuery] string destinationUrl, [FromQuery] string method = "POST") =>
        {
            if (!IsHttpUrl(destinationUrl)) return BadUrl();
            id ??= Guid.NewGuid().ToString();
            if (name != null) id = name + "-" + id;
            var call = await ReadCall(request, destinationUrl, method);
            recurring.AddOrUpdate<HttpCallbackSender>(id, s => s.Send(call, default), cron);
            return Results.Ok(id);
        })
        .Produces<string>().WithName("AddRecurringJobV1").WithTags("Scheduler-V1");

        app.MapDelete("/v1/jobs/background/{jobId}", (IBackgroundJobClient jobs, [FromRoute] string jobId) =>
            jobs.Delete(jobId) ? Results.Ok() : Results.NotFound())
        .Produces(StatusCodes.Status404NotFound).WithName("DeleteBackgroundJobV1").WithTags("Scheduler-V1");

        // Recurring jobs are keyed by id, not by a background job id, so they have their own delete.
        app.MapDelete("/v1/jobs/recurring/{id}", (IRecurringJobManager recurring, [FromRoute] string id) =>
        {
            recurring.RemoveIfExists(id);
            return Results.Ok();
        })
        .WithName("DeleteRecurringJobV1").WithTags("Scheduler-V1");
    }

    private static async Task<ApiCall> ReadCall(HttpRequest request, string url, string method)
    {
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms);
        return new ApiCall(ms.Length == 0 ? null : ms.ToArray(), url, request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? "").ToArray()), method);
    }

    /// <summary>
    /// Storage keeps due times in whole seconds and truncates, so a job due at 12:00:03.7 would fire at 12:00:03.0 —
    /// up to a second EARLY. A delayed callback must never run before its time, so round the due time UP instead.
    /// </summary>
    internal static DateTimeOffset CeilToSecond(DateTimeOffset t) =>
        new(t.UtcTicks + (TimeSpan.TicksPerSecond - t.UtcTicks % TimeSpan.TicksPerSecond) % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static IResult BadUrl() => Results.BadRequest("destinationUrl must be an absolute http(s) URL.");

    private static DateTimeOffset DueIn(int seconds) => CeilToSecond(DateTimeOffset.UtcNow.AddSeconds(seconds));

    /// <summary>A job key is URL-safe and short: it is a path segment on the result read and part of a store key.</summary>
    [GeneratedRegex("^[A-Za-z0-9._~-]{1,128}$")]
    private static partial Regex JobKeyPattern();

    public static string? ValidateEnqueue(SchedulerOptions o, string? jobKey, string? owner, bool keepResult,
        int? resultTtl, int? timeout, string? queue, int? attempts)
    {
        if (jobKey is not null && !JobKeyPattern().IsMatch(jobKey))
            return "jobKey must be 1-128 characters of A-Z a-z 0-9 . _ ~ -.";
        if (owner is not null && jobKey is null) return "owner needs a jobKey.";
        if (keepResult && jobKey is null) return "keepResult needs a jobKey.";
        if (resultTtl is not null && !keepResult) return "resultTtl needs keepResult=true.";
        if (resultTtl is < 1 or > MaxResultTtlSeconds) return $"resultTtl must be 1-{MaxResultTtlSeconds} seconds.";
        if (attempts is not null && !keepResult) return "attempts needs keepResult=true.";
        if (attempts is < 1 or > MaxAttempts) return $"attempts must be 1-{MaxAttempts}.";
        if (queue is not null && queue != DefaultQueue && queue != AsyncQueue) return $"queue must be '{DefaultQueue}' or '{AsyncQueue}'.";
        // A job still running when the invisibility timeout passes is re-queued and run a second time (Redis storage
        // sets the fetch time once and never extends it), so a per-job timeout keeps a margin under it.
        var max = Math.Min(o.CallbackTimeoutSeconds, o.InvisibilityTimeoutSeconds - 10);
        if (timeout is < 1 || timeout > max) return $"timeout must be 1-{max} seconds (CallbackTimeoutSeconds, and InvisibilityTimeoutSeconds less 10).";
        return null;
    }

    /// <summary>The Hangfire state of a job, with a retry wait reported as <c>Retrying</c>; null when the job is gone.</summary>
    private static string? JobState(JobStorage storage, string jobId)
    {
        using var connection = storage.GetConnection();
        var state = connection.GetJobData(jobId)?.State;
        if (state == ScheduledState.StateName && int.TryParse(connection.GetJobParameter(jobId, "RetryCount"), out var n) && n > 0)
            return "Retrying";
        return state;
    }

    private static IResult JobStateResult(string jobKey, string state, int status, string? error = null) =>
        new JobStateHttp(status, state, error is null ? new { handle = jobKey, state } : new { handle = jobKey, state, error });

    private sealed class JobStateHttp(int status, string state, object body) : IResult
    {
        public Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers[JobStateHeader] = state;
            return status == StatusCodes.Status204NoContent
                ? Task.FromResult(http.Response.StatusCode = status)
                : Results.Json(body, statusCode: status).ExecuteAsync(http);
        }
    }

    /// <summary>
    /// A stored outcome. <c>Completed</c>: the target's status, content type and body, verbatim. <c>Failed</c>:
    /// <c>502</c> with <c>{handle, state, error}</c>. Either way <c>X-Job-State</c> says which, so a caller can tell the
    /// target's own 502 from the scheduler's.
    /// </summary>
    private sealed class StoredResultHttp(string jobKey, StoredResult result) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            http.Response.Headers[JobStateHeader] = result.State;
            if (result.State != StoredResult.Completed)
            {
                await Results.Json(new { handle = jobKey, state = result.State, error = result.Error },
                    statusCode: StatusCodes.Status502BadGateway).ExecuteAsync(http);
                return;
            }
            http.Response.StatusCode = result.Status;
            if (result.ContentType is not null) http.Response.ContentType = result.ContentType;
            if (result.Body.Length > 0) await http.Response.Body.WriteAsync(result.Body, http.RequestAborted);
        }
    }

    public const string JobStateHeader = "X-Job-State";
}
