namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// The credential an external AI client presents to reach the Familiar, bound from configuration
/// section "FamiliarGateway" — for example the environment variable <c>FamiliarGateway__Token</c>.
/// Never given a value in committed appsettings.
///
/// <b>A separate secret from the runner bridge, deliberately.</b> They look alike and they are not:
/// the runner token is handed to a process on this machine or on the user's tailnet, and this one is
/// handed to a frontier vendor's servers and travels the public internet on every call. Sharing one
/// credential across those two trust domains would mean a leak at either end handing away both, and
/// would make rotating one impossible without breaking the other. Two tokens cost one more line in an
/// env file.
///
/// <b>Off unless configured.</b> There is no default and no development fallback. An unset token
/// leaves the gateway refusing every request rather than accepting any, which is the correct posture
/// for a surface whose whole purpose is to be reachable from outside.
/// </summary>
public sealed class FamiliarGatewayOptions
{
    public const string SectionName = "FamiliarGateway";

    public string? Token { get; set; }

    /// <summary>
    /// Whether the gateway is mapped at all.
    ///
    /// Separate from the token so a deployment can state "no external body may reach this" as a fact
    /// about the deployment rather than as the absence of a setting. With this false the routes do
    /// not exist, and a probe cannot tell a disabled gateway from a server that never had one.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The largest request body the gateway will read, in bytes.
    ///
    /// Small on purpose. Every operation here takes a short query and some ids; a megabyte of JSON is
    /// not a question this can answer, and reading it before finding that out is the cheapest denial
    /// of service there is.
    /// </summary>
    public int MaxRequestBytes { get; set; } = 16 * 1024;

    /// <summary>
    /// A token short enough to be guessable is worse than none, because it looks like security. This
    /// is not a strength estimate — it is a floor that rejects a placeholder somebody meant to
    /// replace.
    /// </summary>
    public const int MinimumTokenLength = 32;

    public bool IsConfigured() =>
        Enabled
        && !string.IsNullOrWhiteSpace(Token)
        && Token.Trim().Length >= MinimumTokenLength;

    // ------------------------------------------------------------------ OAuth (Sprint 14.1)

    /// <summary>
    /// The origin this deployment is reachable at from the public internet — for example
    /// <c>https://familiar.taila1d25f.ts.net</c>. Scheme and host only; no path, no trailing slash.
    ///
    /// <b>Configured, never inferred from the request.</b> Every OAuth document this server publishes
    /// contains absolute URLs, and deriving them from the <c>Host</c> header would let anyone who can
    /// reach the server rewrite its issuer, its token endpoint and its audience by sending a header.
    /// That is host-header injection with an authorization server on the end of it. One env line is
    /// cheaper than the class of bug.
    ///
    /// Unset means no OAuth. The static bearer token continues to work and the discovery documents are
    /// not mapped at all, so a deployment that has not opted in has no OAuth surface to probe.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// How long an issued access token is good for. Short by intent: the MCP authorization spec asks
    /// for short-lived access tokens precisely because they travel to a vendor's servers, and a
    /// refresh token exists so that shortness costs the user nothing.
    /// </summary>
    public int AccessTokenLifetimeSeconds { get; set; } = 3600;

    /// <summary>How long a refresh token is good for, in days. Rotated on every use.</summary>
    public int RefreshTokenLifetimeDays { get; set; } = 30;

    /// <summary>
    /// Hosts a client may register a redirect URI on, comma-separated. Suffix-matched, so
    /// <c>chatgpt.com</c> also admits <c>sub.chatgpt.com</c> but never <c>notchatgpt.com</c>.
    ///
    /// <b>This is the open-redirection control.</b> Dynamic client registration means an unauthenticated
    /// caller chooses where the authorization code is delivered; without an allowlist, "anywhere" is
    /// the answer, and the user's own approval would be what hands a code to an attacker's host. The
    /// default is the vendor this gate was opened for, plus loopback for local testing.
    /// </summary>
    public string AllowedRedirectHosts { get; set; } = "chatgpt.com,chat.openai.com,openai.com";

    /// <summary>
    /// Whether to record the OAuth surface's traffic to the server log. Off by default and meant to be
    /// turned off again — it exists to diagnose a client that reports only that it failed. It never
    /// records the Authorization header, a query string, or any body but registration's, which is
    /// public client metadata. See <see cref="OAuth.FamiliarOAuthRequestLog"/>.
    /// </summary>
    public bool LogOAuthRequests { get; set; }

