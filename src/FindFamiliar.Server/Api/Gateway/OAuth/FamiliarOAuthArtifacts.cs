using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FindFamiliar.Server.Api.Gateway;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway.OAuth;

/// <summary>
/// Everything this authorization server hands out: client identifiers, authorization requests,
/// authorization codes, access tokens and refresh tokens. All five are the same thing — a small JSON
/// payload with an HMAC over it — and none of them is a row in a table.
///
/// <b>Stateless on purpose, and the purpose is not elegance.</b> This deployment is one person, one
/// client, one resource, restarted by a systemd unit whenever a build lands. A client store would mean
/// a migration, a table, and a failure mode where restarting the server silently unpairs ChatGPT and
/// the user is told only that the connector stopped working. Signing instead means the server can
/// forget everything and still recognise what it issued.
///
/// <b>The signing key is derived from the gateway token, not stored beside it.</b> There is no second
/// secret to generate, back up or leak, and rotating <c>FamiliarGateway__Token</c> invalidates every
/// artifact this server ever issued — which is exactly what rotating a credential should mean. HKDF
/// with a per-purpose label means a code can never be replayed as an access token even if an attacker
/// could make the payloads identical: five purposes, five keys, one secret.
///
/// <b>What signing cannot do is single use.</b> A signed code stays valid until it expires, and OAuth
/// 2.1 requires a code be redeemable once. <see cref="FamiliarOAuthReplayGuard"/> is the small piece of
/// state that closes that, and it is in memory rather than in the database because its whole content is
/// worthless sixty seconds later.
/// </summary>
public sealed class FamiliarOAuthArtifacts(IOptions<FamiliarGatewayOptions> options, TimeProvider clock)
{
    /// <summary>
    /// The five kinds. The label goes into key derivation, so a value of one kind presented as another
    /// fails signature verification rather than being caught by a check somebody has to remember.
    /// </summary>
    public enum Purpose
    {
        Client,
        Request,
        Code,
        Access,
        Refresh
    }

    /// <summary>Sixty seconds. A code travels from a browser redirect to a token call; that is seconds.</summary>
    public static readonly TimeSpan AuthorizationCodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long an in-flight authorization request stays signable, covering a human reading a consent
    /// screen and finding the token to paste. Ten minutes, not indefinite: this value is what makes the
    /// consent form's hidden fields tamper-proof, and a stale one should stop working.
    /// </summary>
    public static readonly TimeSpan AuthorizationRequestLifetime = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// The body of every artifact. One shape with nullable members rather than five near-identical
    /// records: the fields that matter differ per purpose, the signature covers all of them either way,
    /// and a reader comparing two kinds should not have to diff two class definitions to do it.
    /// </summary>
    public sealed record Payload
    {
        [JsonPropertyName("jti")] public string Id { get; init; } = string.Empty;

        [JsonPropertyName("iat")] public long IssuedAt { get; init; }

        [JsonPropertyName("exp")] public long ExpiresAt { get; init; }

        /// <summary>The client this artifact belongs to. Absent on a client identifier itself.</summary>
        [JsonPropertyName("cid")] public string? ClientId { get; init; }

        /// <summary>The audience: this server's canonical resource URI. Checked, never merely carried.</summary>
        [JsonPropertyName("aud")] public string? Audience { get; init; }

        [JsonPropertyName("scp")] public string? Scope { get; init; }

        [JsonPropertyName("uris")] public string[]? RedirectUris { get; init; }

        [JsonPropertyName("nm")] public string? ClientName { get; init; }

        [JsonPropertyName("red")] public string? RedirectUri { get; init; }

        [JsonPropertyName("chal")] public string? CodeChallenge { get; init; }

        [JsonPropertyName("st")] public string? State { get; init; }
    }

    private FamiliarGatewayOptions Options => options.Value;

    // ---------------------------------------------------------------- issuing

    public string IssueClientId(IReadOnlyList<string> redirectUris, string? clientName) =>
        Sign(Purpose.Client, new Payload
        {
            Id = NewId(),
            IssuedAt = Now(),

            // A registration does not expire. The client_id is the registration: there is nowhere else
            // for it to live, and an expiring one would unpair ChatGPT on a schedule for no benefit.
            ExpiresAt = 0,
            RedirectUris = [.. redirectUris],
            ClientName = clientName
        });

    public string IssueAuthorizationRequest(string clientId, string redirectUri, string codeChallenge, string? state) =>
        Sign(Purpose.Request, new Payload
        {
            Id = NewId(),
            IssuedAt = Now(),
            ExpiresAt = Now() + (long)AuthorizationRequestLifetime.TotalSeconds,
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            State = state
        });

    public string IssueCode(string clientId, string redirectUri, string codeChallenge) =>
        Sign(Purpose.Code, new Payload
        {
            Id = NewId(),
            IssuedAt = Now(),
            ExpiresAt = Now() + (long)AuthorizationCodeLifetime.TotalSeconds,
            ClientId = clientId,
            RedirectUri = redirectUri,
            CodeChallenge = codeChallenge,
            Audience = Options.ResolvedResource
        });

