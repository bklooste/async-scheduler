using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AsyncScheduler.Tests;

public sealed record Received(string Method, string Path, string Body, Dictionary<string, string> Headers, DateTimeOffset At);

/// <summary>A real HTTP endpoint on a random port that records every call the scheduler makes to it.</summary>
public sealed class Receiver : IAsyncDisposable
{
    private readonly WebApplication app;
    public ConcurrentQueue<Received> Calls { get; } = new();
    public string BaseUrl { get; }

    private Receiver(WebApplication app, string baseUrl) { this.app = app; BaseUrl = baseUrl; }

    public static async Task<Receiver> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        Receiver? self = null;
        app.Map("/{**path}", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            self!.Calls.Enqueue(new(ctx.Request.Method, ctx.Request.Path, body, ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()), DateTimeOffset.UtcNow));
            return ctx.Request.Path.StartsWithSegments("/gone") ? Results.NotFound() : Results.Ok();
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return self = new Receiver(app, url);
    }

    public IEnumerable<Received> To(string path) => Calls.Where(c => c.Path == path);

    public static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(100);
        }
        return condition();
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
