using FindFamiliar.Server.Api.Gateway.OAuth;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// Requires one scope of whatever credential authenticated the request.
///
/// <b>Declared, not repeated.</b> A route or group states the permission it needs
/// (<c>.RequireFamiliarScope(FamiliarGatewayOptions.DecideScope)</c>) and the check happens here, once.
/// The alternative — each operation reading scopes and deciding for itself — is how the tenth operation
/// eventually ships without the check, and on this surface that would mean an external client acting
/// without the human ever having granted it.
///
/// <b>It runs after authentication and cannot substitute for it.</b> A request that never passed
/// <see cref="FamiliarGatewayAuthenticationFilter"/> has no caller attached, and this refuses it rather
/// than treating the absence as permission.
///
/// <b>403, not 401.</b> The credential was valid; it simply does not carry this permission. Answering
/// 401 would tell a client to go and authenticate again, which would not help and would loop it through
/// a consent screen it already completed. The <c>WWW-Authenticate</c> header names the scope that was
/// missing, so a client can ask for the right thing next time — that is the one detail worth
/// disclosing, and it describes the protocol rather than the credential.
/// </summary>
public sealed class FamiliarGatewayScopeFilter(
    string requiredScope,
    IOptions<FamiliarGatewayOptions> options,
    ILogger<FamiliarGatewayScopeFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var caller = FamiliarGatewayCaller.From(context.HttpContext);

        if (caller is null || !caller.Has(requiredScope))
        {
            // The scope that was missing is logged; the credential and its scopes are not.
            logger.LogWarning(
                "A Familiar gateway request was refused: the credential does not carry {RequiredScope}.",
                requiredScope);

            if (FamiliarOAuthEndpoints.ResourceMetadataUrl(options.Value) is { } metadataUrl)
            {
                context.HttpContext.Response.Headers.WWWAuthenticate =
                    $"Bearer error=\"insufficient_scope\", scope=\"{requiredScope}\", "
                    + $"resource_metadata=\"{metadataUrl}\"";
            }

            return Results.Json(
                new FamiliarGatewayError("This credential does not carry the permission that operation requires."),
                statusCode: StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}

public static class FamiliarGatewayScopeFilterExtensions
{
    /// <summary>
    /// Adds the scope requirement to a route or group. Intended to sit on the group a future decision
    /// operation is mapped into, so that operation is guarded by construction rather than by its author
    /// having remembered — the same reason the credential check is on a group rather than per route.
    /// </summary>
    public static TBuilder RequireFamiliarScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var services = factoryContext.ApplicationServices;
            var filter = new FamiliarGatewayScopeFilter(
                scope,
                services.GetRequiredService<IOptions<FamiliarGatewayOptions>>(),
                services.GetRequiredService<ILogger<FamiliarGatewayScopeFilter>>());

            return invocationContext => filter.InvokeAsync(invocationContext, next);
        });
}
