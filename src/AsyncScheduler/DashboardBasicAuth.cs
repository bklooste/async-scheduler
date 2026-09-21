using System.Security.Cryptography;
using System.Text;

namespace AsyncScheduler;

/// <summary>HTTP Basic guard placed in front of the dashboard path. Sends the challenge so browsers prompt.</summary>
public static class DashboardBasicAuth
{
    public static IApplicationBuilder UseDashboardBasicAuth(this IApplicationBuilder app, string path, string user, string password)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"{user}:{password}"));
        return app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments(path)) { await next(); return; }

            var header = ctx.Request.Headers.Authorization.ToString();
            if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var given = SHA256.HashData(Convert.FromBase64String(header["Basic ".Length..]));
                    if (CryptographicOperations.FixedTimeEquals(given, expected)) { await next(); return; }
                }
                catch (FormatException) { }
            }
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"scheduler\"";
        });
    }
}
