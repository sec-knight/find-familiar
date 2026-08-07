using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Api.Gateway;
using FindFamiliar.Server.Api.Gateway.OAuth;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// The authorization server over the wire (ADR-0017): discovery, registration, consent, tokens, and
/// every way the flow is supposed to fail.
///
/// The shape of these tests follows the shape of the risk. A resource server that accepts a token it
/// should not is a total loss of the user's memory, so the refusals get more attention here than the
/// happy path does — one test proves a client can connect, and the rest prove that everything which
/// looks almost like a valid connection cannot.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarOAuthEndpointTests(FindFamiliarWebApplicationFactory factory)
{
    private const string RegisteredRedirectUri = "https://chatgpt.com/connector/oauth/find-familiar-test";
    private const string McpRoute = "/mcp";
    private const string Issuer = FindFamiliarWebApplicationFactory.GatewayTestPublicBaseUrl;

    // ---------------------------------------------------------------- discovery

    /// <summary>
    /// RFC 9728. The resource names itself and names its authorization server, and every URL in the
    /// document is built from configuration rather than from the request — asserted by calling over
    /// loopback and requiring the configured origin to come back.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task The_resource_publishes_metadata_at_both_well_known_forms(string route)
    {
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>(route);

        Assert.Equal(Issuer + "/mcp", document.GetProperty("resource").GetString());
        Assert.Equal(Issuer, document.GetProperty("authorization_servers").EnumerateArray().Single().GetString());
        Assert.Contains(
            FamiliarGatewayOptions.ReadScope,
            document.GetProperty("scopes_supported").EnumerateArray().Select(scope => scope.GetString()));
    }

    /// <summary>
    /// RFC 8414, and specifically the fields a client makes security decisions from: S256 only, public
    /// client only, and no grant this server does not actually implement.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-authorization-server/mcp")]
    public async Task The_authorization_server_publishes_metadata_a_client_can_act_on(string route)
    {
        using var client = factory.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>(route);

        Assert.Equal(Issuer, document.GetProperty("issuer").GetString());
        Assert.Equal(Issuer + "/oauth/authorize", document.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(Issuer + "/oauth/token", document.GetProperty("token_endpoint").GetString());
        Assert.Equal(Issuer + "/oauth/register", document.GetProperty("registration_endpoint").GetString());

        Assert.Equal(
            ["S256"],
            document.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["none"],
            document.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(value => value.GetString()));

        var grants = document.GetProperty("grant_types_supported").EnumerateArray().Select(value => value.GetString()).ToList();

        Assert.Equal(["authorization_code", "refresh_token"], grants);
        Assert.DoesNotContain("implicit", grants);
        Assert.DoesNotContain("password", grants);
        Assert.DoesNotContain("client_credentials", grants);
    }

    /// <summary>
    /// The discovery documents are reachable without a credential — they have to be — so the thing to
    /// assert is that they contain nothing worth having. No token, no identity, no project.
    /// </summary>
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/.well-known/oauth-authorization-server")]
    public async Task Discovery_documents_disclose_nothing_about_the_familiar(string route)
    {
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync(route);

        Assert.DoesNotContain(FindFamiliarWebApplicationFactory.GatewayTestToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain(FindFamiliarWebApplicationFactory.GatewayTestIdentityName, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("project", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// RFC 9728 §5.1 and the MCP authorization spec: the 401 is what tells a client that has never seen
    /// this server that there is an authorization server to go and talk to. Without this header the
    /// discovery documents above might as well not exist.
    /// </summary>
    [Fact]
    public async Task An_unauthenticated_mcp_call_points_the_client_at_the_resource_metadata()
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var challenge = Assert.Single(response.Headers.WwwAuthenticate).ToString();

        Assert.Contains($"resource_metadata=\"{Issuer}/.well-known/oauth-protected-resource/mcp\"", challenge, StringComparison.Ordinal);
        Assert.DoesNotContain(FindFamiliarWebApplicationFactory.GatewayTestToken, challenge, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- registration

    [Fact]
    public async Task A_client_can_register_itself_and_is_told_it_is_public()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/oauth/register",
            new { redirect_uris = new[] { RegisteredRedirectUri }, client_name = "ChatGPT" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(document.GetProperty("client_id").GetString()));
        Assert.Equal("none", document.GetProperty("token_endpoint_auth_method").GetString());

        // A public client must never be handed a secret it would then have to protect in a browser.
        Assert.False(document.TryGetProperty("client_secret", out _));
    }

    /// <summary>
    /// The open-redirection control, on the cases that actually get tried: another origin entirely, a
    /// lookalike host that merely ends in the allowed one, and plain http.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("https://notchatgpt.com/callback")]
    [InlineData("https://chatgpt.com.evil.example/callback")]
    [InlineData("http://chatgpt.com/callback")]
    [InlineData("not-a-uri")]
    public async Task A_redirect_uri_off_the_allowed_hosts_is_refused(string redirectUri)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/oauth/register", new { redirect_uris = new[] { redirectUri } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- the flow

    /// <summary>
    /// The acceptance moment: a client with no prior relationship to this server registers, sends the
    /// user through consent, exchanges a code for a token, and reaches real MCP with it.
    /// </summary>
    [Fact]
    public async Task A_registered_client_can_complete_the_flow_and_reach_mcp()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);

        var tokens = await ExchangeCodeAsync(client, flow);

        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());
        Assert.Equal(FamiliarGatewayOptions.ReadScope, tokens.GetProperty("scope").GetString());
        Assert.True(tokens.GetProperty("expires_in").GetInt32() > 0);

        var accessToken = tokens.GetProperty("access_token").GetString()!;

        using var response = await CallMcpAsync(client, accessToken, "tools/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("search_familiar_context", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>The state parameter is returned unaltered, which is how the client detects a swapped response.</summary>
    [Fact]
    public async Task The_authorization_response_returns_state_and_the_issuer()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);

        Assert.Equal(flow.State, flow.ReturnedState);
        Assert.Equal(Issuer, flow.ReturnedIssuer);
    }

    // ---------------------------------------------------------------- refusals in the flow

    /// <summary>
    /// Consent without the owner's credential issues nothing. This is the single check standing between
    /// the public internet and a token: anybody can reach the consent screen, and only the person
    /// holding the gateway token can get past it.
    /// </summary>
    [Fact]
    public async Task Consent_without_the_owner_credential_issues_no_code()
    {
        using var client = NonRedirectingClient();
        var pending = await StartAuthorizationAsync(client);

        using var response = await client.PostAsync(
            "/oauth/authorize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["request"] = pending.PendingRequest,
                ["owner_token"] = "not-the-gateway-token-but-a-plausible-length-0123456789"
            }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);

        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(FindFamiliarWebApplicationFactory.GatewayTestToken, body, StringComparison.Ordinal);
        Assert.DoesNotContain("code=", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unregistered redirect URI is refused in the browser rather than redirected to. Redirecting an
    /// error to an unvalidated URI is the open redirect itself.
    /// </summary>
    [Fact]
    public async Task An_unregistered_redirect_uri_is_refused_without_redirecting()
    {
        using var client = NonRedirectingClient();
        var clientId = await RegisterAsync(client);

        using var response = await client.GetAsync(
            "/oauth/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + "&redirect_uri=" + Uri.EscapeDataString("https://evil.example/callback")
            + "&code_challenge=" + Challenge(NewVerifier())
            + "&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task An_unknown_client_is_refused_without_redirecting()
    {
        using var client = NonRedirectingClient();

        using var response = await client.GetAsync(
            "/oauth/authorize?response_type=code&client_id=ffc1.forged.forged"
            + "&redirect_uri=" + Uri.EscapeDataString(RegisteredRedirectUri)
            + "&code_challenge=" + Challenge(NewVerifier())
            + "&code_challenge_method=S256");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    /// <summary>
    /// PKCE downgrade. A client asking for <c>plain</c> — or for no challenge at all — is refused rather
    /// than quietly accommodated, because accommodating it would remove the protection entirely.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    public async Task An_authorization_request_without_s256_is_refused(string method)
    {
        using var client = NonRedirectingClient();
        var clientId = await RegisterAsync(client);

        using var response = await client.GetAsync(
            "/oauth/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + "&redirect_uri=" + Uri.EscapeDataString(RegisteredRedirectUri)
            + "&code_challenge=" + Challenge(NewVerifier())
            + "&code_challenge_method=" + method);

        // Refused, and refused to the registered URI as a protocol error rather than as a code.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=invalid_request", response.Headers.Location!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("code=", response.Headers.Location!.Query, StringComparison.Ordinal);
    }

    /// <summary>A token bound to some other resource is not a token this server will mint (RFC 8707).</summary>
    [Fact]
    public async Task An_authorization_request_for_another_resource_is_refused()
    {
        using var client = NonRedirectingClient();
        var clientId = await RegisterAsync(client);

        using var response = await client.GetAsync(
            "/oauth/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + "&redirect_uri=" + Uri.EscapeDataString(RegisteredRedirectUri)
            + "&code_challenge=" + Challenge(NewVerifier())
            + "&code_challenge_method=S256"
            + "&resource=" + Uri.EscapeDataString("https://someone-elses-mcp.example/mcp"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=invalid_target", response.Headers.Location!.Query, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- refusals at the token endpoint

    /// <summary>
    /// Proof of possession. A code intercepted in transit is worthless without the verifier that never
    /// left the client, and this is the test that proves the check is actually performed.
    /// </summary>
    [Fact]
    public async Task A_code_redeemed_with_the_wrong_verifier_is_refused()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);

        using var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = flow.Code,
            ["redirect_uri"] = RegisteredRedirectUri,
            ["client_id"] = flow.ClientId,
            ["code_verifier"] = NewVerifier()
        });

        await AssertInvalidGrantAsync(response);
    }

    /// <summary>OAuth 2.1 requires a code be redeemable once; a signature alone cannot enforce that.</summary>
    [Fact]
    public async Task A_code_cannot_be_redeemed_twice()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);

        var first = await ExchangeCodeAsync(client, flow);
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("access_token").GetString()));

        using var second = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = flow.Code,
            ["redirect_uri"] = RegisteredRedirectUri,
            ["client_id"] = flow.ClientId,
            ["code_verifier"] = flow.Verifier
        });

        await AssertInvalidGrantAsync(second);
    }

    /// <summary>
    /// The redirect URI presented at the token endpoint must be the one the code was issued against.
    /// </summary>
    [Fact]
    public async Task A_code_redeemed_against_a_different_redirect_uri_is_refused()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);

        using var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = flow.Code,
            ["redirect_uri"] = "https://chatgpt.com/connector/oauth/some-other-connector",
            ["client_id"] = flow.ClientId,
            ["code_verifier"] = flow.Verifier
        });

        await AssertInvalidGrantAsync(response);
    }

    [Theory]
    [InlineData("ffk1.forged.forged")]
    [InlineData("not-even-the-right-shape")]
    [InlineData("")]
    public async Task A_forged_or_malformed_code_is_refused(string code)
    {
        using var client = NonRedirectingClient();

        using var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RegisteredRedirectUri,
            ["code_verifier"] = NewVerifier()
        });

        await AssertInvalidGrantAsync(response);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("client_credentials")]
    [InlineData("implicit")]
    public async Task A_grant_this_server_does_not_implement_is_refused(string grantType)
    {
        using var client = NonRedirectingClient();

        using var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = grantType
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("unsupported_grant_type", document.GetProperty("error").GetString());
    }

    /// <summary>Rotation: the refresh token works once, and the spent one stops working.</summary>
    [Fact]
    public async Task A_refresh_token_is_rotated_and_the_old_one_stops_working()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);
        var first = await ExchangeCodeAsync(client, flow);

        var refreshToken = first.GetProperty("refresh_token").GetString()!;

        using var refreshed = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = flow.ClientId
        });

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var second = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var rotated = second.GetProperty("refresh_token").GetString()!;

        Assert.NotEqual(refreshToken, rotated);

        // The token that was just spent must not be spendable again.
        using var replayed = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = flow.ClientId
        });

        await AssertInvalidGrantAsync(replayed);

        // And the rotated one still reaches the resource, so rotation did not cost the user access.
        using var response = await CallMcpAsync(client, second.GetProperty("access_token").GetString()!, "tools/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------------------------------------------------------------- tokens at the resource

    /// <summary>
    /// The refusals that matter most, at the surface that matters most. Every one of these is a string
    /// that could plausibly be presented to <c>/mcp</c> by something that got close to a real token.
    /// </summary>
    [Theory]
    [InlineData("ffa1.forged.forged")]
    [InlineData("ffa1..")]
    [InlineData("Bearer")]
    [InlineData("ffk1.not-an-access-token.signature")]
    public async Task An_invalid_access_token_reaches_nothing(string token)
    {
        using var client = factory.CreateClient();

        using var response = await CallMcpAsync(client, token, "tools/list");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A real access token with one byte changed. The payload is base64url, so an attacker who wanted a
    /// longer expiry or a different audience would edit exactly this and re-present it.
    /// </summary>
    [Fact]
    public async Task An_access_token_whose_payload_was_edited_is_refused()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);
        var tokens = await ExchangeCodeAsync(client, flow);

        var parts = tokens.GetProperty("access_token").GetString()!.Split('.');
        var tampered = parts[0] + "." + parts[1][..^1] + (parts[1][^1] == 'A' ? 'B' : 'A') + "." + parts[2];

        using var response = await CallMcpAsync(client, tampered, "tools/list");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A code is not an access token, and a refresh token is not one either. They are signed by
    /// different keys precisely so that presenting one where another belongs fails at the signature.
    /// </summary>
    [Fact]
    public async Task A_code_or_refresh_token_cannot_be_spent_as_an_access_token()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);
        var tokens = await ExchangeCodeAsync(client, flow);

        foreach (var wrongKind in new[] { flow.Code, tokens.GetProperty("refresh_token").GetString()! })
        {
            using var response = await CallMcpAsync(client, wrongKind, "tools/list");

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    /// <summary>
    /// Sprint 14 must keep working. The static token is how a terminal on this machine reaches the gate,
    /// and adding OAuth was meant to add a way in, not replace one.
    /// </summary>
    [Fact]
    public async Task The_static_gateway_token_still_works_alongside_oauth()
    {
        using var client = factory.CreateClient();

        using var response = await CallMcpAsync(client, FindFamiliarWebApplicationFactory.GatewayTestToken, "tools/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// An OAuth token grants exactly what the consent screen said and nothing more: the read tools, and
    /// no write of any kind. The tool surface is asserted elsewhere; what is asserted here is that
    /// arriving by OAuth does not widen it.
    /// </summary>
    [Fact]
    public async Task An_oauth_token_grants_the_same_read_only_surface_as_the_static_token()
    {
        using var client = NonRedirectingClient();
        var flow = await AuthorizeAsync(client);
        var tokens = await ExchangeCodeAsync(client, flow);

        using var response = await CallMcpAsync(client, tokens.GetProperty("access_token").GetString()!, "tools/list");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        foreach (var forbidden in new[] { "\"create", "\"start", "\"approve", "\"delete", "\"update" })
        {
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Redirects must not be followed: the authorization response is the thing under test, and an
    /// HttpClient that chased it would send the code to chatgpt.com and assert on their 404.
    /// </summary>
    private HttpClient NonRedirectingClient() =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private sealed record PendingAuthorization(string ClientId, string Verifier, string State, string PendingRequest);

    private sealed record CompletedAuthorization(
        string ClientId,
        string Verifier,
        string State,
        string Code,
        string? ReturnedState,
        string? ReturnedIssuer);

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/oauth/register",
            new { redirect_uris = new[] { RegisteredRedirectUri }, client_name = "ChatGPT" });

        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        return document.GetProperty("client_id").GetString()!;
    }

    /// <summary>Registers, opens the consent screen, and lifts the signed request out of the form.</summary>
    private static async Task<PendingAuthorization> StartAuthorizationAsync(HttpClient client)
    {
        var clientId = await RegisterAsync(client);
        var verifier = NewVerifier();
        var state = Guid.NewGuid().ToString("N");

        using var response = await client.GetAsync(
            "/oauth/authorize?response_type=code"
            + $"&client_id={Uri.EscapeDataString(clientId)}"
            + "&redirect_uri=" + Uri.EscapeDataString(RegisteredRedirectUri)
            + "&code_challenge=" + Challenge(verifier)
            + "&code_challenge_method=S256"
            + "&scope=" + FamiliarGatewayOptions.ReadScope
            + "&resource=" + Uri.EscapeDataString(Issuer + "/mcp")
            + "&state=" + state);

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadAsStringAsync();

        // The consent screen must never carry anything but the protocol. If a project name or the
        // gateway token ever appears on it, this is the assertion that says so.
        Assert.DoesNotContain(FindFamiliarWebApplicationFactory.GatewayTestToken, page, StringComparison.Ordinal);

        var marker = "name=\"request\" value=\"";
        var start = page.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, "The consent page did not carry a signed authorization request.");

        start += marker.Length;
        var pending = page[start..page.IndexOf('"', start)];

        return new PendingAuthorization(clientId, verifier, state, WebUtility.HtmlDecode(pending));
    }

    private static async Task<CompletedAuthorization> AuthorizeAsync(HttpClient client)
    {
        var pending = await StartAuthorizationAsync(client);

        using var response = await client.PostAsync(
            "/oauth/authorize",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["request"] = pending.PendingRequest,
                ["owner_token"] = FindFamiliarWebApplicationFactory.GatewayTestToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;

        Assert.StartsWith(RegisteredRedirectUri, location.ToString(), StringComparison.Ordinal);

        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);

        return new CompletedAuthorization(
            pending.ClientId,
            pending.Verifier,
            pending.State,
            query["code"]!,
            query["state"],
            query["iss"]);
    }

    private static async Task<JsonElement> ExchangeCodeAsync(HttpClient client, CompletedAuthorization flow)
    {
        using var response = await PostTokenAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = flow.Code,
            ["redirect_uri"] = RegisteredRedirectUri,
            ["client_id"] = flow.ClientId,
            ["code_verifier"] = flow.Verifier,
            ["resource"] = Issuer + "/mcp"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PostTokenAsync(HttpClient client, Dictionary<string, string> form) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    private static async Task AssertInvalidGrantAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("invalid_grant", document.GetProperty("error").GetString());

        // The refusal must not say which of the several possible reasons applied.
        var description = document.GetProperty("error_description").GetString()!;

        foreach (var leaked in new[] { "expired", "verifier", "replay", "already", "redirect" })
        {
            Assert.DoesNotContain(leaked, description, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Task<HttpResponseMessage> CallMcpAsync(HttpClient client, string token, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = new { } }),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        return client.SendAsync(request);
    }

    private static string NewVerifier() =>
        FamiliarOAuthArtifacts.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    private static string Challenge(string verifier) =>
        FamiliarOAuthArtifacts.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
