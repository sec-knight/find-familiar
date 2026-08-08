using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public enum ProjectLifecycleStatus
{
    Succeeded,

    /// <summary>The project or task named does not exist.</summary>
    NotFound,

    /// <summary>The project is not active, so its work is closed to changes.</summary>
    ProjectInactive,

    /// <summary>A project with that name already exists. Names are how a person finds a project again.</summary>
    NameTaken,

    /// <summary>A field was missing, too long, or not a value this system issues.</summary>
    ValidationFailed,

    /// <summary>
    /// The caller stated the revision it had read and the project moved since. Only returned when a
    /// caller opted into the check by supplying one.
    /// </summary>
    ContextMoved,

    /// <summary>SQLite was busy. Nothing was written and nobody else decided anything — retry.</summary>
    DatabaseBusy
}

/// <param name="RetiredDecisions">
/// How many decisions the change settled. Closing a task retires the step that was waiting on it —
/// without that the handoff stays Pending forever, unanswerable and still asked about.
/// </param>
public sealed record ProjectLifecycleOutcome(
    ProjectLifecycleStatus Status,
    Guid? ProjectId = null,
    Guid? TaskId = null,
    Guid? ContextEntryId = null,
    int RetiredDecisions = 0,
    string? ValidationMessage = null)
{
    public static ProjectLifecycleOutcome Of(ProjectLifecycleStatus status, string? message = null) =>
        new(status, ValidationMessage: message);
}

public sealed record CreateProjectRequest(string Name, string Purpose);

public sealed record CreateTaskRequest(Guid ProjectId, string Title, string RequestedOutcome);

/// <param name="ExpectedContextRevision">
/// Optional fence. Supply the revision you read when the change only makes sense against that view;
/// omit it when the change is unconditional.
/// </param>
public sealed record UpdateTaskStatusRequest(Guid TaskId, TaskStatus Status, int? ExpectedContextRevision = null);

public sealed record RecordTaskContextRequest(
    Guid TaskId,
    ContextEntryKind Kind,
    string Title,
    string Content,
    ContextProvenance Provenance,
    string? RecordedBy = null);

public interface IProjectLifecycleService
{
    Task<ProjectLifecycleOutcome> CreateProjectAsync(CreateProjectRequest request, CancellationToken cancellationToken = default);

    Task<ProjectLifecycleOutcome> CreateTaskAsync(CreateTaskRequest request, CancellationToken cancellationToken = default);

    Task<ProjectLifecycleOutcome> UpdateTaskStatusAsync(UpdateTaskStatusRequest request, CancellationToken cancellationToken = default);

