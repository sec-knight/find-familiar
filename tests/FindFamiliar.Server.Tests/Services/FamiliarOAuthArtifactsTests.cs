using FindFamiliar.Server.Api.Gateway;
using FindFamiliar.Server.Api.Gateway.OAuth;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The signing layer, tested where the properties actually live.
///
/// Expiry is the reason this file exists rather than an HTTP test. An access token's whole security
/// argument is that it stops working, and proving that over the wire would mean either sleeping for an
/// hour or weakening the deployment's lifetime to make a test convenient. A controllable clock proves
/// it in a millisecond, and proves it at the boundary the rule is enforced at.
/// </summary>
public sealed class FamiliarOAuthArtifactsTests
{
    private const string Secret = "ffa-test-fixture-familiar-gateway-token-not-a-real-secret";
    private const string OtherSecret = "ffa-test-fixture-a-completely-different-gateway-token-x";

    [Fact]
    public void An_issued_access_token_reads_back_with_its_audience_and_scope()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var artifacts = Build(clock, out var options);

        var token = artifacts.IssueAccessToken("client-1", FamiliarGatewayOptions.ReadScope);

        Assert.True(artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Access, token, out var payload));
        Assert.Equal(options.ResolvedResource, payload.Audience);
        Assert.Equal(FamiliarGatewayOptions.ReadScope, payload.Scope);
        Assert.Equal("client-1", payload.ClientId);
    }

    /// <summary>The property the deployment's security actually rests on: it stops working.</summary>
    [Fact]
    public void An_access_token_stops_verifying_once_it_expires()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var artifacts = Build(clock, out var options);

        var token = artifacts.IssueAccessToken("client-1", FamiliarGatewayOptions.ReadScope);

        clock.Advance(TimeSpan.FromSeconds(options.AccessTokenLifetimeSeconds - 5));
        Assert.True(artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Access, token, out _));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Access, token, out _));
    }

    [Fact]
    public void An_authorization_code_expires_within_a_minute()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var artifacts = Build(clock, out _);

        var code = artifacts.IssueCode("client-1", "https://chatgpt.com/cb", "challenge", FamiliarGatewayOptions.ReadScope);

        clock.Advance(FamiliarOAuthArtifacts.AuthorizationCodeLifetime + TimeSpan.FromSeconds(1));

        Assert.False(artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Code, code, out _));
    }

    /// <summary>
    /// Five purposes, five derived keys. This is what makes "a code is not an access token" a fact about
    /// the cryptography rather than a check somebody has to remember to write.
    /// </summary>
    [Fact]
    public void An_artifact_of_one_purpose_never_verifies_as_another()
    {
        var artifacts = Build(new FixedClock(DateTimeOffset.UtcNow), out _);

        var issued = new (FamiliarOAuthArtifacts.Purpose Purpose, string Value)[]
        {
            (FamiliarOAuthArtifacts.Purpose.Access, artifacts.IssueAccessToken("c", FamiliarGatewayOptions.ReadScope)),
            (FamiliarOAuthArtifacts.Purpose.Refresh, artifacts.IssueRefreshToken("c", FamiliarGatewayOptions.ReadScope)),
            (FamiliarOAuthArtifacts.Purpose.Code, artifacts.IssueCode("c", "https://chatgpt.com/cb", "ch", FamiliarGatewayOptions.ReadScope)),
            (FamiliarOAuthArtifacts.Purpose.Client, artifacts.IssueClientId(["https://chatgpt.com/cb"], "ChatGPT")),
            (FamiliarOAuthArtifacts.Purpose.Request, artifacts.IssueAuthorizationRequest("c", "https://chatgpt.com/cb", "ch", null, FamiliarGatewayOptions.ReadScope))
        };

        foreach (var (purpose, value) in issued)
        {
            foreach (var other in Enum.GetValues<FamiliarOAuthArtifacts.Purpose>())
            {
                var accepted = artifacts.TryRead(other, value, out _);

                Assert.Equal(purpose == other, accepted);
            }
        }
    }

    /// <summary>
    /// Rotating the gateway token revokes everything issued under the old one. That is what rotating a
    /// credential is supposed to mean, and here it falls out of key derivation rather than needing a
    /// revocation list.
    /// </summary>
    [Fact]
    public void Rotating_the_gateway_token_invalidates_every_artifact_it_signed()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var before = Build(clock, out _);
        var token = before.IssueAccessToken("client-1", FamiliarGatewayOptions.ReadScope);

        var after = Build(clock, out _, secret: OtherSecret);

        Assert.False(after.TryRead(FamiliarOAuthArtifacts.Purpose.Access, token, out _));
    }

    /// <summary>
    /// A token minted by a server whose resource is a different URL is not spendable here, even though
    /// this server would happily verify its signature if the secret were shared.
    /// </summary>
    [Fact]
    public void A_token_bound_to_another_resource_is_refused()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var elsewhere = Build(clock, out _, publicBaseUrl: "https://somewhere-else.test");

        var token = elsewhere.IssueAccessToken("client-1", FamiliarGatewayOptions.ReadScope);
        var here = Build(clock, out _);

        Assert.False(here.TryRead(FamiliarOAuthArtifacts.Purpose.Access, token, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("ffa1.only-two-parts")]
    [InlineData("ffa1.!!!not-base64!!!.signature")]
    [InlineData("ffa1.e30.forged")]
    public void A_malformed_or_forged_value_is_refused_without_throwing(string value)
    {
        var artifacts = Build(new FixedClock(DateTimeOffset.UtcNow), out _);

        Assert.False(artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Access, value, out _));
    }

    // ---------------------------------------------------------------- PKCE

    [Fact]
    public void A_matching_verifier_satisfies_its_challenge()
    {
        var verifier = new string('a', 64);
        var challenge = FamiliarOAuthArtifacts.Base64UrlEncode(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        Assert.True(FamiliarOAuthArtifacts.VerifyCodeChallenge(challenge, verifier));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public void A_verifier_outside_the_rfc_bounds_is_refused(string verifier)
    {
        var challenge = FamiliarOAuthArtifacts.Base64UrlEncode(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)));

        Assert.False(FamiliarOAuthArtifacts.VerifyCodeChallenge(challenge, verifier));
    }

    [Fact]
    public void A_wrong_verifier_does_not_satisfy_a_challenge()
    {
        var challenge = FamiliarOAuthArtifacts.Base64UrlEncode(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(new string('a', 64))));

        Assert.False(FamiliarOAuthArtifacts.VerifyCodeChallenge(challenge, new string('b', 64)));
    }

    // ---------------------------------------------------------------- replay guard

    [Fact]
    public void An_identifier_can_be_spent_exactly_once()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var guard = new FamiliarOAuthReplayGuard(clock);
        var expiry = clock.GetUtcNow().AddMinutes(1);

        Assert.True(guard.TrySpend("id-1", expiry));
        Assert.False(guard.TrySpend("id-1", expiry));
        Assert.True(guard.TrySpend("id-2", expiry));
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarOAuthArtifacts Build(
        TimeProvider clock,
        out FamiliarGatewayOptions options,
        string secret = Secret,
        string publicBaseUrl = "https://familiar.test")
    {
        options = new FamiliarGatewayOptions
        {
            Enabled = true,
            Token = secret,
            PublicBaseUrl = publicBaseUrl
        };

        return new FamiliarOAuthArtifacts(Options.Create(options), clock);
    }

    /// <summary>
    /// A clock the test moves. Written here rather than pulled in as a package because it is nine lines
    /// and the suite has no other need for a time-testing dependency.
    /// </summary>
    private sealed class FixedClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan amount) => _now = _now.Add(amount);
    }
}
