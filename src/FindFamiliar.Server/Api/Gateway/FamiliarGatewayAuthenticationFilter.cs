using System.Security.Cryptography;
using System.Text;
using FindFamiliar.Server.Api.Gateway.OAuth;
using FindFamiliar.Server.Services.Familiar.Gateway;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// The gate on the Summoning Gate. Applied to every gateway route — REST and MCP alike — and to the
/// whole group rather than per-operation, so a route added later is behind it by construction rather
/// than by the author having remembered.
///
/// <b>Fails closed.</b> No configured token means every request is refused, not permitted: this
/// surface exists to be reachable from the public internet, and the failure mode of an
/// authentication check that defaults open is the entire memory of a person's projects.
///
/// <b>Says nothing it does not have to.</b> No branch here logs the header, the configured token, the
/// supplied token, a hash or prefix of either, or the length of anything. A rejected caller is told
/// "Unauthorized" and no more — a message distinguishing "no token" from "wrong token" from "token
/// too short" is a message that helps whoever is guessing. The one thing logged is that a rejection
/// happened, without its input.
///
/// Comparison is fixed-time over SHA-256 digests, the same primitive the runner bridge uses and for
/// the same reason: equal-length inputs mean neither the comparison time nor an exception discloses
/// the configured token's length or contents.
/// </summary>
public sealed class FamiliarGatewayAuthenticationFilter(
    IOptions<FamiliarGatewayOptions> options,
    FamiliarOAuthArtifacts artifacts,
    ILogger<FamiliarGatewayAuthenticationFilter> logger) : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configured = options.Value;

        if (!configured.IsConfigured())
        {
            // Deliberately the same 401 an invalid token gets, with the reason kept to the server's
            // own log. A distinct status here would let an unauthenticated prober learn whether this
            // deployment has a gateway credential at all.
            logger.LogWarning(
                "A Familiar gateway request was refused: the gateway is enabled without a token of at "
                + "least {MinimumLength} characters, or is not enabled.",
                FamiliarGatewayOptions.MinimumTokenLength);

            return Unauthorized(context.HttpContext, configured);
        }

        if (context.HttpContext.Request.ContentLength is { } length && length > configured.MaxRequestBytes)
        {
            // Refused on the declared length, before the body is read. Reading a megabyte to discover
            // it is a megabyte is the cheapest denial of service there is.
            return Results.Json(
                new FamiliarGatewayError("The request body is larger than this gateway accepts."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var header = context.HttpContext.Request.Headers.Authorization.ToString();

        if (!TryExtractBearerToken(header, out var supplied) || !IsAcceptedCredential(configured, supplied))
        {
            return Unauthorized(context.HttpContext, configured);
        }

        return await next(context);
    }

    /// <summary>
    /// Two credentials are accepted and they are not equals.
    ///
    /// The static token is the deployment's own key: the user pasted it, it does not expire, and it is
    /// how a terminal on this machine reaches the gate. An OAuth access token is what a client obtained
    /// through the browser flow: short-lived, bound to this resource as its audience, and revoked
    /// wholesale the moment the static token is rotated, since the key that signs it is derived from it.
    ///
    /// The static comparison runs first and unconditionally, so the time this method takes does not say
    /// which of the two a caller was attempting.
    /// </summary>
    private bool IsAcceptedCredential(FamiliarGatewayOptions configured, string supplied)
    {
        var matchesStaticToken = FixedTimeTokenEquals(configured.Token!.Trim(), supplied);

        if (matchesStaticToken)
        {
            return true;
        }

        return configured.IsOAuthConfigured()
            && artifacts.TryRead(FamiliarOAuthArtifacts.Purpose.Access, supplied, out _);
    }

    /// <summary>
    /// The refusal, plus the one thing a refusal is allowed to disclose: where to go and ask properly.
    ///
    /// RFC 9728 §5.1 and the MCP authorization spec both require a 401 from a protected resource to
    /// carry <c>WWW-Authenticate</c> naming the resource metadata URL — it is how a client that has
    /// never seen this server discovers there is an authorization server at all. It says nothing about
    /// the credential, only about the protocol, and it is omitted entirely when this deployment has no
    /// OAuth configured.
    /// </summary>
    private static IResult Unauthorized(HttpContext context, FamiliarGatewayOptions configured)
    {
        if (FamiliarOAuthEndpoints.ResourceMetadataUrl(configured) is { } metadataUrl)
        {
            context.Response.Headers.WWWAuthenticate =
                $"Bearer resource_metadata=\"{metadataUrl}\", scope=\"{FamiliarGatewayOptions.ReadScope}\"";
        }

        return Results.Json(new FamiliarGatewayError("Unauthorized."), statusCode: StatusCodes.Status401Unauthorized);
    }

    private static bool TryExtractBearerToken(string headerValue, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrEmpty(headerValue) || !headerValue.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        token = headerValue[BearerPrefix.Length..].Trim();
        return token.Length > 0;
    }

    private static bool FixedTimeTokenEquals(string configuredToken, string suppliedToken)
    {
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));

        return CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash);
    }
}

/// <summary>
/// What a refused caller is told: one sentence, never a field of the request, never a hint about the
/// credential. Shaped like the runner bridge's error so the two surfaces fail the same way.
/// </summary>
public sealed record FamiliarGatewayError(string Error);
