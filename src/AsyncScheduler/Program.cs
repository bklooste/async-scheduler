using AsyncScheduler;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

builder.Services.AddOptions<ServiceOptions>()
    .BindConfiguration(ServiceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Telemetry: exporters are controlled entirely by the standard OTEL_* env vars
// (OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_SERVICE_NAME, ...). With none set, nothing is exported.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(builder.Environment.ApplicationName))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter());

// Add one check per external dependency (Redis, downstream HTTP) here.
builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/", () => "async-scheduler");

app.Run();
return 0;

public partial class Program;
