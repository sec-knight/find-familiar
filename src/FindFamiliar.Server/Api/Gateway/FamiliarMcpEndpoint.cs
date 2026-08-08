using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// Where the MCP transport is mounted, and what stands in front of it.
///
/// <b>The same filter the REST adapter uses.</b> The MCP SDK offers its own OAuth-shaped
/// authentication, and this deployment does not use it: OAuth here would mean standing up an
/// authorization server, a redirect surface and a token store for one user with one client, which is
/// more moving parts to get wrong than a bearer token this user rotates by editing one line. ADR-0016
/// records that decision and what would reverse it — a second person, a second client, or any need to
/// grant partial access.
///
/// Applied to the mapped endpoint rather than inside a tool, so it runs before the protocol layer
/// looks at the body. An unauthenticated caller cannot enumerate the tool list, which is itself worth
/// protecting: the names and descriptions of these tools describe what this user keeps.
/// </summary>
public static class FamiliarMcpEndpoint
{
    /// <summary>The path a client connects to. One route; Streamable HTTP uses it for both directions.</summary>
    public const string Route = "/mcp";

    public static void MapFamiliarMcpEndpoint(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FamiliarGatewayOptions>>().Value;

        if (!options.Enabled)
        {
            return;
        }

        // Mounted inside a filtered group rather than filtered afterwards. MapMcp returns the
        // non-generic convention builder, and putting the credential check on the group means every
        // route the transport adds — now or in a later SDK version — is behind it by construction.
        // Authentication on the group; the scope on each tool.
        //
        // Slice 2 put the read scope here, which was right while every tool was a read. It stops being
        // right the moment two tools need different permissions: MapMcp puts the whole tool set behind
        // one route, so a group-level scope is necessarily the same answer for all of them — and the
        // decision tool must require familiar.decide while the read tools must not.
        //
        // So the requirement moved one level down, to a single helper every tool calls as its first
        // statement (see FamiliarMcpTools.Require). That is still one implementation rather than four,
        // and a tool that forgets it is a tool that reaches the gateway with no permission checked —
        // which is why the test asserting every tool refuses a credential lacking its scope is not
        // optional.
        var group = app
            .MapGroup(Route)
            .AddEndpointFilter<FamiliarGatewayAuthenticationFilter>();

        group.MapMcp();
    }
}
