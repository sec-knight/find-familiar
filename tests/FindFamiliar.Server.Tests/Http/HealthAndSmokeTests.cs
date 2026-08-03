using System.Net;
using System.Text.Json;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Http;

[Collection(IntegrationTestCollection.Name)]
public sealed class HealthAndSmokeTests(FindFamiliarWebApplicationFactory factory)
{
    [Fact]
    public async Task Health_endpoint_returns_ok_status()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/About")]
    [InlineData("/Projects")]
    public async Task Core_page_returns_success(string path)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