    Task<ProjectLifecycleOutcome> RecordTaskContextAsync(RecordTaskContextRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The ordinary project lifecycle: making a project, making a task, moving a task's status, and
/// writing something down against a task.
///
/// <b>Why it exists.</b> Every one of these lived inline in a Razor handler, which was fine while a
/// browser was the only thing that did them. It stops being fine the moment a second frontend needs
/// the same operations: the alternative to this service is the same rules written twice, and two
/// implementations of "what closing a task means" will not stay identical. ADR-0019 makes the
/// Demiplane and the Familiar peers over one authoritative system; peers cannot each own a copy of the
/// rules.
///
/// <b>It creates work; it does not run any.</b> Nothing here starts a session or answers a decision. A
/// task this service creates sits Ready until somebody decides to run it, and that decision has its own
/// gate and its own permission. The one adjacent thing it does do is retire a decision that a status
/// change has made unanswerable, which is not crossing a gate but removing one that no longer applies.
///
/// <b>What it does not do.</b> No deletes. No worker administration. No capturing a session's result on
/// a worker's behalf. Those are either destructive or somebody else's authority, and none of them is
/// ordinary project work.
/// </summary>
public sealed class ProjectLifecycleService(
    FamiliarDbContext dbContext,
    IWorkflowDispatchService workflowDispatch,
    ISessionHandoffService handoffs,
    TimeProvider clock) : IProjectLifecycleService
{
    public async Task<ProjectLifecycleOutcome> CreateProjectAsync(
        CreateProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = (request.Name ?? string.Empty).Trim();
        var purpose = (request.Purpose ?? string.Empty).Trim();

        if (name.Length is 0 or > 200)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "A project name of at most 200 characters is required.");
        }

        if (purpose.Length is 0 or > 4_000)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "A purpose of at most 4000 characters is required.");
        }

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // The same uniqueness the project page enforces. A name is how a person finds a project
            // again, and two projects called the same thing is a filing problem they cannot fix later.
            if (await dbContext.Projects.AnyAsync(project => project.Name == name, cancellationToken))
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.NameTaken, "A project with this name already exists.");
            }

            var nowUtc = clock.GetUtcNow().UtcDateTime;

            var project = new FamiliarProject
            {
                Id = Guid.NewGuid(),
                Name = name,
                Purpose = purpose,
                Status = ProjectStatus.Active,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc
            };

            dbContext.Projects.Add(project);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ProjectLifecycleOutcome(ProjectLifecycleStatus.Succeeded, ProjectId: project.Id);
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.DatabaseBusy);
        }
    }

    public async Task<ProjectLifecycleOutcome> CreateTaskAsync(
        CreateTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        var title = (request.Title ?? string.Empty).Trim();
        var outcome = (request.RequestedOutcome ?? string.Empty).Trim();

        if (title.Length is 0 or > 200)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "A task title of at most 200 characters is required.");
        }

        if (outcome.Length is 0 or > 4_000)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "A requested outcome of at most 4000 characters is required.");
        }

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var project = await dbContext.Projects
                .SingleOrDefaultAsync(candidate => candidate.Id == request.ProjectId, cancellationToken);

            if (project is null)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.NotFound);
            }

            if (project.Status != ProjectStatus.Active)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ProjectInactive);
            }

            // Through the dispatch boundary the manual pages use, so a task created from a
            // conversation is indistinguishable from one typed in — including the revision bump it
            // causes, which is the dispatch service's business rather than this one's.
            var task = workflowDispatch.CreateReadyTask(project, title, outcome, clock.GetUtcNow().UtcDateTime);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ProjectLifecycleOutcome(
                ProjectLifecycleStatus.Succeeded, ProjectId: project.Id, TaskId: task.Id);
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.DatabaseBusy);
        }
    }

    /// <summary>
    /// Moves a task's status, and retires any decision the move makes unanswerable.
    ///
    /// The retirement is the part worth keeping in one place. Completing a task while a step is still
    /// waiting on the human leaves a handoff that can never be approved — the approval service refuses
    /// a closed task — and that nothing retires, so it sits in every "waiting for you" list being asked
    /// about and never answerable. Superseded is exactly the right state: something newer replaced the
    /// decision point, and closing the task is that something. It commits with the status change,
    /// because a task that closed while its handoff stayed pending is the state this prevents.
    /// </summary>
    public async Task<ProjectLifecycleOutcome> UpdateTaskStatusAsync(
        UpdateTaskStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Status))
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "That is not a task status this system uses.");
        }

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var task = await dbContext.Tasks
                .Include(candidate => candidate.Project)
                .SingleOrDefaultAsync(candidate => candidate.Id == request.TaskId, cancellationToken);

            if (task is null)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.NotFound);
            }

            if (task.Project.Status != ProjectStatus.Active)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ProjectInactive);
            }

            if (request.ExpectedContextRevision is { } expected && expected != task.Project.ContextRevision)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ContextMoved);
            }

            var nowUtc = clock.GetUtcNow().UtcDateTime;

            task.Status = request.Status;
            task.UpdatedUtc = nowUtc;
            task.Project.IncrementContextRevision();

            var retired = request.Status == TaskStatus.Completed
                ? await handoffs.SupersedePendingAsync(task.Id, nowUtc, cancellationToken)
                : 0;

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ProjectLifecycleOutcome(
                ProjectLifecycleStatus.Succeeded,
                ProjectId: task.ProjectId,
                TaskId: task.Id,
                RetiredDecisions: retired);
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.DatabaseBusy);
        }
    }

    /// <summary>
    /// Records something against a task — the task-scoped twin of
    /// <see cref="IProjectContextRecordingService"/>, with the same invariants and the same reasons.
    /// </summary>
    public async Task<ProjectLifecycleOutcome> RecordTaskContextAsync(
        RecordTaskContextRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Kind))
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "That is not a context category this system records.");
        }

        if (!Enum.IsDefined(request.Provenance) || request.Provenance == ContextProvenance.Unspecified)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "A provenance class is required, and must not be Unspecified.");
        }

        var title = (request.Title ?? string.Empty).Trim();
        var content = (request.Content ?? string.Empty).Trim();

        if (title.Length is 0 or > 200)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "A title of at most 200 characters is required.");
        }

        if (content.Length is 0 or > 12_000)
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ValidationFailed, "Content of at most 12000 characters is required.");
        }

        try
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            var task = await dbContext.Tasks
                .Include(candidate => candidate.Project)
                .SingleOrDefaultAsync(candidate => candidate.Id == request.TaskId, cancellationToken);

            if (task is null)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.NotFound);
            }

            if (task.Project.Status != ProjectStatus.Active)
            {
                return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.ProjectInactive);
            }

            var entry = new ContextEntry
            {
                Id = Guid.NewGuid(),

                // From the loaded rows, so both foreign keys are values this transaction has proved.
                ProjectId = task.ProjectId,
                TaskId = task.Id,
                Kind = request.Kind,
                Title = title,
                Content = content,
                State = ContextEntryState.Active,
                Provenance = request.Provenance,
                RecordedBy = string.IsNullOrWhiteSpace(request.RecordedBy) ? null : request.RecordedBy.Trim(),
                CreatedUtc = clock.GetUtcNow().UtcDateTime
            };

            dbContext.ContextEntries.Add(entry);
            task.Project.IncrementContextRevision();

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ProjectLifecycleOutcome(
                ProjectLifecycleStatus.Succeeded,
                ProjectId: task.ProjectId,
                TaskId: task.Id,
                ContextEntryId: entry.Id);
        }
        catch (Exception exception) when (IsBusy(exception))
        {
            return ProjectLifecycleOutcome.Of(ProjectLifecycleStatus.DatabaseBusy);
        }
    }

    private static bool IsBusy(Exception exception) =>
        exception is SqliteException { SqliteErrorCode: 5 or 6 }
        || exception is DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 } };
}
