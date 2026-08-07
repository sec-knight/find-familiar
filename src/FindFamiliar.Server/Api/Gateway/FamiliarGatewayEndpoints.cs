using FindFamiliar.Server.Services.Familiar.Gateway;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Gateway;

/// <summary>
/// The REST adapter over <see cref="IFamiliarGateway"/>.
///
/// It exists in the same sprint as the MCP adapter on purpose. The claim this sprint makes is that
/// the gateway is a provider-neutral boundary and MCP is one adapter over it — and a boundary with
/// exactly one consumer is an assertion, not a demonstration. Two adapters serialising the same
/// contracts, sharing the same filter, and holding no policy of their own is the cheapest available
/// proof that nothing about ChatGPT leaked into the domain.
///
/// It is also the surface that can be exercised with <c>curl</c> from the machine itself, which is
/// what makes the gateway verifiable before any decision about exposing anything to the internet.
///
/// <b>Every handler here is a projection and a bound.</b> No filtering, no project selection, no
/// sensitivity decision, no relevance judgement — those live in the gateway and below it, once. A
/// reviewer should be able to read this file and find nothing in it worth arguing about.
/// </summary>
public static class FamiliarGatewayEndpoints
{
    public static void MapFamiliarGatewayEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptions<FamiliarGatewayOptions>>().Value;

        if (!options.Enabled)
        {
            // Not mapped at all rather than mapped-and-refusing. A deployment that has not turned this
            // on has no external surface to probe, and a 404 is a more honest answer than a 401 about
            // a gate that is not there.
            return;
        }

        var group = app
            .MapGroup("/api/gateway")
            .AddEndpointFilter<FamiliarGatewayAuthenticationFilter>();

        group.MapGet("/manifest", (IFamiliarGateway gateway) => Results.Ok(gateway.GetManifest()));

        group.MapPost("/context/search", async (
            FamiliarContextSearchRequest request,
            IFamiliarGateway gateway,
            CancellationToken cancellationToken) =>
            Results.Ok(await gateway.SearchContextAsync(
                request.Query ?? string.Empty,
                request.ProjectId,
                request.MaxItems,
                cancellationToken)));

        group.MapGet("/projects", async (IFamiliarGateway gateway, CancellationToken cancellationToken) =>
            Results.Ok(await gateway.ListProjectsAsync(cancellationToken)));

        group.MapGet("/projects/{projectId:guid}", async (
            Guid projectId,
            IFamiliarGateway gateway,
            CancellationToken cancellationToken) =>
        {
            var project = await gateway.GetProjectContextAsync(projectId, cancellationToken);

            // A project that is sensitive and a project that does not exist answer identically, and
            // that is the point: naming which of the two would itself be the disclosure the
            // sensitivity rule withholds.
            return project is null
                ? Results.NotFound(new FamiliarGatewayError("No readable project has that id."))
                : Results.Ok(project);
        });
    }
}

/// <summary>
/// The body of a context search. A record with nullable members rather than required ones, because a
/// malformed request from an external client should produce an empty, explained result rather than a
/// deserialization failure the client cannot interpret.
/// </summary>
public sealed record FamiliarContextSearchRequest(string? Query, Guid? ProjectId, int? MaxItems);
