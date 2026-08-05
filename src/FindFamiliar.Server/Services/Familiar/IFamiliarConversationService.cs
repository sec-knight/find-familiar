namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// Reads one project's conversation.
///
/// <see cref="GetAsync"/> performs no writes, so a <c>GET</c> of the Familiar page cannot create a
/// conversation row for a project nobody has spoken to yet. A project with no conversation returns
/// null, which is the truth rather than an empty aggregate conjured to make rendering tidier.
/// </summary>
public interface IFamiliarConversationService
{
    /// <summary>The conversation for this project, or null when none has been started.</summary>
    Task<FamiliarConversationView?> GetAsync(Guid projectId, CancellationToken cancellationToken = default);
}