    public string IssueAccessToken(string clientId) =>
        Sign(Purpose.Access, new Payload
        {
            Id = NewId(),
            IssuedAt = Now(),
            ExpiresAt = Now() + Math.Max(60, Options.AccessTokenLifetimeSeconds),
            ClientId = clientId,
            Audience = Options.ResolvedResource,
            Scope = FamiliarGatewayOptions.ReadScope
        });

    public string IssueRefreshToken(string clientId) =>
        Sign(Purpose.Refresh, new Payload
        {
            Id = NewId(),
            IssuedAt = Now(),
            ExpiresAt = Now() + ((long)Math.Max(1, Options.RefreshTokenLifetimeDays) * 86400),
            ClientId = clientId,
            Audience = Options.ResolvedResource,
            Scope = FamiliarGatewayOptions.ReadScope
        });

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// Verify and decode, or fail. Every rejection is the same <c>false</c> — a caller cannot learn from
    /// this method whether a value was the wrong shape, the wrong purpose, forged, or merely expired.
    /// </summary>
    public bool TryRead(Purpose purpose, string? value, out Payload payload)
    {
        payload = new Payload();

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.');

        if (parts.Length != 3 || parts[0] != Prefix(purpose))
        {
            return false;
        }

        byte[] payloadBytes;
        byte[] suppliedSignature;

        try
        {
            payloadBytes = Base64UrlDecode(parts[1]);
            suppliedSignature = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeSignature(purpose, parts[0] + "." + parts[1]);

        // Fixed-time over equal-length digests. The comparison happens before the payload is parsed, so
        // a forged value never reaches the deserialiser.
        if (suppliedSignature.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expected))
        {
            return false;
        }

        Payload? decoded;

        try
        {
            decoded = JsonSerializer.Deserialize<Payload>(payloadBytes, PayloadJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (decoded is null || string.IsNullOrEmpty(decoded.Id))
        {
            return false;
        }

        // ExpiresAt of zero means "does not expire", which only the client registration uses.
        if (decoded.ExpiresAt != 0 && decoded.ExpiresAt <= Now())
        {
            return false;
        }

        // Audience is bound at issue and checked here for every artifact that carries one. This is the
        // rule the MCP spec is most insistent about: a token minted for another resource is not a token
        // this server may spend, however valid its signature.
        if (decoded.Audience is not null && decoded.Audience != Options.ResolvedResource)
        {
            return false;
        }

        payload = decoded;
        return true;
    }

    // ---------------------------------------------------------------- PKCE

    /// <summary>
    /// S256 only. The <c>plain</c> method is in the RFC and is not offered here: it protects against
    /// nothing this deployment faces, and advertising it would let a client downgrade to it.
    /// </summary>
    public static bool VerifyCodeChallenge(string codeChallenge, string codeVerifier)
    {
        if (string.IsNullOrEmpty(codeChallenge) || string.IsNullOrEmpty(codeVerifier))
        {
            return false;
        }

        // RFC 7636 bounds the verifier; a value outside them is malformed rather than merely wrong.
        if (codeVerifier.Length is < 43 or > 128)
        {
            return false;
        }

        var computed = Encoding.ASCII.GetBytes(
            Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))));
        var supplied = Encoding.ASCII.GetBytes(codeChallenge);

        return computed.Length == supplied.Length && CryptographicOperations.FixedTimeEquals(computed, supplied);
    }

    // ---------------------------------------------------------------- internals

    private long Now() => clock.GetUtcNow().ToUnixTimeSeconds();

    private static string NewId() => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

    private static string Prefix(Purpose purpose) => purpose switch
    {
        Purpose.Client => "ffc1",
        Purpose.Request => "ffq1",
        Purpose.Code => "ffk1",
        Purpose.Access => "ffa1",
        Purpose.Refresh => "ffr1",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose))
    };

    private string Sign(Purpose purpose, Payload payload)
    {
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson));
        var signingInput = Prefix(purpose) + "." + body;

        return signingInput + "." + Base64UrlEncode(ComputeSignature(purpose, signingInput));
    }

    private byte[] ComputeSignature(Purpose purpose, string signingInput) =>
        HMACSHA256.HashData(DeriveKey(purpose), Encoding.UTF8.GetBytes(signingInput));

    /// <summary>
    /// One secret, five keys. The gateway token is the input keying material and the purpose is the
    /// info label, so the key that signs an access token cannot verify a refresh token and neither can
    /// verify a code. If the gateway is unconfigured there is no key and nothing verifies — the same
    /// fail-closed posture the request filter takes.
    /// </summary>
    private byte[] DeriveKey(Purpose purpose)
    {
        var secret = Options.Token?.Trim();

        if (string.IsNullOrEmpty(secret))
        {
            throw new InvalidOperationException("The Familiar gateway has no configured token to derive a key from.");
        }

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            Encoding.UTF8.GetBytes(secret),
            outputLength: 32,
            salt: Encoding.UTF8.GetBytes("find-familiar/oauth/v1"),
            info: Encoding.UTF8.GetBytes(purpose.ToString()));
    }

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }
}
