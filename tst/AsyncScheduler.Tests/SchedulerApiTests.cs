using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AsyncScheduler.Tests;

// One collection: Hangfire keeps process-wide static storage, so hosts must not overlap.
[CollectionDefinition("scheduler", DisableParallelization = true)]
public class SchedulerCollection;

[Collection("scheduler")]
public class SchedulerApiTests : IAsyncLifetime
{
    private Receiver receiver = null!;
    private WebApplicationFactory<Program> factory = null!;
    private HttpClient client = null!;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    public async ValueTask InitializeAsync()
    {
        receiver = await Receiver.StartAsync();
        factory = new WebApplicationFactory<Program>();
        client = factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await factory.DisposeAsync();
        await receiver.DisposeAsync();
    }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
    private string Dest(string path) => Uri.EscapeDataString(receiver.BaseUrl + path);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Health_is_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Enqueue_calls_back_with_body_and_forwards_headers()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/http/enqueue?destinationUrl={Dest("/hook")}") { Content = Json("{\"a\":1}") };
        req.Headers.Add("X-Trace", "abc");
        var resp = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.True(await Receiver.WaitFor(() => receiver.To("/hook").Any(), Patience));
        var call = receiver.To("/hook").Single();
        Assert.Equal("POST", call.Method);
        Assert.Equal("{\"a\":1}", call.Body);
        Assert.Equal("abc", call.Headers["X-Trace"]);
    }

    [Fact]
    public async Task Enqueue_honours_method()
    {
        await client.PostAsync($"/v1/jobs/http/enqueue?destinationUrl={Dest("/put")}&method=PUT", Json("{}"), Ct);
        Assert.True(await Receiver.WaitFor(() => receiver.To("/put").Any(), Patience));
        Assert.Equal("PUT", receiver.To("/put").Single().Method);
    }

    [Fact]
    public async Task Enqueue_with_delay_does_not_fire_early()
    {
        var sent = DateTimeOffset.UtcNow;
        await client.PostAsync($"/v1/jobs/http/enqueue?delay=3&destinationUrl={Dest("/delayed")}", Json("{}"), Ct);

        await Task.Delay(1500, Ct);
        Assert.Empty(receiver.To("/delayed"));
        Assert.True(await Receiver.WaitFor(() => receiver.To("/delayed").Any(), Patience));
        Assert.True(receiver.To("/delayed").Single().At - sent >= TimeSpan.FromSeconds(2.5));
    }

    [Fact]
    public async Task A_delayed_job_never_fires_before_its_due_time()
    {
        // Storage keeps due times in whole seconds; a job due at 12:00:03.7 used to fire at 12:00:03.0.
        // Stagger the posts so the sub-second offsets differ, then require every callback to be at/after post + delay.
        var posted = new Dictionary<string, DateTimeOffset>();
        for (var i = 0; i < 8; i++)
        {
            posted[$"/early/{i}"] = DateTimeOffset.UtcNow;
            await client.PostAsync($"/v1/jobs/http/enqueue?delay=2&destinationUrl={Dest($"/early/{i}")}", Json("{}"), Ct);
            await Task.Delay(130, Ct);
        }
        Assert.True(await Receiver.WaitFor(() => receiver.Calls.Count(c => c.Path.StartsWith("/early/")) >= 8, Patience));

        var early = receiver.Calls.Where(c => posted.ContainsKey(c.Path))
            .Where(c => c.At < posted[c.Path].AddSeconds(2))
            .Select(c => $"{c.Path} fired {(posted[c.Path].AddSeconds(2) - c.At).TotalMilliseconds:F0} ms early").ToList();
        Assert.True(early.Count == 0, string.Join("; ", early));
    }

    [Fact]
    public async Task A_job_scheduled_for_a_time_never_fires_before_it()
    {
        var results = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var due = DateTimeOffset.UtcNow.AddSeconds(2).AddMilliseconds(i * 170 + 40);
            await client.PostAsync($"/v1/jobs/http/schedule?scheduledTime={Uri.EscapeDataString(due.UtcDateTime.ToString("o"))}&destinationUrl={Dest($"/at/{i}")}", Json("{}"), Ct);
            Assert.True(await Receiver.WaitFor(() => receiver.To($"/at/{i}").Any(), Patience));
            var arrived = receiver.To($"/at/{i}").Single().At;
            if (arrived < due) results.Add($"/at/{i} fired {(due - arrived).TotalMilliseconds:F0} ms early");
        }
        Assert.True(results.Count == 0, string.Join("; ", results));
    }

    [Fact]
    public async Task Schedule_fires_at_the_given_time()
    {
        var at = DateTime.UtcNow.AddSeconds(3).ToString("o");
        await client.PostAsync($"/v1/jobs/http/schedule?scheduledTime={Uri.EscapeDataString(at)}&destinationUrl={Dest("/sched")}", Json("{}"), Ct);
        Assert.True(await Receiver.WaitFor(() => receiver.To("/sched").Any(), Patience));
    }

    [Fact]
    public async Task Recurring_fires_repeatedly_and_can_be_deleted()
    {
        var resp = await client.PostAsync(
            $"/v1/jobs/http/recurring?name=tick&id=1&cron={Uri.EscapeDataString("*/2 * * * * *")}&destinationUrl={Dest("/rec")}", null, Ct);
        Assert.Equal("tick-1", (await resp.Content.ReadAsStringAsync(Ct)).Trim('"'));

        Assert.True(await Receiver.WaitFor(() => receiver.To("/rec").Count() >= 2, Patience));

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/v1/jobs/recurring/tick-1", Ct)).StatusCode);
        await Task.Delay(1500, Ct);   // let any in-flight run finish
        var after = receiver.To("/rec").Count();
        await Task.Delay(4500, Ct);
        Assert.Equal(after, receiver.To("/rec").Count());
    }

    [Fact]
    public async Task Delete_of_a_queued_job_is_200_and_it_never_fires_and_missing_is_404()
    {
        var resp = await client.PostAsync($"/v1/jobs/http/enqueue?delay=3&destinationUrl={Dest("/never")}", Json("{}"), Ct);
        var id = (await resp.Content.ReadAsStringAsync(Ct)).Trim('"');

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/v1/jobs/background/{id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/v1/jobs/background/999999", Ct)).StatusCode);

        await Task.Delay(5000, Ct);
        Assert.Empty(receiver.To("/never"));
    }

    [Fact]
    public async Task Target_returning_404_is_treated_as_done_not_retried()
    {
        await client.PostAsync($"/v1/jobs/http/enqueue?destinationUrl={Dest("/gone")}", Json("{}"), Ct);
        Assert.True(await Receiver.WaitFor(() => receiver.To("/gone").Any(), Patience));
        await Task.Delay(3000, Ct);
        Assert.Single(receiver.To("/gone"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("file:///etc/passwd")]
    public async Task Non_http_destination_is_rejected(string url)
    {
        var resp = await client.PostAsync($"/v1/jobs/http/enqueue?destinationUrl={Uri.EscapeDataString(url)}", Json("{}"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Dashboard_is_off_by_default()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/hangfire", Ct)).StatusCode);
    }
}

[Collection("scheduler")]
public class ConfiguredJobsTests
{
    [Fact]
    public async Task Jobs_from_config_are_registered_at_startup_and_fire()
    {
        await using var receiver = await Receiver.StartAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Jobs:0:Id", "cfg-hello");
            b.UseSetting("Jobs:0:Cron", "*/2 * * * * *");
            b.UseSetting("Jobs:0:Url", receiver.BaseUrl + "/cfg");
            b.UseSetting("Jobs:0:Method", "GET");
        });
        using var client = factory.CreateClient();

        Assert.True(await Receiver.WaitFor(() => receiver.To("/cfg").Count() >= 1, TimeSpan.FromSeconds(15)));
        var call = receiver.To("/cfg").First();
        Assert.Equal("GET", call.Method);
        Assert.Equal("", call.Body);
    }

    [Fact]
    public void A_malformed_job_entry_fails_startup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Jobs:0:Id", "bad");
            b.UseSetting("Jobs:0:Cron", "* * * * *");
            b.UseSetting("Jobs:0:Url", "not a url");
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}

[Collection("scheduler")]
public class DashboardAndConfigTests
{
    private static WebApplicationFactory<Program> With(params (string, string)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => { foreach (var (k, v) in settings) b.UseSetting(k, v); });

    [Fact]
    public async Task Dashboard_requires_basic_credentials_when_enabled()
    {
        await using var factory = With(("Scheduler:DashboardEnabled", "true"), ("Scheduler:DashboardUsername", "ops"), ("Scheduler:DashboardPassword", "s3cret"));
        using var client = factory.CreateClient();

        var anon = await client.GetAsync("/hangfire", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Contains("Basic", anon.Headers.WwwAuthenticate.ToString());

        var bad = new HttpRequestMessage(HttpMethod.Get, "/hangfire");
        bad.Headers.Authorization = new("Basic", Convert.ToBase64String("ops:wrong"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(bad, TestContext.Current.CancellationToken)).StatusCode);

        var good = new HttpRequestMessage(HttpMethod.Get, "/hangfire/");
        good.Headers.Authorization = new("Basic", Convert.ToBase64String("ops:s3cret"u8.ToArray()));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(good, TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public void Enabling_the_dashboard_without_credentials_fails_startup()
    {
        using var factory = With(("Scheduler:DashboardEnabled", "true"));
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Redis_storage_without_a_connection_string_fails_startup()
    {
        using var factory = With(("Scheduler:Storage", "Redis"));
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Redis_prefix_without_the_hash_tag_fails_startup()
    {
        using var factory = With(("Scheduler:RedisPrefix", "scheduler:"));
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }
}
