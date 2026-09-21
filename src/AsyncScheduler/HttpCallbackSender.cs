using System.Net;
using System.Net.Http.Headers;

namespace AsyncScheduler;

/// <summary>Performs the callback. Resolved from DI by Hangfire when a job runs.</summary>
public class HttpCallbackSender(ILogger<HttpCallbackSender> logger, IHttpClientFactory httpClientFactory)
{
    public const string ClientName = "callbacks";

    /// <summary>
    /// Copies request headers except <c>Content-*</c>. Any non-2xx throws (so Hangfire retries) except
    /// <c>404</c>, which is logged and treated as done: the target is gone, retrying will not help.
    /// </summary>
    public async Task Send(ApiCall message, CancellationToken token)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(new HttpMethod(message.Method), message.Url);
            if (message.Body is { Length: > 0 })
            {
                request.Content = new ByteArrayContent(message.Body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            foreach (var (key, value) in message.Headers)
                if (!key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) && !key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation(key, value);

            using var result = await client.SendAsync(request, token);
            logger.LogInformation("Callback {Method} {Url} -> {Status}", message.Method, message.Url, (int)result.StatusCode);

            if (!result.IsSuccessStatusCode && result.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException($"Callback {message.Method} {message.Url} failed: {(int)result.StatusCode}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Callback to {Url} failed: {Error}", message.Url, ex.Message);
            throw;
        }
    }
}
