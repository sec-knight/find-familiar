using System.Net;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// A deployment that has not opened the Summoning Gate has no gate to probe.
///
/// The gateway is the one surface on this server meant to be reachable from outside, so "off" has to
/// mean genuinely absent rather than present-and-refusing. An unmapped route answers 404, which tells
/// a prober nothing; a 401 would confirm that this host has a Familiar behind a credential worth
/// guessing at, which is a disclosure made before anyone authenticated.
///
/// It is also the fail-closed test that matters most. The enabled fixture proves a wrong credential
/// is refused; this proves that a deployment which never set one is not accidentally serving the
/// user's memory to anybody who asks.
/// </summary>
public sealed class FamiliarGatewayDisabledTests
{
    [Theory]
    [InlineData("/api/gateway/manifest")]
    [InlineData("/api/gateway/projects")]
    [InlineData("/mcp")]
    public async Task An_unconfigured_gateway_exposes_no_route_at_all(string route)
    {
        using var factory = new DisabledGatewayFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(route);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Enabled without a usable token still refuses everything. The two settings are separate so a
    /// deployment can say "off" as a fact rather than as the absence of a secret — but a half-configured
    /// gate must never be an open one.
    /// </summary>
    [Fact]
    public async Task A_gateway_enabled_without_a_usable_token_refuses_every_call()
    {
        using var factory = new DisabledGatewayFactory(enabled: true, token: "too-short");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/gateway/manifest");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer too-short");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Its own host, because the gateway's presence is decided once at startup by whether routes are
    /// mapped. The shared collection fixture is deliberately enabled; this one is deliberately not.
    /// </summary>
    private sealed class DisabledGatewayFactory : WebApplicationFactory<Program>
    {
        private readonly string _tempDirectory;
        private readonly bool _enabled;
        private readonly string? _token;

        public DisabledGatewayFactory(bool enabled = false, string? token = null)
        {
            _enabled = enabled;
            _token = token;
            _tempDirectory = Path.Combine(
                Path.GetTempPath(), "FindFamiliar.Tests", Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(_tempDirectory);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Familiar:DataDirectory", _tempDirectory);
            builder.UseSetting(
                "ConnectionStrings:FindFamiliar",
                $"Data Source={Path.Combine(_tempDirectory, "find-familiar-test.db")}");
            builder.UseSetting("FamiliarGateway:Enabled", _enabled ? "true" : "false");
            builder.UseSetting("FamiliarGateway:Token", _token ?? string.Empty);

            // No paid provider can be reached from a test host, on this fixture as on the shared one.
            builder.UseSetting("Familiar:Chat:Provider", string.Empty);
            builder.UseSetting("Familiar:Chat:ApiKeyVariable", string.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            TemporaryDirectoryCleanup.Delete(_tempDirectory);
        }
    }
}
