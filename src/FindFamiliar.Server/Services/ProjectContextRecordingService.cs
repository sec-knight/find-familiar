using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public enum RecordProjectContextStatus
{
    /// <summary>The entry was written and the project's context revision moved, together.</summary>
    Recorded,

    /// <summary>No project has that id. Nothing was written.</summary>
    ProjectNotFound,

    /// <summary>The project is not active, so its record is closed to new context.</summary>
    ProjectInactive,

    /// <summary>
    /// The caller stated which revision it had read and the project has moved since. Only returned when
    /// a caller opted into the check by supplying one.
    /// </summary>
    ContextMoved,

    /// <summary>A field was missing, too long, or not a value this system issues.</summary>
    ValidationFailed,

    /// <summary>SQLite was busy. Nothing was written and nobody else decided anything — retry.</summary>
    DatabaseBusy
}

/// <param name="ExpectedContextRevision">
/// Optional. When supplied, the write is refused if the project's context moved after the caller read
/// it — the same fence the conversational action path uses. Callers reporting an independent fact
/// (a session result, a repository observation) have no reason to care and pass null.
/// </param>
public sealed record RecordProjectContextRequest(
    Guid ProjectId,
    ContextEntryKind Kind,
    string Title,
    string Content,
    ContextProvenance Provenance,
    string? RecordedBy = null,
    bool IsSensitive = false,
    int? ExpectedContextRevision = null);

public sealed record RecordProjectContextOutcome(
    RecordProjectContextStatus Status,
    Guid? ContextEntryId = null,
    int? ContextRevision = null,
    string? ValidationMessage = null)
{
    public static RecordProjectContextOutcome Of(RecordProjectContextStatus status, string? message = null) =>
        new(status, ValidationMessage: message);
}

public interface IProjectContextRecordingService
{
    Task<RecordProjectContextOutcome> RecordAsync(
        RecordProjectContextRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The supported way to record durable project context. One implementation of the invariants, for
/// every caller that has a fact to keep.
///
/// <b>Why this exists.</b> Recording context used to mean one of two things: a Razor Page handler with
/// the rules written inline, or — for anything that was not a browser — opening the SQLite file and
/// writing rows. The second is how three orphaned entries were created during Sprint 14/15 work: a
/// project id supplied in the wrong case matched nothing, foreign keys were not enforcing, the rows
/// inserted anyway belonging to no project, and the context revision never moved. Every one of those
/// failures is an invariant this class now owns and a caller can no longer get wrong.
///
/// <b>The rule it makes enforceable.</b> An agent reports facts; Find Familiar validates and records
/// them. Nothing outside this application writes this database. That is not a convention here — a
/// caller of this service cannot express a raw statement, cannot choose a table, and cannot create an
/// entry without the revision moving with it.
///
/// <b>What it is not.</b> Not generic write access. It creates exactly one row of exactly one kind,
/// linked to one project. It cannot create or modify a task, start a session, decide anything, or edit
/// an existing entry — those have their own gates, and none of them is here.
/// </summary>
public sealed class ProjectContextRecordingService(FamiliarDbContext dbContext, TimeProvider clock)
    : IProjectContextRecordingService
{
    public async Task<RecordProjectContextOutcome> RecordAsync(
        RecordProjectContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (Validate(request) is { } validationMessage)
        {
            return RecordProjectContextOutcome.Of(RecordProjectContextStatus.ValidationFailed, validationMessage);
        }

        try
        {
            // One transaction over the lookup, the insert and the revision bump. The failure this
            // prevents is the one that actually happened: an entry that exists while the revision that
            // announces it does not.
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Looked up through EF against the typed Guid column, so the textual representation the
            // caller used — casing, braces, hyphenation — cannot decide whether a project is found.
            // Raw SQL comparing strings is exactly what produced rows belonging to no project.
            var project = await dbContext.Projects
                .SingleOrDefaultAsync(candidate => candidate.Id == request.ProjectId, cancellationToken);

            if (project is null)
            {
                return RecordProjectContextOutcome.Of(RecordProjectContextStatus.ProjectNotFound);
            }

            if (project.Status != ProjectStatus.Active)
            {
                return RecordProjectContextOutcome.Of(RecordProjectContextStatus.ProjectInactive);
            }

            if (request.ExpectedContextRevision is { } expected && expected != project.ContextRevision)
            {
                return RecordProjectContextOutcome.Of(RecordProjectContextStatus.ContextMoved);
            }

            var entry = new ContextEntry
            {
                Id = Guid.NewGuid(),

                // Taken from the loaded row rather than from the request, so the foreign key is a value
                // this transaction has already proved exists.
                ProjectId = project.Id,
                Kind = request.Kind,
                Title = request.Title.Trim(),
                Content = request.Content.Trim(),
                State = ContextEntryState.Active,
                Provenance = request.Provenance,
                RecordedBy = string.IsNullOrWhiteSpace(request.RecordedBy) ? null : request.RecordedBy.Trim(),
                IsSensitive = request.IsSensitive,
                CreatedUtc = clock.GetUtcNow().UtcDateTime
            };

            dbContext.ContextEntries.Add(entry);
            project.IncrementContextRevision();

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new RecordProjectContextOutcome(
                RecordProjectContextStatus.Recorded, entry.Id, project.ContextRevision);
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            // Distinct from a failure, and never reported as one: nothing was written, and retrying is
            // the correct response rather than investigating.
            return RecordProjectContextOutcome.Of(RecordProjectContextStatus.DatabaseBusy);
        }
    }

    /// <summary>
    /// Everything checkable before the database is touched. Bounds match the column definitions, so a
    /// caller is refused here rather than by a truncation or a constraint violation later.
    /// </summary>
    private static string? Validate(RecordProjectContextRequest request)
    {
        if (request.ProjectId == Guid.Empty)
        {
            return "A project id is required.";
        }

        if (!Enum.IsDefined(request.Kind))
        {
            return "That is not a context category this system records.";
        }

        // The recording path requires a caller to say how well a fact is known. Defaulting would mean
        // guessing on the reader's behalf, and Unspecified exists for historical rows, not new ones.
        if (!Enum.IsDefined(request.Provenance) || request.Provenance == ContextProvenance.Unspecified)
        {
            return "A provenance class is required, and must not be Unspecified.";
        }

        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
        {
            return "A title is required, of at most 200 characters.";
        }

        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Trim().Length > 12_000)
        {
            return "Content is required, of at most 12000 characters.";
        }

        if (request.RecordedBy is { } recordedBy && recordedBy.Trim().Length > ContextEntry.MaxRecordedByLength)
        {
            return $"RecordedBy must be at most {ContextEntry.MaxRecordedByLength} characters.";
        }

        return null;
    }

    private static bool IsBusy(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 }
        || (exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 } });
}
