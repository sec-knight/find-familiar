using FindFamiliar.Server.Api.Gateway.OAuth;

namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// What the credential on this request actually permits, decided once by
/// <see cref="FamiliarGatewayAuthenticationFilter"/> and read everywhere else.
///
/// <b>Why this is a type and not a string comparison at each call site.</b> The gateway's two adapters
/// have different shapes: REST is a route per operation, where a filter can guard the route, while MCP
/// is one route carrying every tool, where it cannot. A future decision tool therefore cannot inherit
/// its authorization from routing, and the alternative — each tool checking scopes itself — is how one
/// tool eventually forgets. One object, attached to the request by the one filter that authenticated
/// it, gives both adapters the same answer from the same place.
///
/// <b>It never widens.</b> This records what was granted; it cannot grant. A credential that arrived
/// with only <see cref="FamiliarGatewayOptions.ReadScope"/> produces a caller that can only read, and
/// there is no path here that adds a scope to one.
/// </summary>
public sealed record FamiliarGatewayCaller(FamiliarGatewayCredentialKind Kind, IReadOnlyList<string> Scopes)
{
    private const string ItemKey = "FindFamiliar.GatewayCaller";

    public bool Has(string scope) => Scopes.Contains(scope, StringComparer.Ordinal);

    public bool CanRead => Has(FamiliarGatewayOptions.ReadScope);

    /// <summary>
    /// Whether this caller may relay a human decision. As of Slice 1 nothing consults this — no
    /// operation exists that a decision scope would unlock — and it is defined now so that the
    /// boundary is reviewable before anything consequential stands behind it.
    /// </summary>
    public bool CanDecide => Has(FamiliarGatewayOptions.DecideScope);

    public void AttachTo(HttpContext context) => context.Items[ItemKey] = this;

    /// <summary>
    /// The caller for this request, or null when the request never passed the gateway filter. Null is
    /// not "unrestricted": every consumer must treat it as no permission at all, which is why this
    /// returns null rather than an empty-scoped caller that could be mistaken for a valid one.
    /// </summary>
    public static FamiliarGatewayCaller? From(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as FamiliarGatewayCaller : null;

    /// <summary>
    /// The static deployment token: the credential a person pastes into a terminal on this machine.
    ///
    /// <b>Read-only, permanently.</b> It does not expire, it is not bound to a browser flow, and nobody
    /// approved a consent screen to obtain it — so it cannot be evidence that a human decided anything.
    /// The whole point of the decision scope is that it is granted deliberately, once, by a person
    /// reading what they are granting; a credential that skipped that step must not satisfy it.
    /// </summary>
    public static FamiliarGatewayCaller StaticToken() =>
        new(FamiliarGatewayCredentialKind.StaticToken, [FamiliarGatewayOptions.ReadScope]);

    public static FamiliarGatewayCaller OAuth(FamiliarOAuthArtifacts.Payload accessToken) =>
        new(FamiliarGatewayCredentialKind.OAuthAccessToken, FamiliarOAuthArtifacts.ScopesOf(accessToken));
}

/// <summary>
/// Which of the two credentials authenticated a request. Recorded rather than inferred, because a
/// later slice must be able to say in a durable record how a decision arrived — a token a human
/// approved through consent is different provenance from a secret pasted into a shell, even when both
/// are valid.
/// </summary>
public enum FamiliarGatewayCredentialKind
{
    StaticToken,
    OAuthAccessToken
}
