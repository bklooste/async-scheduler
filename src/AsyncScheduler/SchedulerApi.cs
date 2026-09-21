using System.Web;
using Hangfire;
using Microsoft.AspNetCore.Mvc;

namespace AsyncScheduler;

public static class SchedulerApi
{
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

        app.MapPost("/v1/jobs/http/enqueue", async (HttpRequest request, IBackgroundJobClient jobs,
            [FromQuery] int? delay, [FromQuery] string destinationUrl, [FromQuery] string method = "POST") =>
        {
            if (!IsHttpUrl(HttpUtility.UrlDecode(destinationUrl))) return BadUrl();
            var call = await ReadCall(request, HttpUtility.UrlDecode(destinationUrl), method);
            return Results.Ok(delay is null
                ? jobs.Enqueue<HttpCallbackSender>(s => s.Send(call, default))
                : jobs.Schedule<HttpCallbackSender>(s => s.Send(call, default), CeilToSecond(DateTimeOffset.UtcNow.AddSeconds(delay.Value))));
        })
        .Produces<string>().WithName("EnqueueJobV1").WithTags("Scheduler-V1");

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
}
