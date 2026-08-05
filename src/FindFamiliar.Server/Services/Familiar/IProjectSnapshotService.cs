namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// Builds the bounded, project-isolated view of one project that the Familiar reasons over.
///
/// Implementations perform no writes and call no reasoning provider, so this is safe to call from a
/// <c>GET</c>: looking at a project must not change it.
/// </summary>
public interface IProjectSnapshotService
{
    Task<ProjectSnapshotResult> GetSnapshotAsync(Guid projectId, CancellationToken cancellationToken = default);
}
