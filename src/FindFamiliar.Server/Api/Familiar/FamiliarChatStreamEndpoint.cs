using System.Text.Json;
using FindFamiliar.Server.Services.Familiar.Chat;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Api.Familiar;

/// <summary>
/// The streaming half of the resume path: everything after sequence N, then whatever follows.
///
/// Deliberately one endpoint with the same contract as the JSON read beside it. A client that has
/// been gone four seconds and one gone four hours send the same request with a different cursor, so
/// the recovery path is the ordinary path and is exercised on every reconnection rather than only
/// when something has gone wrong.
///
/// The stream is a <i>view</i> of the persisted turn, not the generation itself. It polls the row and
/// emits what changed; the generation is happening in the background host regardless of whether
/// anybody is connected. That is why closing a laptop mid-reply loses nothing, and why two devices
/// watching the same conversation both see it — there is no single connection that owns the work.
///
/// Polling rather than an in-process event bus, on purpose. The database is already the single source
/// of truth for what a turn contains, and a notification channel would be a second one that could
/// disagree with it. A poll that reads a row every 300ms is not the expensive part of a lane whose
/// other end is a language model.
/// </summary>
public static class FamiliarChatStreamEndpoint
{
    /// <summary>How often the persisted turn is re-read while a reply is arriving.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How often a comment frame is sent when nothing has changed.
    ///
    /// Proxies and mobile networks drop connections that go quiet, and a dead connection that looks
    /// alive is worse than one that obviously needs reconnecting — the client sees the drop and
    /// resumes from its cursor.
    /// </summary>
    public static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// A bound on one connection's life, after which the client reconnects from its cursor.
    ///
    /// Not a limitation being worked around — it is the resume path being exercised deliberately and
    /// often, so it cannot rot. A stream that could live forever would mean reconnection was only
    /// ever tested by accident.
    /// </summary>
    public static readonly TimeSpan MaxConnectionLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapFamiliarChatStreamEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/familiar/chats/{chatId:guid}/stream", async (
            Guid chatId,
            int? after,
            HttpContext context,
            IFamiliarChatService chats,
            TimeProvider timeProvider,
            IOptions<JsonOptions> jsonOptions,
            CancellationToken cancellationToken) =>
        {
            var cursor = Math.Max(after ?? 0, 0);

            // Existence is settled before any header is written, so an unknown conversation is a 404
            // rather than a 200 carrying an error inside a stream a client would have to parse.
            if (await chats.ReadTurnsAfterAsync(chatId, cursor, cancellationToken) is not { } initial)
            {
                return Results.NotFound();
            }

            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";

            // Nginx and friends buffer proxied responses by default, which turns a stream into one
            // delivery at the end. Harmless when nothing is proxying.
            context.Response.Headers["X-Accel-Buffering"] = "no";

            await WriteStreamAsync(
                context,
                chats,
                chatId,
                cursor,
                initial,
                timeProvider,
                jsonOptions.Value.SerializerOptions,
                cancellationToken);

            return Results.Empty;
        });

        return endpoints;
    }

    private static async Task WriteStreamAsync(
        HttpContext context,
        IFamiliarChatService chats,
        Guid chatId,
        int cursor,
        FamiliarChatTurnPage initial,
        TimeProvider timeProvider,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        var lastWriteAt = startedAt;

        // The gap first, in one frame: everything the client missed while it was away. A suspended
        // tab that wakes far behind is caught up by the same mechanism as a fresh connection.
        var lastPage = initial;
        await WriteEventAsync(context, "turns", initial, serializerOptions, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (timeProvider.GetElapsedTime(startedAt) >= MaxConnectionLifetime)
            {
                // A clean, expected close. The client reconnects from its own cursor, which is the
                // same thing it does after a real network drop.
                await WriteCommentAsync(context, "reconnect", cancellationToken);
                return;
            }

            try
            {
                await Task.Delay(PollInterval, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var page = await chats.ReadTurnsAfterAsync(chatId, cursor, cancellationToken);

            if (page is null)
            {
                // The conversation went away underneath the stream. Ending is the honest response;
                // the client's next resume gets a 404 and can say so.
                return;
            }

            if (HasChanged(lastPage, page))
            {
                await WriteEventAsync(context, "turns", page, serializerOptions, cancellationToken);
                lastPage = page;
                lastWriteAt = timeProvider.GetTimestamp();

                // Nothing is in flight and the client is level with the head: there is nothing more
                // to send until it asks again.
                if (!page.HasTurnInFlight)
                {
                    await WriteCommentAsync(context, "idle", cancellationToken);
                    return;
                }

                continue;
            }

            if (!page.HasTurnInFlight)
            {
                await WriteCommentAsync(context, "idle", cancellationToken);
                return;
            }

            if (timeProvider.GetElapsedTime(lastWriteAt) >= KeepAliveInterval)
            {
                await WriteCommentAsync(context, "keep-alive", cancellationToken);
                lastWriteAt = timeProvider.GetTimestamp();
            }
        }
    }

    /// <summary>
    /// Whether anything a client renders has moved.
    ///
    /// Compared on content rather than on a revision column: output grows character by character, and
    /// a stamp that only moved when a turn changed state would leave a reply frozen on screen while
    /// it was still being written.
    /// </summary>
    private static bool HasChanged(FamiliarChatTurnPage previous, FamiliarChatTurnPage current)
    {
        if (previous.LatestSequence != current.LatestSequence
            || previous.HasTurnInFlight != current.HasTurnInFlight
            || previous.Turns.Count != current.Turns.Count)
        {
            return true;
        }

        for (var index = 0; index < current.Turns.Count; index++)
        {
            var before = previous.Turns[index];
            var now = current.Turns[index];

            if (before.State != now.State || before.Output.Length != now.Output.Length)
            {
                return true;
            }
        }

        return false;
    }

    private static async Task WriteEventAsync<T>(
        HttpContext context,
        string name,
        T payload,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        // The application's configured options, so enums serialise as the same strings the JSON read
        // beside this endpoint emits. A client must not have to parse two shapes of the same page.
        var json = JsonSerializer.Serialize(payload, serializerOptions);

        await context.Response.WriteAsync($"event: {name}\n", cancellationToken);
        await context.Response.WriteAsync($"data: {json}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// An SSE comment: ignored by <c>EventSource</c>, but traffic on the wire, which is what keeps an
    /// idle connection from being reaped by something in the middle.
    /// </summary>
    private static async Task WriteCommentAsync(
        HttpContext context,
        string note,
        CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync($": {note}\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
}
