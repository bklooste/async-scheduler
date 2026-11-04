using System.Net;
using System.Net.Http.Headers;
using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.Options;

namespace AsyncScheduler;

/// <summary>Performs the callback. Resolved from DI by Hangfire when a job runs.</summary>
public class HttpCallbackSender(ILogger<HttpCallbackSender> logger, IHttpClientFactory httpClientFactory,
    ResultStore results, IOptions<SchedulerOptions> options)
{
    public const string ClientName = "callbacks";

    /// <summary>Attempts for a kept-result job that did not pass <c>attempts</c>.</summary>
    public const int DefaultAttempts = 3;

    /// <summary>
    /// A plain job (no <c>keepResult</c>). Copies request headers except <c>Content-*</c> and <c>Host</c>, and sends the
    /// stored <c>Content-Type</c>. Any non-2xx throws (so Hangfire retries) except <c>404</c>, which is logged and treated
    /// as done: the target is gone, retrying will not help.
    /// A connection-level failure (no response at all — typically a pooled keep-alive connection the server
    /// just closed) is retried once immediately, since waiting for Hangfire's first backoff costs 15-45 s.
    /// </summary>
    public async Task Send(ApiCall message, CancellationToken token)
    {
        try
        {
            using var timeout = Timeout(message, token);
            using var result = await SendWithReconnect(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            logger.LogInformation("Callback {Method} {Url} -> {Status}", message.Method, message.Url, (int)result.StatusCode);
            if (!result.IsSuccessStatusCode && result.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException($"Callback {message.Method} {message.Url} failed: {(int)result.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            logger.LogError(ex, "Callback to {Url} failed: {Error}", message.Url, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// A kept-result job. 2xx and 4xx are final. A 5xx, a connection error or a timeout is retried until
    /// <see cref="ApiCall.Attempts"/> attempts have been made, and the last outcome is stored. A body over
    /// <c>MaxResultBytes</c> is stored as the failure <c>ResultTooLarge</c>. The job always ends Succeeded once the
    /// outcome is stored: the outcome, not the Hangfire state, is the answer.
    /// </summary>
    [AutomaticRetry(Attempts = 10, DelaysInSeconds = new[] { 1, 2, 5, 10, 30 })]
    public async Task Run(ApiCall message, PerformContext? context, CancellationToken token)
    {
        var key = message.JobKey ?? throw new InvalidOperationException("A kept-result job needs a job key.");
        var jobId = context?.BackgroundJob.Id ?? "";
        if (results.Get(key)?.Result is not null)
        {
            // Re-run after the outcome was stored (a worker died between storing it and completing the job).
            logger.LogInformation("Job {JobKey} already has a result; not calling {Url} again", key, message.Url);
            return;
        }

        var attempt = (context?.GetJobParameter<int>("RetryCount", allowStale: true) ?? 0) + 1;
        var attempts = message.Attempts ?? DefaultAttempts;
        var (outcome, retryable) = await Attempt(message, token);
        logger.LogInformation("Callback {Method} {Url} -> {Outcome} (attempt {Attempt} of {Attempts})", message.Method,
            message.Url, outcome.Error ?? outcome.Status.ToString(), attempt, attempts);

        if (retryable && attempt < attempts)
            throw new InvalidOperationException(
                $"Callback {message.Method} {message.Url} failed: {outcome.Error ?? outcome.Status.ToString()}; will retry");

        results.Save(key, jobId, message.Owner, outcome, TimeSpan.FromSeconds(message.ResultTtlSeconds));
    }

    private async Task<(StoredResult Outcome, bool Retryable)> Attempt(ApiCall message, CancellationToken token)
    {
        using var timeout = Timeout(message, token);
        try
        {
            using var response = await SendWithReconnect(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            var body = await ReadCapped(response.Content, options.Value.MaxResultBytes, timeout.Token);
            return body is null
                ? (new StoredResult(StoredResult.Failed, status, null, [], "ResultTooLarge"), false)
                : (new StoredResult(StoredResult.Completed, status, contentType, body, null), status >= 500);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (new StoredResult(StoredResult.Failed, 0, null, [], "Timeout"), true);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            return (new StoredResult(StoredResult.Failed, 0, null, [], "ConnectionError"), true);
        }
    }

    /// <summary>The body, or null when it is larger than <paramref name="max"/> (read no further than max + 1 bytes).</summary>
    private static async Task<byte[]?> ReadCapped(HttpContent content, int max, CancellationToken token)
    {
        if (content.Headers.ContentLength > max) return null;
        await using var stream = await content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static CancellationTokenSource Timeout(ApiCall message, CancellationToken token)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (message.TimeoutSeconds is { } seconds) cts.CancelAfter(TimeSpan.FromSeconds(seconds));
        return cts;
    }

    private async Task<HttpResponseMessage> SendWithReconnect(ApiCall message, HttpCompletionOption completion, CancellationToken token)
    {
        try
        {
            return await SendOnce(message, completion, token);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null && !token.IsCancellationRequested)
        {
            logger.LogWarning("Callback {Method} {Url} had a connection error ({Error}); retrying once now",
                message.Method, message.Url, ex.Message);
            return await SendOnce(message, completion, token);
        }
    }

    private async Task<HttpResponseMessage> SendOnce(ApiCall message, HttpCompletionOption completion, CancellationToken token)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var request = new HttpRequestMessage(new HttpMethod(message.Method), message.Url);
        if (message.Body is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(message.Body);
            request.Content.Headers.ContentType = StoredContentType(message.Headers) ?? new MediaTypeHeaderValue("application/json");
        }
        foreach (var (key, value) in message.Headers)
            if (!key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && !key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(key, value);

        return await client.SendAsync(request, completion, token);
    }

    /// <summary>The caller's <c>Content-Type</c> as stored with the job (jobs stored by 0.2.x carry it too), if it parses.</summary>
    private static MediaTypeHeaderValue? StoredContentType(Dictionary<string, string[]> headers)
    {
        foreach (var (key, value) in headers)
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && value.Length > 0
                && MediaTypeHeaderValue.TryParse(value[0], out var parsed))
                return parsed;
        return null;
    }
}
