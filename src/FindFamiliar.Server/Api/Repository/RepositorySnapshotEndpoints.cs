using FindFamiliar.Server.Api.Runner;
using FindFamiliar.Server.Services.Familiar.Repository;

namespace FindFamiliar.Server.Api.Repository;

/// <summary>
/// The post-commit trigger: one authenticated route a git hook can poke.
///
/// Behind <see cref="RunnerBridgeAuthenticationFilter"/> — the same machine-to-machine token the
/// Runner already uses, because this is the same kind of caller and a second credential to distribute
/// would be a second credential to leak. It asks for a capture and returns immediately: a git hook
/// runs inside <c>git commit</c>, and a hook that waits on a database write is a hook that makes
/// committing feel slow and gets deleted.
/// </summary>
public static class RepositorySnapshotEndpoints
{
    public static void MapRepositorySnapshotEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/repository").AddEndpointFilter<RunnerBridgeAuthenticationFilter>();

        group.MapPost("/snapshot", (RepositorySnapshotQueue queue) =>
        {
            queue.Request();

            // Accepted, not OK: nothing has been written yet, and saying otherwise would let a caller
            // read the entry and find the old one.
            return Results.Accepted();
        });
    }
}
