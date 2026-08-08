using System.Net;
using System.Security.Cryptography;
using System.Text;
using FindFamiliar.Server.Services.Familiar.Gateway;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway.OAuth;

/// <summary>
/// The Summoning Gate's authorization server: discovery, registration, consent and tokens.
///
/// <b>Why this exists at all.</b> Sprint 14 authenticated the gate with a bearer token the user pasted
/// into ChatGPT. That still works and remains the way a terminal reaches this server. What it does not
/// do is satisfy a client that speaks the MCP authorization spec, which requires the resource to name
/// an authorization server, the authorization server to publish metadata, and the client to obtain a
/// token through a browser flow it controls. This file is the smallest standards-compliant answer to
/// that requirement, and nothing more.
///
/// <b>Smallest, specifically.</b> One user, one scope, one resource. No user table — the person proves
/// they are the owner by presenting the gateway token they already have, which is the same credential
/// and the same fixed-time comparison the request filter uses, so this adds a flow rather than a trust
/// assumption. No client store, no token store: <see cref="FamiliarOAuthArtifacts"/> signs instead. No
/// consent-grant memory, because with one user and one scope there is nothing to remember that
/// re-approving would not restate.
///
/// <b>What is deliberately not here.</b> No <c>plain</c> PKCE, no implicit grant, no password grant, no
/// client credentials grant, no OIDC identity claims, no CIMD client resolution. Each would be a
/// surface to defend for a capability this deployment does not use.
/// </summary>
public static class FamiliarOAuthEndpoints
{
    public const string AuthorizeRoute = "/oauth/authorize";
    public const string TokenRoute = "/oauth/token";
    public const string RegisterRoute = "/oauth/register";

    /// <summary>
    /// RFC 9728 §3.1: a client that knows the resource <c>https://host/mcp</c> looks for its metadata
    /// at <c>https://host/.well-known/oauth-protected-resource/mcp</c>, inserting the well-known
    /// segment before the resource path. Both that form and the bare form are served, because clients
    /// in the wild try one, the other, or both, and the document is identical either way.
    /// </summary>
    public const string ProtectedResourceMetadataRoute = "/.well-known/oauth-protected-resource";

    public const string AuthorizationServerMetadataRoute = "/.well-known/oauth-authorization-server";

    /// <summary>The metadata URL a 401 points at, or null when this deployment has no OAuth.</summary>
    public static string? ResourceMetadataUrl(FamiliarGatewayOptions options) =>
        options.ResolvedIssuer is { } issuer
            ? issuer + ProtectedResourceMetadataRoute + FamiliarMcpEndpoint.Route
            : null;

    public static void MapFamiliarOAuthEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FamiliarGatewayOptions>>().Value;

        if (!options.IsOAuthConfigured())
        {
            // Same posture as the gateway itself: not mapped rather than mapped-and-refusing. A
            // deployment that has not published a public base URL has no OAuth surface to probe.
            return;
        }

        // ------------------------------------------------------------ discovery

        foreach (var route in new[]
                 {
                     ProtectedResourceMetadataRoute,
                     ProtectedResourceMetadataRoute + FamiliarMcpEndpoint.Route
                 })
        {
            MapMetadataDocument(app, route, (context, current) =>
                Document(context, new
                {
                    resource = current.ResolvedResource,
                    authorization_servers = new[] { current.ResolvedIssuer },
                    scopes_supported = FamiliarGatewayOptions.SupportedScopes,
                    bearer_methods_supported = new[] { "header" }
                }));
        }

        foreach (var route in new[]
                 {
                     AuthorizationServerMetadataRoute,
                     AuthorizationServerMetadataRoute + FamiliarMcpEndpoint.Route
                 })
        {
            MapMetadataDocument(app, route, (context, current) =>
            {
                var issuer = current.ResolvedIssuer;

                return Document(context, new
                {
                    issuer,
                    authorization_endpoint = issuer + AuthorizeRoute,
                    token_endpoint = issuer + TokenRoute,
                    registration_endpoint = issuer + RegisterRoute,
                    scopes_supported = FamiliarGatewayOptions.SupportedScopes,
                    response_types_supported = new[] { "code" },
                    grant_types_supported = new[] { "authorization_code", "refresh_token" },

                    // Public client with PKCE. This server issues no client secrets, so advertising any
                    // other method would be advertising something it cannot honour.
                    token_endpoint_auth_methods_supported = new[] { "none" },
                    code_challenge_methods_supported = new[] { "S256" },

                    // RFC 8707. Declared so a client knows the resource parameter will be honoured
                    // rather than ignored, which is what binds its token to this server alone.
                    resource_parameter_supported = true,
                    authorization_response_iss_parameter_supported = true
                });
            });
        }

