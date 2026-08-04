using System.ComponentModel.DataAnnotations;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Pages.Talk;

public sealed class IndexModel(
    FamiliarDbContext dbContext,
    IConversationIntakeService intake) : PageModel
{
    public IReadOnlyList<ConversationListItem> Conversations { get; private set; } =
        Array.Empty<ConversationListItem>();

    [BindProperty]
    public WorkRequestInput NewRequest { get; set; } = new();

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadConversationsAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostStartAsync(CancellationToken cancellationToken)
    {
        // The whole point of this handler: it may create a conversation, a message and a proposal,
        // and nothing else. No task, no session, no context entry, no provider call.
        var outcome = await intake.CreateAsync(new ConversationIntakeRequest(NewRequest.Request), cancellationToken);

        if (outcome.Status == ConversationIntakeStatus.Success)
        {
            return RedirectToPage("./Details", new { id = outcome.ConversationId });
        }

        foreach (var (field, message) in outcome.ValidationErrors ?? new Dictionary<string, string>())
        {
            ModelState.AddModelError($"NewRequest.{field}", message);
        }

        await LoadConversationsAsync(cancellationToken);
        return Page();
    }

    private async Task LoadConversationsAsync(CancellationToken cancellationToken)
    {
        Conversations = await dbContext.Conversations
            .AsNoTracking()
            .OrderByDescending(conversation => conversation.UpdatedUtc)
            .ThenByDescending(conversation => conversation.CreatedUtc)
            .Take(50)
            .Select(conversation => new ConversationListItem(
                conversation.Id,
                conversation.Status,
                conversation.CreatedUtc,
                conversation.UpdatedUtc,
                dbContext.WorkProposals
                    .Where(proposal => proposal.ConversationId == conversation.Id)
                    .Select(proposal => proposal.Title)
                    .FirstOrDefault()))
            .ToListAsync(cancellationToken);
    }
}

public sealed record ConversationListItem(
    Guid Id,
    ConversationStatus Status,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    string? ProposedTitle);

public sealed class WorkRequestInput
{
    [Required(ErrorMessage = "Describe the work you want done.")]
    [StringLength(
        DeterministicProposalGenerator.MaxRequestLength,
        ErrorMessage = "Keep the request to {1} characters or fewer.")]
    [Display(Name = "What do you need done?")]
    public string Request { get; set; } = string.Empty;
}
