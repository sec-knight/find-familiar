namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// Reads one project's conversation, and appends to it.
///
/// <see cref="GetAsync"/> performs no writes, so a <c>GET</c> of the Familiar page cannot create a
/// conversation row for a project nobody has spoken to yet. A project with no conversation returns
/// null, which is the truth rather than an empty aggregate conjured to make rendering tidier.
/// </summary>
public interface IFamiliarConversationService
{
    /// <summary>The conversation for this project, or null when none has been started.</summary>
    Task<FamiliarConversationView?> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a human message and whatever the reasoning provider had to say about it.
    ///
    /// The human message commits before any provider I/O, in its own transaction. That is not an
    /// optimisation and the two transactions must not be merged: a provider that hangs, faults, or is
    /// killed by a deploy must not take the user's words with it, and a single transaction spanning a
    /// network call to a third party would hold a SQLite write lock for its duration — on a database
    /// the runner, the capture path and the claim scan are all writing to.
    /// </summary>
    Task<FamiliarSendResult> SendAsync(
        Guid projectId,
        string message,
        CancellationToken cancellationToken = default);
}
