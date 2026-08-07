using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// Captures the current repository state into the one context entry that holds it.
/// </summary>
public interface IRepositorySnapshotService
{
    Task<RepositorySnapshotOutcome> CaptureAsync(CancellationToken cancellationToken = default);
}

public enum RepositorySnapshotStatus
{
    /// <summary>Written. Exactly one snapshot row exists, and it is this one.</summary>
    Captured,

    /// <summary>No repository is configured. Nothing ran and nothing was written.</summary>
    NotConfigured,

    /// <summary>Configured, but no project could be resolved to hold the entry.</summary>
    NoProject,

    /// <summary>git could not be read. The previous snapshot, if any, is untouched.</summary>
    Unreadable
}

/// <param name="Characters">
/// The size of what was written, so an operator can see the ceiling binding without reading the row.
/// </param>
public sealed record RepositorySnapshotOutcome(RepositorySnapshotStatus Status, int Characters = 0)
{
    public static RepositorySnapshotOutcome NotConfigured { get; } = new(RepositorySnapshotStatus.NotConfigured);
    public static RepositorySnapshotOutcome NoProject { get; } = new(RepositorySnapshotStatus.NoProject);
    public static RepositorySnapshotOutcome Unreadable { get; } = new(RepositorySnapshotStatus.Unreadable);
}

/// <summary>
/// The repository, written down where the Familiar can already read it.
///
/// <b>Nothing here depends on a person remembering to log anything.</b> That is the whole reason it
/// exists: the repository's shape was previously known to the Familiar only when somebody happened to
/// paste it into a conversation, which meant it was usually months stale and nobody could tell.
///
/// <b>Supersession is delete-on-write.</b> The prior snapshot row is deleted in the same transaction
/// that inserts the new one, so exactly one exists at any moment and no consumer needs to know a
/// filtering rule for its results to be correct. The rejected alternative — keep every snapshot and
/// filter to the newest at retrieval time — puts a correctness requirement in every reader, including
/// readers not yet written, and a reader that forgets it gets a confident answer about a repository as
/// it stood in March.
///
/// <b>It is an ordinary context entry.</b> Kind <see cref="ContextEntryKind.Summary"/>, fixed title,
/// no new table and no migration, so it is retrievable through exactly the path everything else is
/// retrievable through. ADR-0015 records when that stops being the right answer.
///
/// <b>The row keeps its id across captures.</b> A context entry id is a citable thing — a reply that
/// cites the snapshot is checked against ids that still resolve — and re-inserting under a new id
/// every half hour meant every such citation decayed into "unsupported reference" within one capture
/// interval. It is also kept out of the brief's newest-record date: an automated capture is not a
/// record of anybody's work, and letting it set that date would pin "the records end here" to today
/// forever, which is the one number the Familiar's answers about staleness rest on.
///
/// <b>It does not increment the project's context revision.</b> A revision bump is a statement that
/// the evidence a human is looking at has changed underneath them, and it invalidates every pending
/// proposal and plan that observed the old one. An automated write every half hour would make plan
/// approval fail permanently, which is a far worse outcome than a plan drafted against a snapshot
/// half an hour old.
/// </summary>
public sealed class RepositorySnapshotService(
    FamiliarDbContext dbContext,
    IRepositoryStateReader reader,
    IOptions<RepositorySnapshotOptions> options,
    TimeProvider timeProvider,
    ILogger<RepositorySnapshotService> logger) : IRepositorySnapshotService
{
    /// <summary>
    /// The title, fixed and never dated.
    ///
    /// "(current)" rather than a timestamp because the title is the identity of the row: it is how
    /// delete-on-write finds the prior snapshot, and how a person searching the store finds the one
    /// that is true rather than a list of eleven that were. The date lives in the header, where it
    /// describes the content instead of naming it.
    /// </summary>
    public const string SnapshotTitle = "Repository state snapshot (current)";

    private readonly RepositorySnapshotOptions _options = options.Value;

    public async Task<RepositorySnapshotOutcome> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured())
        {
            return RepositorySnapshotOutcome.NotConfigured;
        }

        if (await ResolveProjectIdAsync(cancellationToken) is not { } projectId)
        {
            logger.LogWarning(
                "A repository snapshot was not written: no project is configured and there is not exactly one to infer.");
            return RepositorySnapshotOutcome.NoProject;
        }

        if (await reader.ReadAsync(cancellationToken) is not { } state)
        {
            // Deliberately before any write. A snapshot that cannot be read must leave the previous
            // one standing rather than replace it with an apology — a stale snapshot that says which
            // commit it describes is still true about that commit.
            return RepositorySnapshotOutcome.Unreadable;
        }

        var content = RepositorySnapshotComposer.Compose(state, timeProvider.GetUtcNow().UtcDateTime);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Every snapshot this project has, oldest first. If a previous process was interrupted part
        // way, more than one could exist; the invariant is restored rather than assumed.
        var existing = await dbContext.ContextEntries
            .Where(entry =>
                entry.ProjectId == projectId
                && entry.Kind == ContextEntryKind.Summary
                && entry.Title == SnapshotTitle)
            .OrderBy(entry => entry.CreatedUtc)
            .ToListAsync(cancellationToken);

        if (existing.Count > 1)
        {
            dbContext.ContextEntries.RemoveRange(existing.Skip(1));
        }

        if (existing.FirstOrDefault() is { } snapshot)
        {
            // Updated in place, keeping its id and its CreatedUtc. Supersession was always about there
            // being exactly one snapshot; it never needed the one to be a different row. A fresh id
            // every half hour meant a reply that cited the snapshot decayed into the words "unsupported
            // reference" within one capture interval, because the row it pointed at no longer existed.
            // Its CreatedUtc stays at first capture for the same reason it is excluded from the brief's
            // newest-record date: an automated write is not a record of anybody's work, and the content
            // carries its own capture date in the header.
            snapshot.Content = content;
            snapshot.State = ContextEntryState.Active;
        }
        else
        {
            dbContext.ContextEntries.Add(new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                Kind = ContextEntryKind.Summary,
                Title = SnapshotTitle,
                Content = content,
                State = ContextEntryState.Active,
                CreatedUtc = timeProvider.GetUtcNow().UtcDateTime
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Repository state snapshot written for project {ProjectId} at {Head} ({Characters} characters).",
            projectId,
            state.HeadSha,
            content.Length);

        return new RepositorySnapshotOutcome(RepositorySnapshotStatus.Captured, content.Length);
    }

    /// <summary>
    /// The configured project, or the only one there is. Same rule as plan drafting: with one
    /// non-sensitive active project "which project?" has one answer, and with several it is a
    /// decision rather than a guess.
    /// </summary>
    private async Task<Guid?> ResolveProjectIdAsync(CancellationToken cancellationToken)
    {
        if (_options.ProjectId is { } configured)
        {
            var exists = await dbContext.Projects
                .AsNoTracking()
                .AnyAsync(project => project.Id == configured, cancellationToken);

            return exists ? configured : null;
        }

        var candidates = await dbContext.Projects
            .AsNoTracking()
            .Where(project => !project.IsSensitive && project.Status == ProjectStatus.Active)
            .Select(project => project.Id)
            .Take(2)
            .ToListAsync(cancellationToken);

        return candidates.Count == 1 ? candidates[0] : null;
    }
}