        // ------------------------------------------------------------ registration (RFC 7591)

        // Registration and token are called by clients that may run either side of a browser. The
        // preflight is answered for the same reason the metadata documents answer one: a blocked
        // preflight fails on the client with nothing in this server's log to explain it.
        foreach (var route in new[] { RegisterRoute, TokenRoute })
        {
            app.MapMethods(route, ["OPTIONS"], (HttpContext context) =>
            {
                ApplyCrossOriginPostHeaders(context);

                return Results.StatusCode(StatusCodes.Status204NoContent);
            });
        }

        app.MapPost(RegisterRoute, async (HttpContext context, IOptions<FamiliarGatewayOptions> current, FamiliarOAuthArtifacts artifacts) =>
        {
            var settings = current.Value;
            ApplyCrossOriginPostHeaders(context);

            if (context.Request.ContentLength is { } length && length > settings.MaxRequestBytes)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, "invalid_client_metadata", "The registration request is too large.");
            }

            RegistrationRequest? request;

            try
            {
                request = await context.Request.ReadFromJsonAsync<RegistrationRequest>();
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or BadHttpRequestException)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_client_metadata", "The registration request could not be read.");
            }

            var redirectUris = request?.RedirectUris ?? [];

            if (redirectUris.Length is 0 or > 8)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_redirect_uri", "Between one and eight redirect URIs are required.");
            }

            foreach (var candidate in redirectUris)
            {
                if (!IsAcceptableRedirectUri(candidate, settings))
                {
                    // The one place this server refuses something a spec-compliant client may ask for.
                    // Dynamic registration lets an unauthenticated caller nominate where an
                    // authorization code is delivered; without this the user's own approval is what
                    // would hand a code to an attacker's host.
                    return Error(
                        StatusCodes.Status400BadRequest,
                        "invalid_redirect_uri",
                        "This deployment only registers redirect URIs on its configured allowed hosts.");
                }
            }

            var clientName = Truncate(request?.ClientName, 100);
            var clientId = artifacts.IssueClientId(redirectUris, clientName);

            // RFC 7591 §3.2.1 requires the response to state all of the client's registered metadata —
            // not only what changed, and not only what the server chose to keep. A client comparing
            // what it asked for against what it got is entitled to find every field there, and one that
            // treats a missing field as a failed registration is within its rights.
            return Results.Json(
                new
                {
                    client_id = clientId,
                    client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    redirect_uris = redirectUris,
                    token_endpoint_auth_method = "none",
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" },
                    // What this client may ask for, not what it holds. Registration grants nothing —
                    // only the consent screen does — so stating the read scope alone here would tell a
                    // client it could never request a decision permission it is in fact allowed to ask
                    // for.
                    scope = FamiliarGatewayOptions.FormatScopes(FamiliarGatewayOptions.SupportedScopes),

                    // Echoed when supplied, absent when not. This is the name shown on the consent
                    // screen, so a client that sent one and got nothing back has reason to doubt the
                    // registration took.
                    client_name = clientName
                },
                statusCode: StatusCodes.Status201Created);
        });

        // ------------------------------------------------------------ authorization

        app.MapGet(AuthorizeRoute, (
            HttpContext context,
            IOptions<FamiliarGatewayOptions> current,
            FamiliarOAuthArtifacts artifacts,
            IOptions<FamiliarIdentityOptions> identity) =>
        {
            var query = context.Request.Query;
            var settings = current.Value;

            if (!artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Client, query["client_id"], out var client))
            {
                // No redirect: an unrecognised client is exactly the case where redirecting would mean
                // trusting a URI nobody registered. RFC 6749 §4.1.2.1 requires this be shown, not sent.
                return Html(Refusal("This client is not registered with this Familiar."), StatusCodes.Status400BadRequest);
            }

            var redirectUri = query["redirect_uri"].ToString();

            if (string.IsNullOrEmpty(redirectUri))
            {
                redirectUri = client.RedirectUris is { Length: 1 } only ? only[0] : string.Empty;
            }

            // Exact match against what was registered. Not prefix, not host-only: an attacker who can
            // append a path to a registered URI can collect the code at it.
            if (client.RedirectUris is null
                || !client.RedirectUris.Contains(redirectUri, StringComparer.Ordinal)
                || !IsAcceptableRedirectUri(redirectUri, settings))
            {
                return Html(Refusal("That redirect URI is not registered for this client."), StatusCodes.Status400BadRequest);
            }

            var state = query["state"].ToString();

            // From here on the redirect URI is trusted, so protocol errors travel to the client the way
            // the RFC asks rather than dead-ending in a browser.
            if (query["response_type"].ToString() != "code")
            {
                return RedirectWithError(redirectUri, "unsupported_response_type", state, settings);
            }

            if (query["code_challenge_method"].ToString() != "S256" || string.IsNullOrEmpty(query["code_challenge"]))
            {
                return RedirectWithError(redirectUri, "invalid_request", state, settings);
            }

            // RFC 8707. A client that names a different resource is asking for a token this server would
            // not be the audience of; issuing one anyway is the audience-confusion the spec forbids.
            var resource = query["resource"].ToString();

            if (!string.IsNullOrEmpty(resource) && !ResourceMatches(resource, settings))
            {
                return RedirectWithError(redirectUri, "invalid_target", state, settings);
            }

            // An unrecognised scope fails the request rather than being dropped. Silently reducing a
            // request to what is understood would hand back a token that means less than the client
            // believes it holds, and the client would discover that only when an operation failed.
            if (!FamiliarGatewayOptions.TryParseScopes(query["scope"].ToString(), out var requestedScopes))
            {
                return RedirectWithError(redirectUri, "invalid_scope", state, settings);
            }

            var scope = FamiliarGatewayOptions.FormatScopes(requestedScopes);

            var pending = artifacts.IssueAuthorizationRequest(
                query["client_id"].ToString(), redirectUri, query["code_challenge"].ToString(), state, scope);

            return Html(
                ConsentPage(identity.Value, client.ClientName, pending, requestedScopes, problem: null),
                StatusCodes.Status200OK);
        });

        app.MapPost(AuthorizeRoute, async (
            HttpContext context,
            IOptions<FamiliarGatewayOptions> current,
            FamiliarOAuthArtifacts artifacts,
            IOptions<FamiliarIdentityOptions> identity,
            ILoggerFactory loggerFactory) =>
        {
            var settings = current.Value;
            var logger = loggerFactory.CreateLogger("FindFamiliar.Server.Api.Gateway.OAuth");

            if (context.Request.ContentLength is { } length && length > settings.MaxRequestBytes)
            {
                return Html(Refusal("That request was too large."), StatusCodes.Status413PayloadTooLarge);
            }

            IFormCollection form;

            try
            {
                form = await context.Request.ReadFormAsync();
            }
            catch (Exception exception) when (exception is InvalidOperationException or BadHttpRequestException)
            {
                return Html(Refusal("That approval could not be read."), StatusCodes.Status400BadRequest);
            }

            // The in-flight request is carried in a signed field rather than as loose hidden inputs.
            // Without that, a page a browser was tricked into submitting could change the redirect URI
            // between the checks above and the code issued below.
            if (!artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Request, form["request"], out var pending))
            {
                return Html(Refusal("That approval has expired. Start the connection again."), StatusCodes.Status400BadRequest);
            }

            if (!OwnerCredentialMatches(form["owner_token"], settings))
            {
                // Same shape of refusal as the request filter, and the same silence about why. What is
                // being guessed at here is the same secret.
                logger.LogWarning("A Familiar OAuth approval was refused: the owner credential did not match.");

                return Html(
                    ConsentPage(
                        identity.Value,
                        clientName: null,
                        form["request"].ToString(),
                        FamiliarOAuthArtifacts.ScopesOf(pending),
                        "That was not the gateway token."),
                    StatusCodes.Status401Unauthorized);
            }

            // The scope comes from the signed request, which is what the human was shown. Nothing the
            // browser posts can change it.
            var code = artifacts.IssueCode(
                pending.ClientId!, pending.RedirectUri!, pending.CodeChallenge!, pending.Scope ?? FamiliarGatewayOptions.ReadScope);

            var separator = pending.RedirectUri!.Contains('?') ? "&" : "?";
            var location = pending.RedirectUri
                + separator
                + "code=" + Uri.EscapeDataString(code)
                + "&iss=" + Uri.EscapeDataString(settings.ResolvedIssuer!);

            if (!string.IsNullOrEmpty(pending.State))
            {
                location += "&state=" + Uri.EscapeDataString(pending.State);
            }

            context.Response.Headers.CacheControl = "no-store";

            return Results.Redirect(location);
        });

        // ------------------------------------------------------------ token

        app.MapPost(TokenRoute, async (
            HttpContext context,
            IOptions<FamiliarGatewayOptions> current,
            FamiliarOAuthArtifacts artifacts,
            FamiliarOAuthReplayGuard replayGuard) =>
        {
            var settings = current.Value;
            context.Response.Headers.CacheControl = "no-store";
            ApplyCrossOriginPostHeaders(context);

            if (context.Request.ContentLength is { } length && length > settings.MaxRequestBytes)
            {
                return Error(StatusCodes.Status413PayloadTooLarge, "invalid_request", "The token request is too large.");
            }

            IFormCollection form;

            try
            {
                form = await context.Request.ReadFormAsync();
            }
            catch (Exception exception) when (exception is InvalidOperationException or BadHttpRequestException)
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_request", "The token request could not be read.");
            }

            var resource = form["resource"].ToString();

            if (!string.IsNullOrEmpty(resource) && !ResourceMatches(resource, settings))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_target", "That resource is not this server.");
            }

            return form["grant_type"].ToString() switch
            {
                "authorization_code" => ExchangeCode(form, artifacts, replayGuard, settings),
                "refresh_token" => ExchangeRefreshToken(form, artifacts, replayGuard, settings),
                _ => Error(StatusCodes.Status400BadRequest, "unsupported_grant_type", "This server issues tokens by authorization code or refresh token.")
            };
        });
    }

    // ---------------------------------------------------------------- serving the metadata documents

    /// <summary>
    /// A discovery document answers GET, HEAD and OPTIONS.
    ///
    /// <c>MapGet</c> alone answers only GET — a HEAD probe gets 404 and an OPTIONS preflight gets 405,
    /// which for a client that probes before it fetches looks exactly like "this server has no
    /// metadata". These documents are public, fixed, and identical for every caller, so the extra two
    /// methods cost nothing and remove a whole class of "discovery failed" that is not about content.
    ///
    /// Kestrel discards the response body for a HEAD request, so the same handler serves both.
    /// </summary>
    private static void MapMetadataDocument(
        WebApplication app,
        string route,
        Func<HttpContext, FamiliarGatewayOptions, IResult> handler) =>
        app.MapMethods(route, ["GET", "HEAD", "OPTIONS"], (HttpContext context, IOptions<FamiliarGatewayOptions> current) =>
        {
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                ApplyMetadataHeaders(context);

                return Results.StatusCode(StatusCodes.Status204NoContent);
            }

            return handler(context, current.Value);
        });

    private static IResult Document(HttpContext context, object body)
    {
        ApplyMetadataHeaders(context);

        return Results.Json(body);
    }

    /// <summary>
    /// The headers a discovery document needs beyond its body.
    ///
    /// <b>CORS, deliberately wide open, and safe because of what these documents are.</b> They contain
    /// fixed protocol URLs and no user data, they are readable by anyone who can reach the host, and
    /// this server accepts no cookies — so a cross-origin reader learns exactly what a
    /// <c>curl</c> would. Allowing the read means a client whose discovery happens in a browser is not
    /// silently blocked by the one policy that produces no server-side error to diagnose.
    ///
    /// <b>A short cache, not none.</b> These change only when the deployment is reconfigured, and a
    /// client that re-fetches them on every call is being made to pay for nothing.
    /// </summary>
    private static void ApplyMetadataHeaders(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowMethods = "GET, HEAD, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Authorization, Mcp-Protocol-Version";
        context.Response.Headers.AccessControlMaxAge = "3600";
        context.Response.Headers.CacheControl = "public, max-age=300";
    }

    /// <summary>
    /// The same reasoning as the metadata documents, for the two endpoints a client POSTs to.
    ///
    /// Neither is authenticated by anything a browser would attach automatically: registration is
    /// public, and the token endpoint is protected by a code and a PKCE verifier the caller must
    /// already hold. Credentials are not allowed, so no ambient cookie or header can be replayed
    /// cross-origin — an attacker's page gains nothing it could not get from its own server.
    /// </summary>
    private static void ApplyCrossOriginPostHeaders(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowMethods = "POST, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type, Mcp-Protocol-Version";
        context.Response.Headers.AccessControlMaxAge = "3600";
    }

    // ---------------------------------------------------------------- grants

    private static IResult ExchangeCode(
        IFormCollection form,
        FamiliarOAuthArtifacts artifacts,
        FamiliarOAuthReplayGuard replayGuard,
        FamiliarGatewayOptions settings)
    {
        if (!artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Code, form["code"], out var code))
        {
            // Expired, forged, already-shaped-wrong and wrong-audience all answer identically. A client
            // has nothing to do differently between them, and a prober would learn from the difference.
            return InvalidGrant();
        }

        // Single use. The signature says this server issued the code; only this says nobody spent it.
        //
        // Spent before the checks below rather than after, so a failed redemption burns the code too.
        // That is deliberate: a code that survived a wrong verifier would be a sixty-second window to
        // guess the verifier in, and the cost of the stricter rule is that a client which botches one
        // exchange must start the flow again rather than retry.
        if (!replayGuard.TrySpend(code.Id, DateTimeOffset.FromUnixTimeSeconds(code.ExpiresAt)))
        {
            return InvalidGrant();
        }

        var clientId = form["client_id"].ToString();

        if (!string.IsNullOrEmpty(clientId) && clientId != code.ClientId)
        {
            return InvalidGrant();
        }

        // RFC 6749 §4.1.3: the redirect URI presented here must equal the one the code was issued
        // against, which is what stops a code obtained through one registered URI being redeemed as if
        // it had come through another.
        var redirectUri = form["redirect_uri"].ToString();

        if (!string.IsNullOrEmpty(redirectUri) && redirectUri != code.RedirectUri)
        {
            return InvalidGrant();
        }

        if (!FamiliarOAuthArtifacts.VerifyCodeChallenge(code.CodeChallenge ?? string.Empty, form["code_verifier"].ToString()))
        {
            return InvalidGrant();
        }

        return IssuedTokens(artifacts, settings, code.ClientId!, FamiliarOAuthArtifacts.ScopesOf(code));
    }

    private static IResult ExchangeRefreshToken(
        IFormCollection form,
        FamiliarOAuthArtifacts artifacts,
        FamiliarOAuthReplayGuard replayGuard,
        FamiliarGatewayOptions settings)
    {
        if (!artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Refresh, form["refresh_token"], out var refresh))
        {
            return InvalidGrant();
        }

        // Rotation, required of public clients by OAuth 2.1: the presented token is spent here and a new
        // one is issued below, so a captured refresh token stops working the moment the real client uses
        // its own.
        if (!replayGuard.TrySpend(refresh.Id, DateTimeOffset.FromUnixTimeSeconds(refresh.ExpiresAt)))
        {
            return InvalidGrant();
        }

        var clientId = form["client_id"].ToString();

        if (!string.IsNullOrEmpty(clientId) && clientId != refresh.ClientId)
        {
            return InvalidGrant();
        }

        var granted = FamiliarOAuthArtifacts.ScopesOf(refresh);

        // RFC 6749 §6: a refresh may ask for less, never for more. A client that could widen its own
        // grant here would make the consent screen decorative — it would need the human once and never
        // again. Asking for something outside the original grant is refused rather than trimmed,
        // because a client that thinks it holds a permission should learn otherwise now.
        var requested = form["scope"].ToString();

        if (!string.IsNullOrEmpty(requested))
        {
            if (!FamiliarGatewayOptions.TryParseScopes(requested, out var narrowed)
                || narrowed.Any(scope => !granted.Contains(scope, StringComparer.Ordinal)))
            {
                return Error(StatusCodes.Status400BadRequest, "invalid_scope", "That scope was not granted.");
            }

            granted = narrowed;
        }

        return IssuedTokens(artifacts, settings, refresh.ClientId!, granted);
    }

    private static IResult IssuedTokens(
        FamiliarOAuthArtifacts artifacts,
        FamiliarGatewayOptions settings,
        string clientId,
        IReadOnlyList<string> scopes)
    {
        var scope = FamiliarGatewayOptions.FormatScopes(scopes);

        return Results.Json(new
        {
            access_token = artifacts.IssueAccessToken(clientId, scope),
            token_type = "Bearer",
            expires_in = Math.Max(60, settings.AccessTokenLifetimeSeconds),
            refresh_token = artifacts.IssueRefreshToken(clientId, scope),

            // Stated back on every response, because the granted scope may be narrower than the one
            // asked for and a client is entitled to know what it actually holds.
            scope
        });
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The owner proves themselves with the gateway token they already hold. No user table, no second
    /// secret, and the same fixed-time comparison over digests the request filter uses — so this adds a
    /// flow rather than a new thing to trust.
    /// </summary>
    private static bool OwnerCredentialMatches(string? supplied, FamiliarGatewayOptions settings)
    {
        if (!settings.IsConfigured() || string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(settings.Token!.Trim())),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied.Trim())));
    }

    /// <summary>
    /// A redirect URI is acceptable when it is absolute, carries no fragment, and is either loopback or
    /// https on an allowed host. Suffix matching is done on a dot boundary so <c>notchatgpt.com</c>
    /// cannot pass as <c>chatgpt.com</c>.
    /// </summary>
    private static bool IsAcceptableRedirectUri(string? candidate, FamiliarGatewayOptions settings)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        if (uri.IsLoopback)
        {
            return uri.Scheme is "http" or "https";
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host.ToLowerInvariant();

        return settings.ResolvedAllowedRedirectHosts.Any(
            allowed => host == allowed || host.EndsWith("." + allowed, StringComparison.Ordinal));
    }

    private static bool ResourceMatches(string resource, FamiliarGatewayOptions settings)
    {
        var canonical = settings.ResolvedResource;

        // The spec asks clients for the most specific URI and allows the bare origin; both name this
        // server, and a trailing slash is a formatting difference rather than a different resource.
        return canonical is not null
            && (string.Equals(resource.TrimEnd('/'), canonical, StringComparison.OrdinalIgnoreCase)
                || string.Equals(resource.TrimEnd('/'), settings.ResolvedIssuer, StringComparison.OrdinalIgnoreCase));
    }

    private static IResult InvalidGrant() =>
        Error(StatusCodes.Status400BadRequest, "invalid_grant", "That grant is not valid.");

    private static IResult Error(int statusCode, string error, string description) =>
        Results.Json(new { error, error_description = description }, statusCode: statusCode);

    private static IResult RedirectWithError(string redirectUri, string error, string? state, FamiliarGatewayOptions settings)
    {
        var separator = redirectUri.Contains('?') ? "&" : "?";
        var location = redirectUri + separator + "error=" + Uri.EscapeDataString(error)
            + "&iss=" + Uri.EscapeDataString(settings.ResolvedIssuer!);

        if (!string.IsNullOrEmpty(state))
        {
            location += "&state=" + Uri.EscapeDataString(state);
        }

        return Results.Redirect(location);
    }

    private static IResult Html(string html, int statusCode) =>
        Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, statusCode);

    private static string? Truncate(string? value, int maximum) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    // ---------------------------------------------------------------- pages

    /// <summary>
    /// The consent screen. It names the Familiar, names what is being granted, and asks for the one
    /// credential that proves ownership.
    ///
    /// <b>It discloses nothing.</b> No project, no count, no context — this page is reachable by anyone
    /// who can construct an authorization request, which after Funnel means anyone at all. The client's
    /// own name is the only caller-supplied string on it and is HTML-encoded, because a registration is
    /// unauthenticated and its <c>client_name</c> is therefore attacker-controlled text.
    /// </summary>
    private static string ConsentPage(
        FamiliarIdentityOptions identity,
        string? clientName,
        string pendingRequest,
        IReadOnlyList<string> scopes,
        string? problem)
    {
        var name = WebUtility.HtmlEncode(identity.ResolvedName);
        var client = WebUtility.HtmlEncode(clientName ?? "An external client");
        var notice = problem is null
            ? string.Empty
            : $"<p class=\"problem\">{WebUtility.HtmlEncode(problem)}</p>";

        var decides = scopes.Contains(FamiliarGatewayOptions.DecideScope, StringComparer.Ordinal);
        var writesProjects = scopes.Contains(FamiliarGatewayOptions.ProjectWriteScope, StringComparer.Ordinal);
        var startsWork = scopes.Contains(FamiliarGatewayOptions.WorkflowStartScope, StringComparer.Ordinal);
        var controlsWork = scopes.Contains(FamiliarGatewayOptions.WorkflowControlScope, StringComparer.Ordinal);
        var anyWrite = decides || writesProjects || startsWork || controlsWork;

        // The summary line must never overstate. It said "read-only" when read-only was all there was;
        // it keeps saying exactly that, and says something different only when something different is
        // being asked for.
        var summary = anyWrite
            ? "This grants read access to your Familiar's context, and the specific abilities listed below."
            : "This grants read-only access to your Familiar's context.";

        // Stated as what the client may carry, never as what it may decide. The distinction is the
        // whole point of the scope: a model that could approve its own work would be the failure this
        // architecture exists to prevent, and the screen a person reads should not blur it.
        var decideBlock = decides
            ? """
              <div class="grant elevated">
                <strong>Also requested: permission to submit your decisions</strong>
                <p>
                  This lets the client carry a decision <em>you</em> have explicitly made — such as approving a
                  step that is waiting on you — back to Find Familiar, so you do not have to open the
                  Demiplane to act on it.
                </p>
                <ul>
                  <li>It does <strong>not</strong> let the AI approve work by itself, or decide anything on your behalf</li>
                  <li>It does <strong>not</strong> grant general write access: nothing can be created, edited or deleted with it</li>
                  <li>Find Familiar still checks every decision independently and refuses any that is not currently legal</li>
                  <li>You remain the authority; the client only relays what you chose</li>
                </ul>
              </div>
              """
            : string.Empty;

        // The "no writes" promise stays absolute where it is true, and becomes precise rather than
        // false where it is not. A screen that kept claiming nothing can change while asking for the
        // decision permission would be the exact misstatement this slice was told to avoid.
        var notWritten = anyWrite
            ? "No general write access: it can do the specific things listed below and nothing else — it "
              + "cannot delete anything, change your settings, or reach any project you have marked sensitive"
            : "No writes: no tasks, sessions, plans or context entries can be created or changed";

        var approveLabel = anyWrite ? "Approve access" : "Approve read access";

        // One block per capability, so a person reads what they are actually granting rather than a
        // single sentence covering four different sizes of consequence. Each says plainly what it
        // cannot do, because that is the part somebody agreeing quickly will assume.
        var projectBlock = writesProjects
            ? """
              <div class="grant elevated">
                <strong>Also requested: create and update project work</strong>
                <p>Lets the client write things down for you — a new project, a new task, a task's status,
                   or a note recorded against a project or task.</p>
                <ul>
                  <li>It does <strong>not</strong> start or run anything: a task it creates sits waiting until you say to run it</li>
                  <li>It does <strong>not</strong> delete anything</li>
                  <li>It cannot answer a step that is waiting on your decision</li>
                </ul>
              </div>
              """
            : string.Empty;

        var startBlock = startsWork
            ? """
              <div class="grant elevated">
                <strong>Also requested: start work on a task</strong>
                <p>Lets the client ask a Planner, Implementer or Reviewer to run on a task you already have,
                   when you tell it to. Running work spends model time.</p>
                <ul>
                  <li>Only when you say so — it may not decide on its own that work should run</li>
                  <li>Find Familiar still checks the step is legal and may refuse it</li>
                  <li>It cannot approve a step that is already waiting on your decision</li>
                </ul>
              </div>
              """
            : string.Empty;

        var controlBlock = controlsWork
            ? """
              <div class="grant elevated">
                <strong>Also requested: stop work that is running</strong>
                <p>Lets the client cancel a session you ask it to stop. Cancelling ends work in progress and
                   cannot be undone.</p>
                <ul>
                  <li>Only when you say so</li>
                  <li>It cannot stop anything that has already finished</li>
                </ul>
              </div>
              """
            : string.Empty;

        return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <meta name="robots" content="noindex, nofollow">
          <title>Connect to {{name}}</title>
          <style>
            :root { color-scheme: light dark; }
            body { font: 16px/1.5 system-ui, sans-serif; max-width: 34rem; margin: 4rem auto; padding: 0 1.25rem; }
            h1 { font-size: 1.35rem; margin-bottom: .25rem; }
            .muted { opacity: .75; }
            .grant { border: 1px solid currentColor; border-radius: .5rem; padding: .85rem 1rem; margin: 1.5rem 0; opacity: .9; }
            label { display: block; font-weight: 600; margin-bottom: .35rem; }
            input { width: 100%; padding: .6rem; font: inherit; border-radius: .4rem; border: 1px solid currentColor; background: transparent; color: inherit; }
            button { margin-top: 1rem; padding: .6rem 1.1rem; font: inherit; border-radius: .4rem; cursor: pointer; }
            .problem { color: #b00020; font-weight: 600; }
            .elevated { border-width: 2px; opacity: 1; }
          </style>
        </head>
        <body>
          <h1>Connect {{client}} to {{name}}</h1>
          <p class="muted">{{summary}}</p>
          <div class="grant">
            <strong>What is being granted</strong>
            <ul>
              <li>Read your projects, their purpose, and what needs attention</li>
              <li>Search your recorded context</li>
            </ul>
            <strong>What is not</strong>
            <ul>
              <li>{{notWritten}}</li>
              <li>Nothing marked sensitive is returned or named</li>
            </ul>
          </div>
          {{decideBlock}}
          {{projectBlock}}
          {{startBlock}}
          {{controlBlock}}
          {{notice}}
          <form method="post" action="{{AuthorizeRoute}}" autocomplete="off">
            <input type="hidden" name="request" value="{{WebUtility.HtmlEncode(pendingRequest)}}">
            <label for="owner_token">Gateway token</label>
            <input id="owner_token" name="owner_token" type="password" autocomplete="off" autofocus
                   placeholder="The value of FamiliarGateway__Token">
            <button type="submit">{{approveLabel}}</button>
          </form>
        </body>
        </html>
        """;
    }

    private static string Refusal(string message) => $$"""
        <!doctype html>
        <html lang="en">
        <head><meta charset="utf-8"><meta name="robots" content="noindex, nofollow"><title>Not connected</title></head>
        <body style="font: 16px/1.5 system-ui, sans-serif; max-width: 34rem; margin: 4rem auto; padding: 0 1.25rem;">
          <p>{{WebUtility.HtmlEncode(message)}}</p>
        </body>
        </html>
        """;

    /// <summary>
    /// The subset of RFC 7591 client metadata this server reads. Everything else a client sends is
    /// accepted and ignored rather than rejected: registration metadata is extensible by design, and
    /// failing on an unknown field would break clients for describing themselves more fully.
    /// </summary>
    private sealed class RegistrationRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
        public string[]? RedirectUris { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("client_name")]
        public string? ClientName { get; init; }
    }
}
