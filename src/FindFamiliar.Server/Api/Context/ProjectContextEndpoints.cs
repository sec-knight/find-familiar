using FindFamiliar.Server.Api.Runner;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;

namespace FindFamiliar.Server.Api.Context;

/// <summary>
/// The route a trusted local process uses to report a fact worth keeping.
///
/// <b>Why it exists.</b> An agent working on this project can observe something durable — a slice
/// shipped, a defect's real cause, a validation that succeeded — and until now had no supported way to
/// record it unless it was a browser or a claimed session. What it did instead was open the SQLite
/// file, which produced rows belonging to no project. This is the alternative that makes the rule
/// enforceable: the agent reports, Find Familiar validates and records.
///
/// <b>Behind <see cref="RunnerBridgeAuthenticationFilter"/>, deliberately.</b> This is the same kind of
/// caller the Runner and the snapshot hook already are — a process on this machine or this tailnet,
/// holding a credential that has never been given to a vendor — so it reuses that boundary rather than
/// inventing a permission model for one route. Concretely that means it is <b>not</b> the Summoning
/// Gate: it is not published through Funnel, it is not reachable by ChatGPT, no OAuth scope grants it,
/// and it is emphatically not <c>familiar.decide</c>.
///
/// <b>It holds no policy.</b> Every rule lives in <see cref="IProjectContextRecordingService"/>; this
/// maps a request onto it and a typed outcome onto a status code. A reviewer should find nothing here
/// worth arguing about.
///
/// <b>What it cannot do.</b> Record one context entry against one project. It cannot create a task,
/// start a session, approve anything, edit or delete an existing entry, or touch any other table. It
/// is not generic write access and there is no route here that could become one without being written.
/// </summary>
public static class ProjectContextEndpoints
{
    public static void MapProjectContextEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/context").AddEndpointFilter<RunnerBridgeAuthenticationFilter>();

        group.MapPost("/projects/{projectId:guid}/entries", async (
            Guid projectId,
            RecordProjectContextBody body,
            IProjectContextRecordingService recording,
            CancellationToken cancellationToken) =>
        {
            // The kind and provenance arrive as names rather than numbers, so a caller writes what it
            // means and a renumbered enum cannot silently change what a stored record says.
            if (!Enum.TryParse<ContextEntryKind>(body.Kind, ignoreCase: true, out var kind))
            {
                return Problem(StatusCodes.Status400BadRequest, $"'{body.Kind}' is not a context category.");
            }

            if (!Enum.TryParse<ContextProvenance>(body.Provenance, ignoreCase: true, out var provenance))
            {
                return Problem(StatusCodes.Status400BadRequest, $"'{body.Provenance}' is not a provenance class.");
            }

            var outcome = await recording.RecordAsync(
                new RecordProjectContextRequest(
                    projectId,
                    kind,
                    body.Title ?? string.Empty,
                    body.Content ?? string.Empty,
                    provenance,
                    body.RecordedBy,
                    body.IsSensitive ?? false,
                    body.ExpectedContextRevision),
                cancellationToken);

            return outcome.Status switch
            {
                RecordProjectContextStatus.Recorded => Results.Ok(new RecordProjectContextResponse(
                    outcome.ContextEntryId!.Value, outcome.ContextRevision!.Value)),

                RecordProjectContextStatus.ProjectNotFound =>
                    Problem(StatusCodes.Status404NotFound, "No project has that id."),

                RecordProjectContextStatus.ProjectInactive =>
                    Problem(StatusCodes.Status409Conflict, "That project is not active."),

                // The caller's view was stale. A retry against the current revision is the fix, which is
                // why this is distinct from a validation failure.
                RecordProjectContextStatus.ContextMoved =>
                    Problem(StatusCodes.Status409Conflict, "The project's context moved after you read it."),

                RecordProjectContextStatus.ValidationFailed =>
                    Problem(StatusCodes.Status400BadRequest, outcome.ValidationMessage ?? "The request was not valid."),

                // 503 with a retry hint: nothing was written and no competitor exists.
                RecordProjectContextStatus.DatabaseBusy =>
                    Problem(StatusCodes.Status503ServiceUnavailable, "The database was busy. Nothing was written; retry."),

                _ => Problem(StatusCodes.Status500InternalServerError, "The context could not be recorded.")
            };
        });
    }

    private static IResult Problem(int statusCode, string detail) =>
        Results.Json(new RecordProjectContextError(detail), statusCode: statusCode);
}

/// <param name="ExpectedContextRevision">
/// Optional fence. Supply the revision you read if this entry only makes sense against that view;
/// omit it when reporting an independent observation.
/// </param>
public sealed record RecordProjectContextBody(
    string? Kind,
    string? Title,
    string? Content,
    string? Provenance,
    string? RecordedBy = null,
    bool? IsSensitive = null,
    int? ExpectedContextRevision = null);

public sealed record RecordProjectContextResponse(Guid ContextEntryId, int ContextRevision);

public sealed record RecordProjectContextError(string Error);
