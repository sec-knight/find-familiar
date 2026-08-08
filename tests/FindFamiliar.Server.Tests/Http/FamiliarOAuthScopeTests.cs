using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FindFamiliar.Server.Api.Gateway;
using FindFamiliar.Server.Api.Gateway.OAuth;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;

namespace FindFamiliar.Server.Tests.Http;

/// <summary>
/// Slice 1: the authorization boundary for remote human decisions, before any decision exists.
///
/// The claim under test is narrow and worth stating exactly. <c>familiar.decide</c> is permission to
/// <em>ask</em> — to relay a choice a person made to a gate that already exists — and it is granted
/// only by that person, once, at a consent screen that says so. Everything here exists to prove that a
/// credential which did not go through that cannot acquire the permission by any other route: not by
/// being the deployment's own static token, not by refreshing, not by asking for a scope the server
/// does not know.
///
/// Slice 1 deliberately adds no operation that consumes the scope. These tests therefore assert the
/// boundary itself, and one of them asserts that nothing consequential appeared.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarOAuthScopeTests(FindFamiliarWebApplicationFactory factory)
{
    private const string RegisteredRedirectUri = "https://chatgpt.com/connector/oauth/scope-tests";
    private const string McpRoute = "/mcp";
    private const string Read = FamiliarGatewayOptions.ReadScope;
    private const string Decide = FamiliarGatewayOptions.DecideScope;

    // ---------------------------------------------------------------- the two grants are distinct

    [Fact]
    public void The_two_scopes_are_distinct_and_there_is_no_generic_write_scope()
    {
        Assert.NotEqual(Read, Decide);
        Assert.Equal([Read, Decide], FamiliarGatewayOptions.SupportedScopes);

        // The instruction was explicit: no familiar.write, and nothing that reads like one.
        Assert.DoesNotContain(
            FamiliarGatewayOptions.SupportedScopes,
            scope => scope.Contains("write", StringComparison.OrdinalIgnoreCase)
                || scope.Contains("admin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Both_scopes_are_advertised_for_discovery()
    {
        using var client = factory.CreateClient();

        foreach (var route in new[]
                 {
                     "/.well-known/oauth-protected-resource/mcp",
                     "/.well-known/oauth-authorization-server"
                 })
        {
            var document = await client.GetFromJsonAsync<JsonElement>(route);
            var scopes = document.GetProperty("scopes_supported").EnumerateArray().Select(value => value.GetString()).ToList();

            Assert.Contains(Read, scopes);
            Assert.Contains(Decide, scopes);
        }
    }

    // ---------------------------------------------------------------- requesting each grant

    [Fact]
    public async Task Familiar_read_can_still_be_requested_and_granted()
    {
        using var client = NonRedirectingClient();

        var tokens = await CompleteFlowAsync(client, Read);

        Assert.Equal(Read, tokens.GetProperty("scope").GetString());
    }

    /// <summary>An unscoped request keeps meaning what it always meant: reading, and nothing else.</summary>
    [Fact]
    public async Task An_authorization_request_naming_no_scope_still_grants_read_only()
    {
        using var client = NonRedirectingClient();

        var tokens = await CompleteFlowAsync(client, requestedScope: null);

        Assert.Equal(Read, tokens.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Familiar_decide_can_be_requested_explicitly_and_survives_the_flow()
    {
        using var client = NonRedirectingClient();

        var tokens = await CompleteFlowAsync(client, $"{Read} {Decide}");
        var granted = tokens.GetProperty("scope").GetString()!;

        Assert.Contains(Read, granted, StringComparison.Ordinal);
        Assert.Contains(Decide, granted, StringComparison.Ordinal);

        // And the grant is in the token itself, not merely in the response body describing it.
        Assert.True(ScopesOf(tokens.GetProperty("access_token").GetString()!).Contains(Decide));
    }

    [Fact]
    public async Task Decide_alone_can_be_granted_without_read()
    {
        using var client = NonRedirectingClient();

        var tokens = await CompleteFlowAsync(client, Decide);
        var scopes = ScopesOf(tokens.GetProperty("access_token").GetString()!);

        Assert.Contains(Decide, scopes);
        Assert.DoesNotContain(Read, scopes);
    }

    /// <summary>
    /// A scope this server does not issue fails the request rather than being quietly dropped. Handing
    /// back a token that means less than the client asked for defers the failure to the first operation
    /// that needed it.
    /// </summary>
    [Theory]
    [InlineData("familiar.write")]
    [InlineData("familiar.admin")]
    [InlineData("familiar.read familiar.write")]
    [InlineData("openid profile")]
    [InlineData("FAMILIAR.READ")]
    public async Task An_unsupported_scope_is_refused(string scope)
    {
        using var client = NonRedirectingClient();
        var clientId = await RegisterAsync(client);

        using var response = await client.GetAsync(AuthorizeUrl(clientId, Challenge(NewVerifier()), scope));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=invalid_scope", response.Headers.Location!.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("code=", response.Headers.Location!.Query, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- a grant cannot widen itself

    /// <summary>
    /// The rule that keeps consent meaningful. If a refresh could add a scope, a client would need the
    /// human once and never again — the consent screen would become decorative.
    /// </summary>
    [Fact]
    public async Task A_refresh_cannot_widen_a_read_only_grant_into_decide()
    {
        using var client = NonRedirectingClient();
        var tokens = await CompleteFlowAsync(client, Read);

        using var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
                ["scope"] = $"{Read} {Decide}"
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_scope", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_refresh_preserves_the_granted_scope_and_may_narrow_it()
    {
        using var client = NonRedirectingClient();
        var tokens = await CompleteFlowAsync(client, $"{Read} {Decide}");

        var preserved = await RefreshAsync(client, tokens.GetProperty("refresh_token").GetString()!, scope: null);
        Assert.Contains(Decide, ScopesOf(preserved.GetProperty("access_token").GetString()!));

        var narrowed = await RefreshAsync(client, preserved.GetProperty("refresh_token").GetString()!, Read);
        var narrowedScopes = ScopesOf(narrowed.GetProperty("access_token").GetString()!);

        Assert.Contains(Read, narrowedScopes);
        Assert.DoesNotContain(Decide, narrowedScopes);

        // And the narrowing is durable: the refresh token it returns cannot climb back up.
        using var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = narrowed.GetProperty("refresh_token").GetString()!,
                ["scope"] = Decide
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------------------------------------------------------------- what each credential permits

    [Fact]
    public void A_read_only_oauth_token_does_not_satisfy_decide()
    {
        var caller = new FamiliarGatewayCaller(FamiliarGatewayCredentialKind.OAuthAccessToken, [Read]);

        Assert.True(caller.CanRead);
        Assert.False(caller.CanDecide);
    }

    [Fact]
    public void A_token_granted_decide_satisfies_the_scope_check()
    {
        var caller = new FamiliarGatewayCaller(FamiliarGatewayCredentialKind.OAuthAccessToken, [Read, Decide]);

        Assert.True(caller.CanDecide);
    }

    /// <summary>
    /// The static token is the deployment's own secret, pasted into a terminal. No human read a consent
    /// screen to produce it, so it cannot be evidence that a human decided anything.
    /// </summary>
    [Fact]
    public void The_static_gateway_token_is_read_only_and_cannot_decide()
    {
        var caller = FamiliarGatewayCaller.StaticToken();

        Assert.Equal(FamiliarGatewayCredentialKind.StaticToken, caller.Kind);
        Assert.True(caller.CanRead);
        Assert.False(caller.CanDecide);
        Assert.Equal([Read], caller.Scopes);
    }

    /// <summary>A request that never passed the credential filter has no permissions, not all of them.</summary>
    [Fact]
    public void A_request_with_no_authenticated_caller_has_no_scopes()
    {
        Assert.Null(FamiliarGatewayCaller.From(new DefaultHttpContext()));
    }

    /// <summary>
    /// A token minted before scopes existed carries no scope claim. It must keep meaning read — the
    /// permission it has always had — and must not be read as unscoped-therefore-unlimited.
    /// </summary>
    [Fact]
    public void A_token_predating_scopes_is_treated_as_read_only()
    {
        var scopes = FamiliarOAuthArtifacts.ScopesOf(new FamiliarOAuthArtifacts.Payload { Id = "x", Scope = null });

        Assert.Equal([Read], scopes);
    }

    // ---------------------------------------------------------------- the static token still reads

    [Fact]
    public async Task The_static_token_still_performs_every_existing_read()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + FindFamiliarWebApplicationFactory.GatewayTestToken);

        using var manifest = await client.GetAsync("/api/gateway/manifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);

        using var tools = await CallMcpAsync(client, "tools/list");
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
    }

    [Fact]
    public async Task A_read_scoped_oauth_token_still_performs_every_existing_read()
    {
        using var client = NonRedirectingClient();
        var tokens = await CompleteFlowAsync(client, Read);

        using var authenticated = factory.CreateClient();
        authenticated.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + tokens.GetProperty("access_token").GetString());

        using var manifest = await authenticated.GetAsync("/api/gateway/manifest");
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);

        using var tools = await CallMcpAsync(authenticated, "tools/list");
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
    }

    // ---------------------------------------------------------------- consent wording

    /// <summary>
    /// The read-only promise must stay literally true. This is the sentence a person makes their
    /// decision from, and Slice 1 must not have quietly turned it into a half-truth.
    /// </summary>
    [Fact]
    public async Task The_consent_screen_still_promises_read_only_when_only_read_is_requested()
    {
        using var client = NonRedirectingClient();
        var page = await ConsentPageAsync(client, Read);

        Assert.Contains("read-only access", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No writes", page, StringComparison.Ordinal);
        Assert.Contains("Approve read access", page, StringComparison.Ordinal);

        // Nothing about decisions may appear on a screen that is not asking for that permission.
        Assert.DoesNotContain("submit your decisions", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Decide, page, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the elevated permission is requested the screen must say so plainly, and must not describe
    /// it as the model gaining authority. The human remains the authority; the client is a courier.
    /// </summary>
    [Fact]
    public async Task The_consent_screen_names_the_decision_permission_when_it_is_requested()
    {
        using var client = NonRedirectingClient();
        var page = await ConsentPageAsync(client, $"{Read} {Decide}");

        Assert.Contains("permission to submit your decisions", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decision <em>you</em> have explicitly made", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does <strong>not</strong> let the AI approve work by itself", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("You remain the authority", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("refuses any that is not currently legal", page, StringComparison.OrdinalIgnoreCase);

        // The read-only claim must not survive into a screen where it would be false.
        Assert.DoesNotContain("This grants read-only access", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No writes:", page, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- nothing consequential shipped

    /// <summary>
    /// The boundary exists; the operation does not. A decision-scoped token must still find no tool
    /// that will act on it — this is the assertion that catches Slice 2 arriving early.
    /// </summary>
    [Fact]
    public async Task No_operation_yet_accepts_the_decision_scope()
    {
        using var client = NonRedirectingClient();
        var tokens = await CompleteFlowAsync(client, $"{Read} {Decide}");

        using var authenticated = factory.CreateClient();
        authenticated.DefaultRequestHeaders.TryAddWithoutValidation(
            "Authorization", "Bearer " + tokens.GetProperty("access_token").GetString());

        using var response = await CallMcpAsync(authenticated, "tools/list");
        var body = await response.Content.ReadAsStringAsync();

        // Asserted on the tool names, not on the whole document: a read tool's own description may
        // legitimately contain a word like "decide" while offering nothing that acts.
        var names = System.Text.RegularExpressions.Regex
            .Matches(body, "\"name\":\"(?<name>[a-z_]+)\"")
            .Select(match => match.Groups["name"].Value)
            .Distinct()
            .ToList();

        // open_decisions reports what a human is being asked; it is still a read. The list is exactly
        // the read surface, with nothing that acts.
        Assert.Equal(
            ["familiar_manifest", "get_project_context", "list_familiar_projects", "open_decisions", "search_familiar_context"],
            names.Order());

        // Named for the verbs that would mean acting. "decisions" is a noun and is allowed; "decide",
        // "submit" and "approve" are the words a tool that consumed familiar.decide would carry.
        foreach (var forbidden in new[] { "submit", "approve", "decline", "start", "create", "_decide" })
        {
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }

        // Every advertised tool still declares itself read-only.
        Assert.Equal(names.Count, System.Text.RegularExpressions.Regex.Matches(body, "\"readOnlyHint\":true").Count);
    }

    // ---------------------------------------------------------------- helpers

    private HttpClient NonRedirectingClient() =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

    private static string AuthorizeUrl(string clientId, string challenge, string? scope) =>
        "/oauth/authorize?response_type=code"
        + $"&client_id={Uri.EscapeDataString(clientId)}"
        + "&redirect_uri=" + Uri.EscapeDataString(RegisteredRedirectUri)
        + "&code_challenge=" + challenge
        + "&code_challenge_method=S256"
        + (scope is null ? string.Empty : "&scope=" + Uri.EscapeDataString(scope));

    private static async Task<string> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/oauth/register",
            new { redirect_uris = new[] { RegisteredRedirectUri }, client_name = "ChatGPT" });

        response.EnsureSuccessStatusCode();

        var document = await response.Content.ReadFromJsonAsync<JsonElement>();

        return document.GetProperty("client_id").GetString()!;
    }

    private static async Task<string> ConsentPageAsync(HttpClient client, string? scope)
    {
        var clientId = await RegisterAsync(client);

        using var response = await client.GetAsync(AuthorizeUrl(clientId, Challenge(NewVerifier()), scope));
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>Register, consent with the owner credential, and redeem the code with PKCE.</summary>
    private static async Task<JsonElement> CompleteFlowAsync(HttpClient client, string? requestedScope)
    {
        var clientId = await RegisterAsync(client);
        var verifier = NewVerifier();

        using var authorize = await client.GetAsync(AuthorizeUrl(clientId, Challenge(verifier), requestedScope));
        authorize.EnsureSuccessStatusCode();

        var page = await authorize.Content.ReadAsStringAsync();
        var marker = "name=\"request\" value=\"";
        var start = page.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var pending = WebUtility.HtmlDecode(page[start..page.IndexOf('"', start)]);

        using var consented = await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["request"] = pending,
                ["owner_token"] = FindFamiliarWebApplicationFactory.GatewayTestToken
            }));

        Assert.Equal(HttpStatusCode.Redirect, consented.StatusCode);

        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers
            .ParseQuery(consented.Headers.Location!.Query)["code"]!;

        using var token = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code!,
                ["redirect_uri"] = RegisteredRedirectUri,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier
            }));

        Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        return await token.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient client, string refreshToken, string? scope)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        if (scope is not null)
        {
            form["scope"] = scope;
        }

        using var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Reads the scope claim out of a signed access token without trusting the response body.</summary>
    private static IReadOnlyList<string> ScopesOf(string accessToken)
    {
        var payload = accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        var padded = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

        using var document = JsonDocument.Parse(Convert.FromBase64String(padded));

        return document.RootElement.TryGetProperty("scp", out var scope)
            ? scope.GetString()!.Split(' ')
            : [];
    }

    private static Task<HttpResponseMessage> CallMcpAsync(HttpClient client, string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpRoute)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = new { } }),
                Encoding.UTF8,
                "application/json")
        };

        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        return client.SendAsync(request);
    }

    private static string NewVerifier() =>
        FamiliarOAuthArtifacts.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    private static string Challenge(string verifier) =>
        FamiliarOAuthArtifacts.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
