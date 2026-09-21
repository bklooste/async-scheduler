using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AsyncScheduler.Tests;

public class HealthTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_returns_200()
    {
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Bad_config_fails_fast_on_start()
    {
        var bad = factory.WithWebHostBuilder(b => b.UseSetting("Service:KeyPrefix", "has spaces!"));
        Assert.Throws<OptionsValidationException>(() => bad.CreateClient());
    }
}
