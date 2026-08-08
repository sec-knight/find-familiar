namespace FindFamiliar.Server.Services;

public interface IContextProjectionService
{
    Task<TaskContextDocument?> GetTaskContextAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A project's own context — the entries recorded against the project rather than against any one
    /// of its tasks, in the order they were written.
    ///
    /// This is the definition of "a project's context", and it lives here so there is one of it. The
    /// project page and the Familiar gateway both enumerate this list, and two queries that agreed
    /// today would be two queries that could disagree tomorrow about what a project's own context is.
    ///
    /// It applies no sensitivity rule, exactly as <see cref="GetTaskContextAsync"/> does not: this
    /// serves a reader on the owner's own machine and a reader holding a vendor's credential, and only
    /// the caller knows which it is. <see cref="ContextEntryDocument.IsSensitive"/> is carried so the
    /// caller that must filter can.
    /// </summary>
    Task<IReadOnlyList<ContextEntryDocument>> GetProjectEntriesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default);
}
