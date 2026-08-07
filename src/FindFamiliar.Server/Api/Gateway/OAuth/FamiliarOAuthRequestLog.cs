using System.Text;
using FindFamiliar.Server.Api.Gateway;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway.OAuth;

/// <summary>
/// A diagnostic tap on the OAuth surface, off unless <c>FamiliarGateway__LogOAuthRequests</c> is set.
///
/// <b>Why this exists.</b> When a vendor's client says only "Failed to resolve OAuth client", the
/// server's own logs are the only place the truth lives. Guessing at the request shape from forum
/// posts produced three plausible causes and no evidence; one capture settles it.
///
/// <b>What it will and will not write.</b> Registration is unauthenticated and its request body is
/// public client metadata, so that body is logged whole — it is exactly what needs to be seen. Nothing
/// else is: the consent form carries the gateway token, the token endpoint carries codes and
/// verifiers, and <c>/mcp</c> carries an access token on every call, so for those only the method,
/// path, status and a few protocol headers are recorded. The Authorization header is never logged, in
/// any form, and neither is any query string value.
///
/// <b>It is meant to be turned off again.</b> This is a deployment switch for diagnosing one
/// connector, not a permanent audit log.
/// </summary>
public static class FamiliarOAuthRequestLog
{
    /// <summary>Paths whose request body is safe to record: public metadata, no credential.</summary>
    private static readonly string[] BodySafePaths = [FamiliarOAuthEndpoints.RegisterRoute];

    private static readonly string[] InterestingHeaders =
        ["User-Agent", "Accept", "Content-Type", "Origin", "Referer", "Mcp-Protocol-Version"];

    public static void UseFamiliarOAuthRequestLog(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FamiliarGatewayOptions>>().Value;

        if (!options.LogOAuthRequests || !options.IsOAuthConfigured())
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FindFamiliar.Server.Api.Gateway.OAuth.Capture");

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (!IsOAuthSurface(path))
            {
                await next(context);
                return;
            }

            var headers = string.Join(
                "; ",
                InterestingHeaders
                    .Where(name => context.Request.Headers.ContainsKey(name))
                    .Select(name => $"{name}={context.Request.Headers[name]}"));

            var body = string.Empty;

            if (BodySafePaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                && context.Request.ContentLength is > 0 and < 16 * 1024)
            {
                context.Request.EnableBuffering();

                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;
            }

            // Whether an Authorization header was present, never what it contained.
            var authorization = context.Request.Headers.ContainsKey("Authorization") ? "present" : "absent";

            logger.LogInformation(
                "OAUTH-CAPTURE >> {Method} {Path} | authorization={Authorization} | {Headers}{Body}",
                context.Request.Method,
                path,
                authorization,
                headers,
                string.IsNullOrEmpty(body) ? string.Empty : " | body=" + body);

            await next(context);

            logger.LogInformation(
                "OAUTH-CAPTURE << {Method} {Path} -> {Status} {ContentType}",
                context.Request.Method,
                path,
                context.Response.StatusCode,
                context.Response.ContentType);
        });
    }

    private static bool IsOAuthSurface(string path) =>
        path.StartsWith("/oauth", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/.well-known", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(FamiliarMcpEndpoint.Route, StringComparison.OrdinalIgnoreCase);
}
