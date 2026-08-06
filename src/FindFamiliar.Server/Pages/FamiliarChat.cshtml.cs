using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// <c>/Familiar/Chat/{chatId}</c> — one conversation, read from the server.
///
/// The server is the conversation and this page is a window onto it. Everything rendered comes from
/// <see cref="IFamiliarChatService"/> already ordered by sequence, so a reload, a service restart and
/// a second device all produce the same page. Nothing that matters lives in the browser.
///
/// A send commits a turn and returns; the reply is generated out of band. That means this page can be
/// closed the instant after sending without losing the answer, which is the property slice 1 exists
/// to establish.
///
/// <c>GET</c> writes nothing on any branch. The one write is <c>OnPostSend</c>, with antiforgery.
/// </summary>
public sealed class FamiliarChatModel(IFamiliarChatService chats) : PageModel
{
    /// <summary>
    /// How long the page waits before re-reading while a turn is in flight.
    ///
    /// A meta refresh, as the Demiplane uses, and deliberately not a stream: slice 1 has no provider
    /// and therefore no tokens to stream. Slice 2 replaces this with SSE resuming from
    /// <see cref="Cursor"/>, which is why the cursor is already rendered here.
    /// </summary>
    public const int InFlightRefreshSeconds = 3;

    public FamiliarChatView? Chat { get; private set; }

    /// <summary>The message being composed. The only value bound from the send form.</summary>
    [BindProperty]
    public string? Message { get; set; }

    public string? SendValidationMessage { get; private set; }

    /// <summary>The highest sequence this render contains — what a client resumes from.</summary>
    public int Cursor => Chat?.LatestSequence ?? 0;

    /// <summary>True when a reply is being generated, on this device or any other.</summary>
    public bool HasTurnInFlight => Chat?.InFlightTurn is not null;

    public async Task<IActionResult> OnGetAsync(Guid chatId, CancellationToken cancellationToken) =>
        await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();

    private async Task<bool> LoadAsync(Guid chatId, CancellationToken cancellationToken)
    {
        Chat = await chats.GetAsync(chatId, cancellationToken);
        return Chat is not null;
    }

    public async Task<IActionResult> OnPostSendAsync(Guid chatId, CancellationToken cancellationToken)
    {
        var result = await chats.SendAsync(chatId, Message ?? string.Empty, null, cancellationToken);

        switch (result.Status)
        {
            case FamiliarChatSendStatus.Accepted:
                return RedirectToPage(new { chatId });

            case FamiliarChatSendStatus.ChatNotFound:
                return NotFound();

            case FamiliarChatSendStatus.Invalid:
                SendValidationMessage = result.ValidationMessage;
                return await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();

            case FamiliarChatSendStatus.Attached:
                // Attached, not queued. The message stays in the composer rather than being written
                // behind a reply that is still arriving — nothing typed is lost, and nothing is sent
                // that the person has not seen the answer to.
                SendValidationMessage =
                    "A reply is still arriving on this conversation, so this was not sent. "
                    + "It is still in the box — send it once the reply finishes.";
                return await LoadAsync(chatId, cancellationToken) ? Page() : NotFound();

            default:
                TempData["StatusMessage"] =
                    "The database was busy and your message was not sent. Nothing was changed — try again.";
                return RedirectToPage(new { chatId });
        }
    }

    /// <summary>
    /// What a turn's state means, in the page's own words rather than the enum's. A failed turn says
    /// plainly that nothing was answered; it never speaks in the Familiar's voice.
    /// </summary>
    public static string StateLabel(FamiliarChatTurnState state) => state switch
    {
        FamiliarChatTurnState.Pending => "Queued",
        FamiliarChatTurnState.Generating => "Replying",
        FamiliarChatTurnState.Completed => "Familiar",
        _ => "Find Familiar"
    };

    public static string StateCss(FamiliarChatTurnState state) => state switch
    {
        FamiliarChatTurnState.Completed => "is-familiar",
        FamiliarChatTurnState.Failed => "is-system",
        _ => "is-pending"
    };
}
