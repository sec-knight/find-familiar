using FindFamiliar.Server.Services.Familiar.Chat;

namespace FindFamiliar.Server.Api.Familiar;

/// <summary>
/// The resume path: one endpoint, taken by every client on every reconnection.
///
/// "Give me everything after sequence N" is the same request whether the caller has been gone four
/// seconds or four hours, so the path that recovers a phone which slept through a reply is the path
/// exercised constantly rather than the one nobody runs until it matters. A client that computes a
/// cursor from what it has rendered can always ask again from it; there is no other state to hold.
///
/// Read-only, on every branch. Sprint 12's talk lane changes nothing, and this is the only HTTP
/// surface it adds outside the Razor pages.
/// </summary>
public static class FamiliarChatEndpoints
{
    public static IEndpointRouteBuilder MapFamiliarChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/familiar/chats", async (
            IFamiliarChatService chats,
            CancellationToken cancellationToken) =>
            Results.Ok(await chats.ListAsync(cancellationToken)));

        endpoints.MapGet("/api/familiar/chats/{chatId:guid}/turns", async (
            Guid chatId,
            int? after,
            IFamiliarChatService chats,
            CancellationToken cancellationToken) =>
        {
            // No cursor means from the beginning, which is what a client opening a conversation for
            // the first time asks for. It is the same call, not a different one.
            var page = await chats.ReadTurnsAfterAsync(chatId, after ?? 0, cancellationToken);

            return page is null ? Results.NotFound() : Results.Ok(page);
        });

        return endpoints;
    }
}
