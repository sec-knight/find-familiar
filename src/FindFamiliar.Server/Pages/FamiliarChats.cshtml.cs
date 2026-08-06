using FindFamiliar.Server.Services.Familiar.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FindFamiliar.Server.Pages;

/// <summary>
/// <c>/Familiar</c> — every conversation, and the place a new one starts.
///
/// System-wide by construction: there is no project id in the route, and there never was one to
/// generalise away later. A conversation may lean towards a project, and that lean never restricts
/// what it can see; cross-project questions are the point.
///
/// The list is read from the server on every render, so every device sees the same set. Nothing about
/// which conversations exist is remembered in a browser.
///
/// <c>GET</c> writes nothing on any branch — no conversation row is created to make the page tidier.
/// The one write is <c>OnPostStart</c>, with antiforgery.
/// </summary>
public sealed class FamiliarChatsModel(IFamiliarChatService chats) : PageModel
{
    public IReadOnlyList<FamiliarChatSummary> Chats { get; private set; } = [];

    /// <summary>The opening message being composed. The only value bound from the form.</summary>
    [BindProperty]
    public string? Message { get; set; }

    /// <summary>Shown beside the textarea when a send was refused. Authored by the service.</summary>
    public string? SendValidationMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Chats = await chats.ListAsync(cancellationToken);

    /// <summary>
    /// Starts a conversation with its first message, and redirects to it.
    ///
    /// A conversation and its opening turn are created together, so the list never holds an empty
    /// row nobody spoke into. Post/redirect/get, so a refresh does not send again.
    /// </summary>
    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
    {
        var result = await chats.SendAsync(null, Message ?? string.Empty, null, cancellationToken);

        switch (result.Status)
        {
            case FamiliarChatSendStatus.Accepted:
                return RedirectToPage("/FamiliarChat", new { chatId = result.ChatId });

            case FamiliarChatSendStatus.Invalid:
                // Re-render in place so the person keeps what they typed and sees why it was refused.
                SendValidationMessage = result.ValidationMessage;
                Chats = await chats.ListAsync(cancellationToken);
                return Page();

            default:
                // No competitor is claimed, because none has been established.
                TempData["StatusMessage"] =
                    "The database was busy and the conversation was not started. Nothing was changed — try again.";
                return RedirectToPage();
        }
    }
}
