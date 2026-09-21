using AsyncScheduler;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.PostgreSql;
using Hangfire.Redis.StackExchange;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

// Container HEALTHCHECK: the chiselled runtime image has no shell or curl, so the app probes itself.
if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "8080";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try { return (await http.GetAsync($"http://localhost:{port}/health")).IsSuccessStatusCode ? 0 : 1; }
    catch { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSecretFiles();

builder.Services.AddOptions<SchedulerOptions>()
    .BindConfiguration(SchedulerOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
var options = builder.Configuration.GetSection(SchedulerOptions.SectionName).Get<SchedulerOptions>() ?? new();
// Validate eagerly too: ValidateOnStart runs at host start, but we branch on options while building.
Validate(options);

builder.Services.AddHttpClient(HttpCallbackSender.ClientName,
    c => c.Timeout = TimeSpan.FromSeconds(options.CallbackTimeoutSeconds));
builder.Services.AddTransient<HttpCallbackSender>();

ConnectionMultiplexer? redis = null;
var healthChecks = builder.Services.AddHealthChecks();
if (options.Storage == StorageKind.Redis)
{
    redis = ConnectionMultiplexer.Connect(options.RedisConnectionString);
    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
    healthChecks.AddCheck("redis", () => redis.IsConnected ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Redis disconnected"));
}

builder.Services.AddHangfire(c =>
{
    c.UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings();
    if (redis is not null)
        c.UseRedisStorage(redis, new RedisStorageOptions { Prefix = options.RedisPrefix, InvisibilityTimeout = TimeSpan.FromSeconds(options.InvisibilityTimeoutSeconds) });
    else if (options.Storage == StorageKind.Postgres)
        c.UsePostgreSqlStorage(o => o.UseNpgsqlConnection(options.PostgresConnectionString),
            new PostgreSqlStorageOptions { InvisibilityTimeout = TimeSpan.FromSeconds(options.InvisibilityTimeoutSeconds) });
    else
        c.UseInMemoryStorage();
});
builder.Services.AddHangfireServer(o =>
{
    o.WorkerCount = options.WorkerCount;
    o.ServerTimeout = TimeSpan.FromSeconds(options.ServerTimeoutSeconds);
    o.SchedulePollingInterval = TimeSpan.FromSeconds(options.PollIntervalSeconds);
});

// Telemetry: exporters are driven entirely by the standard OTEL_* env vars (OTEL_EXPORTER_OTLP_ENDPOINT,
// OTEL_SERVICE_NAME, ...). With none set, nothing is exported.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Environment.ApplicationName))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
         .AddHangfireInstrumentation(o => { o.DisplayNameFunc = job => $"JOB {job.Id}"; o.RecordException = true; });
        if (redis is not null) t.AddRedisInstrumentation(redis);
        t.AddOtlpExporter();
    })
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapSchedulerApi();

if (options.DashboardEnabled)
{
    if (options.DashboardAuthMode == DashboardAuth.Basic)
        app.UseDashboardBasicAuth("/hangfire", options.DashboardUsername, options.DashboardPassword);
    // Auth is enforced by the middleware above (or by the operator's own proxy for Auth=None).
    app.UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new AllowAllDashboardFilter()] });
}

ConfiguredJobs.Apply(app.Configuration, app.Services.GetRequiredService<IRecurringJobManager>(),
    app.Services.GetRequiredService<JobStorage>(),
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ConfiguredJobs"), options.ReconcileConfigJobs);

app.Run();
return 0;

static void Validate(SchedulerOptions o)
{
    var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
    if (!System.ComponentModel.DataAnnotations.Validator.TryValidateObject(o, new(o), results, true))
        throw new OptionsValidationException(SchedulerOptions.SectionName, typeof(SchedulerOptions), results.Select(r => r.ErrorMessage!));
}

public partial class Program;

file sealed class AllowAllDashboardFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context) => true;
}
