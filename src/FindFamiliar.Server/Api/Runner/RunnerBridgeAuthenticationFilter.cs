using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Runner;

/// <summary>
/// Endpoint filter applied to the whole "/api/runner" route group. Runs before any
/// task/session lookup so an invalid caller cannot enumerate resources. Never logs the
/// Authorization header, the configured token, the supplied token, a hash of either, or
/// any equality/timing detail.
/// </summary>
public sealed class RunnerBridgeAuthenticationFilter(
    IOptions<RunnerBridgeOptions> options,
    ILogger<RunnerBridgeAuthenticationFilter> logger) : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configuredToken = options.Value.Token;
        if (string.IsNullOrEmpty(configuredToken))
        {
            logger.LogWarning("Runner bridge request rejected: no runner bridge token is configured.");
            return Results.Json(
                RunnerErrorResponse.Create("The runner bridge is not configured."),
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var header = context.HttpContext.Request.Headers.Authorization.ToString();

        if (!TryExtractBearerToken(header, out var suppliedToken) || !FixedTimeTokenEquals(configuredToken, suppliedToken))
        {
            return Results.Json(
                RunnerErrorResponse.Create("Unauthorized."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    private static bool TryExtractBearerToken(string headerValue, out string token)
    {
        token = string.Empty;

        if (string.IsNullOrEmpty(headerValue) || !headerValue.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        token = headerValue[BearerPrefix.Length..];
        return token.Length > 0;
    }

    /// <summary>
    /// Compares SHA-256 digests of equal length with a fixed-time primitive, so neither the
    /// comparison time nor any exception reveals the configured token's length or contents.
    /// </summary>
    private static bool FixedTimeTokenEquals(string configuredToken, string suppliedToken)
    {
        var configuredHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedToken));
        return CryptographicOperations.FixedTimeEquals(configuredHash, suppliedHash);
    }
}
