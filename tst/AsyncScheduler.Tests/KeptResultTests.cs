using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AsyncScheduler.Tests;

/// <summary>Job key, owner, kept result, per-job timeout, the async queue, content type and the result read.</summary>
[Collection("scheduler")]
public class KeptResultTests : IAsyncLifetime
{
    private Receiver receiver = null!;
    private WebApplicationFactory<Program>? factory;
    private HttpClient client = null!;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        receiver = await Receiver.StartAsync();
        Host();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        await receiver.DisposeAsync();
    }

    /// <summary>(Re)starts the scheduler with these settings.</summary>
    private void Host(params (string Key, string Value)[] settings)
    {
        factory?.Dispose();
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });
        client = factory.CreateClient();
    }

    private string Dest(string path) => Uri.EscapeDataString(receiver.BaseUrl + path);
    private static string NewKey() => Guid.NewGuid().ToString("N");

    private async Task<HttpResponseMessage> Enqueue(string path, string query, HttpContent? body = null) =>
        await client.PostAsync($"/v1/jobs/http/enqueue?destinationUrl={Dest(path)}&{query}", body ?? new StringContent("{}", Encoding.UTF8, "application/json"), Ct);

    private async Task<string> Kept(string path, string key, string extra = "", HttpContent? body = null)
    {
        var resp = await Enqueue(path, $"jobKey={key}&owner=alice&keepResult=true{extra}", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadAsStringAsync(Ct)).Trim('"');
    }

    private Task<HttpResponseMessage> Result(string key, string userId = "alice", int wait = 0) =>
        client.GetAsync($"/v1/jobs/{key}/result?userId={userId}&wait={wait}", Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;

    [Fact]
    public async Task A_repeated_job_key_returns_the_same_id_and_calls_once()
    {
        var key = NewKey();
        var first = await Kept("/dedupe", key);
        var second = await Kept("/dedupe", key);

        Assert.Equal(first, second);
        Assert.True(await Receiver.WaitFor(() => receiver.To("/dedupe").Any(), Patience));
        await Task.Delay(1500, Ct);
        Assert.Single(receiver.To("/dedupe"));
    }

    [Fact]
    public async Task A_reader_who_is_not_the_owner_gets_404_and_the_owner_gets_the_result()
    {
        var key = NewKey();
        await Kept("/owned", key);
        Assert.Equal(HttpStatusCode.OK, (await Result(key, wait: 10)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await Result(key, userId: "bob")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/v1/jobs/{key}/result", Ct)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_key_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Result(NewKey())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Result("not%20a%20key")).StatusCode);
    }

    [Fact]
    public async Task A_running_job_is_202_with_retry_after_and_its_state()
    {
        var key = NewKey();
        await Kept("/slow/3000", key);

        var resp = await Result(key);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.NotNull(resp.Headers.RetryAfter);
        var body = await Json(resp);
        Assert.Equal(key, body.GetProperty("handle").GetString());
        Assert.Contains(body.GetProperty("state").GetString(), new[] { "Enqueued", "Processing" });
    }

    [Fact]
    public async Task Wait_returns_as_soon_as_the_job_is_done()
    {
        var key = NewKey();
        await Kept("/slow/1500", key);

        var clock = Stopwatch.StartNew();
        var resp = await Result(key, wait: 20);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("slow done", await resp.Content.ReadAsStringAsync(Ct));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Wait_is_capped_at_MaxWaitSeconds_which_defaults_to_50()
    {
        Assert.Equal(50, new SchedulerOptions().MaxWaitSeconds);
        Host(("Scheduler:MaxWaitSeconds", "1"));
        var key = NewKey();
        await Kept("/slow/8000", key);

        var clock = Stopwatch.StartNew();
        var resp = await Result(key, wait: 60);

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task A_4xx_is_stored_as_final_and_not_retried()
    {
        var key = NewKey();
        await Kept("/status/422", key);

        var resp = await Result(key, wait: 10);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Equal("status 422", await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Completed", resp.Headers.GetValues(SchedulerApi.JobStateHeader).Single());
        await Task.Delay(3000, Ct);
        Assert.Single(receiver.To("/status/422"));
    }

    [Fact]
    public async Task A_5xx_is_retried_to_3_attempts_then_the_last_one_is_stored()
    {
        var key = NewKey();
        await Kept("/status/503", key);

        var resp = await Result(key, wait: 20);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("status 503", await resp.Content.ReadAsStringAsync(Ct));
        Assert.Equal(3, receiver.To("/status/503").Count());
        await Task.Delay(2500, Ct);
        Assert.Equal(3, receiver.To("/status/503").Count());
    }

    [Fact]
    public async Task Attempts_sets_how_many_tries_a_5xx_gets()
    {
        var key = NewKey();
        await Kept("/status/500", key, "&attempts=1");
        Assert.Equal(HttpStatusCode.InternalServerError, (await Result(key, wait: 10)).StatusCode);
        Assert.Single(receiver.To("/status/500"));
    }

    [Fact]
    public async Task A_body_over_MaxResultBytes_is_stored_as_ResultTooLarge()
    {
        Host(("Scheduler:MaxResultBytes", "1024"));
        var key = NewKey();
        await Kept("/big/5000", key);

        var resp = await Result(key, wait: 10);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("Failed", resp.Headers.GetValues(SchedulerApi.JobStateHeader).Single());
        var body = await Json(resp);
        Assert.Equal("ResultTooLarge", body.GetProperty("error").GetString());
        Assert.Single(receiver.To("/big/5000"));
    }

    [Fact]
    public async Task A_body_at_the_limit_is_kept()
    {
        Host(("Scheduler:MaxResultBytes", "1024"));
        var key = NewKey();
        await Kept("/big/1024", key);
        var resp = await Result(key, wait: 10);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1024, (await resp.Content.ReadAsByteArrayAsync(Ct)).Length);
    }

    [Fact]
    public async Task The_callers_content_type_is_sent_and_the_targets_is_kept()
    {
        var key = NewKey();
        await Kept("/echo", key, body: new StringContent("a,b\n1,2", Encoding.UTF8, "text/csv"));

        var resp = await Result(key, wait: 10);

        var sent = Assert.Single(receiver.To("/echo"));
        Assert.StartsWith("text/csv", sent.Headers["Content-Type"]);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/csv", resp.Content.Headers.ContentType!.ToString());
        Assert.Equal("a,b\n1,2", await resp.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_plain_job_also_sends_the_callers_content_type()
    {
        await Enqueue("/plain-ct", "", new StringContent("x=1", Encoding.UTF8, "application/x-www-form-urlencoded"));
        Assert.True(await Receiver.WaitFor(() => receiver.To("/plain-ct").Any(), Patience));
        Assert.StartsWith("application/x-www-form-urlencoded", receiver.To("/plain-ct").Single().Headers["Content-Type"]);
    }

    [Fact]
    public async Task A_per_job_timeout_ends_the_call_and_is_stored_as_Timeout()
    {
        var key = NewKey();
        await Kept("/slow/4000", key, "&timeout=1&attempts=1");

        var resp = await Result(key, wait: 10);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("Timeout", (await Json(resp)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_long_job_on_the_async_queue_does_not_delay_a_default_queue_job()
    {
        Host(("Scheduler:WorkerCount", "1"), ("Scheduler:AsyncWorkerCount", "1"));
        await Kept("/slow/6000/async", NewKey(), "&queue=async");
        Assert.True(await Receiver.WaitFor(() => receiver.To("/slow/6000/async").Any(), Patience));

        var sent = DateTimeOffset.UtcNow;
        await Enqueue("/quick", "");

        Assert.True(await Receiver.WaitFor(() => receiver.To("/quick").Any(), TimeSpan.FromSeconds(4)),
            "the default-queue job waited for the async one");
        Assert.True(receiver.To("/quick").Single().At - sent < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task An_async_queue_job_runs_on_the_async_queue()
    {
        var id = await Kept("/where", NewKey(), "&queue=async");
        Assert.True(await Receiver.WaitFor(() => receiver.To("/where").Any(), Patience));
        using var connection = factory!.Services.GetRequiredService<JobStorage>().GetConnection();
        Assert.Equal("async", connection.GetJobData(id)!.Job.Queue);
    }

    [Fact]
    public async Task Enqueue_with_no_new_parameters_behaves_as_0_2_7()
    {
        // Same job method, default queue, a JSON id back, JSON forced when no content type was sent, and a 4xx other
        // than 404 still failing the attempt so Hangfire retries it (kept-result jobs treat 4xx as final instead).
        var resp = await client.PostAsync($"/v1/jobs/http/enqueue?destinationUrl={Dest("/status/400")}", new ByteArrayContent("{}"u8.ToArray()), Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var id = await resp.Content.ReadFromJsonAsync<string>(Ct);

        Assert.True(await Receiver.WaitFor(() => receiver.To("/status/400").Any(), Patience));
        Assert.Equal("application/json", receiver.To("/status/400").Single().Headers["Content-Type"]);

        var storage = factory!.Services.GetRequiredService<JobStorage>();
        Assert.True(await Receiver.WaitFor(() =>
        {
            using var c = storage.GetConnection();
            return c.GetJobData(id!)?.State == "Scheduled" && c.GetJobParameter(id!, "RetryCount") == "1";
        }, Patience), "a 400 on a plain job should be retried");

        using var connection = storage.GetConnection();
        var job = connection.GetJobData(id!)!.Job;
        Assert.Equal(nameof(HttpCallbackSender.Send), job.Method.Name);
        Assert.Null(job.Queue);
    }

    [Fact]
    public async Task A_job_key_without_keepResult_dedupes_and_reports_state()
    {
        var key = NewKey();
        var first = (await (await Enqueue("/keyonly", $"jobKey={key}")).Content.ReadAsStringAsync(Ct)).Trim('"');
        var second = (await (await Enqueue("/keyonly", $"jobKey={key}")).Content.ReadAsStringAsync(Ct)).Trim('"');
        Assert.Equal(first, second);

        var resp = await Result(key, userId: "", wait: 10);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal("Succeeded", resp.Headers.GetValues(SchedulerApi.JobStateHeader).Single());
        Assert.Single(receiver.To("/keyonly"));
    }

    [Theory]
    [InlineData("keepResult=true")]                                            // no jobKey
    [InlineData("owner=alice")]                                                // no jobKey
    [InlineData("jobKey=a%2Fb&keepResult=true")]                               // key not URL-safe
    [InlineData("jobKey=k&resultTtl=60")]                                      // resultTtl without keepResult
    [InlineData("jobKey=k&keepResult=true&resultTtl=86401")]
    [InlineData("jobKey=k&keepResult=true&attempts=0")]
    [InlineData("jobKey=k&keepResult=true&attempts=11")]
    [InlineData("jobKey=k&attempts=2")]                                        // attempts without keepResult
    [InlineData("queue=critical")]
    [InlineData("timeout=0")]
    [InlineData("timeout=31")]                                                 // over CallbackTimeoutSeconds (30)
    public async Task Bad_new_parameters_are_400(string query)
    {
        var resp = await Enqueue("/never-called", query);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public void A_timeout_must_stay_10_s_under_the_invisibility_timeout()
    {
        // Startup already requires InvisibilityTimeoutSeconds >= CallbackTimeoutSeconds + 30; the check holds on its own.
        var o = new SchedulerOptions { CallbackTimeoutSeconds = 300, InvisibilityTimeoutSeconds = 305 };
        Assert.Null(SchedulerApi.ValidateEnqueue(o, null, null, false, null, 295, null, null));
        Assert.NotNull(SchedulerApi.ValidateEnqueue(o, null, null, false, null, 296, null, null));
    }
}