    /// <summary>
    /// Reading what the Familiar holds: projects, their state, and recorded context. This is the whole
    /// of Sprint 14's grant and it remains exactly that — a token carrying only this scope can change
    /// nothing, and no later slice may quietly add to what it permits.
    /// </summary>
    public const string ReadScope = "familiar.read";

    /// <summary>
    /// Relaying a decision the human has explicitly made, to a workflow gate that already exists.
    ///
    /// <b>What this is not.</b> It is not write access, it is not "the model may approve work", and it
    /// is not a licence to mutate a task. It is permission to <em>ask</em> — to carry a person's stated
    /// choice, against one identified object at one observed revision, to the same service the
    /// Demiplane posts to. Find Familiar re-decides legality inside the transaction either way, so a
    /// client holding this scope is a courier, never an authority.
    ///
    /// <b>Separate from <see cref="ReadScope"/> on purpose.</b> A conversational client is asked to
    /// read constantly and to decide rarely, and those two deserve different answers from the person
    /// granting them. Folding them into one grant would mean every read connection silently carried
    /// the ability to act, and the consent screen could no longer honestly say "read-only" to anyone.
    ///
    /// As of Slice 1 no operation accepts this scope. It exists so that the boundary is established
    /// and reviewable before anything consequential stands behind it.
    /// </summary>
    public const string DecideScope = "familiar.decide";

    /// <summary>
    /// Every scope this server will issue. An authorization request naming anything else is refused
    /// rather than silently reduced to what is understood: a client that asked for a permission this
    /// server does not have should be told so, not handed a token that quietly means less.
    /// </summary>
    public static readonly IReadOnlyList<string> SupportedScopes = [ReadScope, DecideScope];

    /// <summary>
    /// Parses a space-delimited scope string into the grant it represents.
    ///
    /// An absent or empty request means <see cref="ReadScope"/> — the historical default, so a client
    /// written against Sprint 14.1 keeps working and keeps getting exactly what it used to get. An
    /// unrecognised scope fails the whole request; duplicates collapse; order is not significant.
    /// </summary>
    public static bool TryParseScopes(string? requested, out IReadOnlyList<string> scopes)
    {
        scopes = [];

        if (string.IsNullOrWhiteSpace(requested))
        {
            scopes = [ReadScope];
            return true;
        }

        var parsed = new List<string>();

        foreach (var candidate in requested.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!SupportedScopes.Contains(candidate, StringComparer.Ordinal))
            {
                return false;
            }

            if (!parsed.Contains(candidate, StringComparer.Ordinal))
            {
                parsed.Add(candidate);
            }
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        scopes = parsed;
        return true;
    }

    /// <summary>Canonical wire form: the granted scopes, space-delimited, in declaration order.</summary>
    public static string FormatScopes(IEnumerable<string> scopes) =>
        string.Join(' ', SupportedScopes.Where(supported => scopes.Contains(supported, StringComparer.Ordinal)));

    /// <summary>
    /// The issuer, or null when this deployment has not opted into OAuth or configured it wrongly.
    /// Validated rather than trusted: an absolute https origin with no path, no query, no fragment.
    /// Loopback over http is admitted so the flow can be exercised on the machine itself.
    /// </summary>
    public string? ResolvedIssuer
    {
        get
        {
            if (!IsConfigured() || string.IsNullOrWhiteSpace(PublicBaseUrl))
            {
                return null;
            }

            if (!Uri.TryCreate(PublicBaseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var uri))
            {
                return null;
            }

            var secure = uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback;

            return secure && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
                ? uri.GetLeftPart(UriPartial.Authority)
                : null;
        }
    }

    /// <summary>
    /// The canonical resource identifier of this MCP server (RFC 8707 / RFC 9728) — the issuer plus the
    /// MCP route, with no trailing slash. This is the audience every access token is bound to and the
    /// value a token is checked against, so a token minted for some other resource cannot be spent here.
    /// </summary>
    public string? ResolvedResource =>
        ResolvedIssuer is { } issuer ? issuer + FamiliarMcpEndpoint.Route : null;

    public bool IsOAuthConfigured() => ResolvedIssuer is not null;

    public IReadOnlyList<string> ResolvedAllowedRedirectHosts =>
        (AllowedRedirectHosts ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(host => host.ToLowerInvariant())
            .ToList();
}
