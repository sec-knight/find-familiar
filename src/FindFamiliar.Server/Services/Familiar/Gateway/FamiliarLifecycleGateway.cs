using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services.Familiar.Gateway;

/// <summary>How an ordinary lifecycle action ended, in terms a client can explain to a person.</summary>
public enum FamiliarLifecycleOutcome
{
    Done,

    /// <summary>Nothing readable has that id. Nothing was changed.</summary>
    NotFound,

    /// <summary>A name already in use. Nothing was changed.</summary>
    NameTaken,

    /// <summary>The request was not usable — a missing field, an over-long one, or an unknown value.</summary>
    Rejected,

    /// <summary>The workflow refused: the action is not currently legal. Nothing was changed.</summary>
    NotCurrentlyLegal,

    /// <summary>The view this was decided against has moved. Nothing was changed.</summary>
    Stale,

    /// <summary>The database was busy. Nothing was changed — retry.</summary>
    Busy
}

/// <param name="Detail">One sentence a client may read to the human. Authored here, never provider text.</param>
public sealed record FamiliarLifecycleResult(
    FamiliarLifecycleOutcome Outcome,
    string Detail,
    Guid? ProjectId = null,
    Guid? TaskId = null,
    Guid? SessionId = null,
    Guid? ContextEntryId = null);

public interface IFamiliarLifecycleGateway
{
    Task<FamiliarLifecycleResult> CreateProjectAsync(string name, string purpose, CancellationToken cancellationToken = default);

    Task<FamiliarLifecycleResult> CreateTaskAsync(Guid projectId, string title, string requestedOutcome, CancellationToken cancellationToken = default);

    Task<FamiliarLifecycleResult> UpdateTaskStatusAsync(Guid taskId, string status, CancellationToken cancellationToken = default);

    Task<FamiliarLifecycleResult> RecordTaskContextAsync(
        Guid taskId, string category, string title, string content, CancellationToken cancellationToken = default);

    Task<FamiliarLifecycleResult> RecordProjectContextAsync(
        Guid projectId, string category, string title, string content, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ordinary project work, done on the human's explicit instruction.
///
/// <b>It holds no rules.</b> Every operation resolves what the caller may see, then calls the same
/// application service the Demiplane's own handlers call. Legality, uniqueness, revision bumps and the
/// retirement of decisions a status change invalidates all live in
/// <see cref="IProjectLifecycleService"/>, once, because a second implementation of "what closing a
/// task means" would not stay identical to the first.
///
/// <b>Visibility first, as on the decision path.</b> A project or task in a project the caller may not
/// read answers exactly as one that does not exist. The lifecycle service knows nothing about
/// sensitivity — its other caller is the owner's own browser — so the check belongs here, on the side
/// of the boundary that knows who is asking.
///
/// <b>It crosses no human gate.</b> Nothing here approves a step, answers a decision, or starts work.
/// Creating a task leaves it Ready; deciding to run it is a different permission and a different
/// service. The one adjacent effect — completing a task retires the step that was waiting on it — is
/// removing a decision that has become unanswerable, not taking it.
/// </summary>
public sealed class FamiliarLifecycleGateway(
    IFamiliarGateway gateway,
    IProjectLifecycleService lifecycle,
    IProjectContextRecordingService projectContext) : IFamiliarLifecycleGateway
{
    /// <summary>
    /// What the Familiar records is reported, not asserted. A person told their Familiar to write
    /// something down; that is <see cref="ContextProvenance.HumanReported"/>, and the client does not
    /// get to choose a stronger class for its own text.
    /// </summary>
    private const ContextProvenance RelayedProvenance = ContextProvenance.HumanReported;

    private const string RecordedBy = "familiar-gateway";

    public async Task<FamiliarLifecycleResult> CreateProjectAsync(
        string name,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        // No visibility check: a project that does not exist yet cannot be one the caller may not see.
        var outcome = await lifecycle.CreateProjectAsync(new CreateProjectRequest(name, purpose), cancellationToken);

        return outcome.Status switch
        {
            ProjectLifecycleStatus.Succeeded => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Done,
                $"Created the project \"{name.Trim()}\". It has no tasks yet.",
                ProjectId: outcome.ProjectId),

            ProjectLifecycleStatus.NameTaken => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.NameTaken,
                "A project with that name already exists, so nothing was created. Pick a different name, "
                + "or work in the existing one."),

            _ => Translate(outcome)
        };
    }

    public async Task<FamiliarLifecycleResult> CreateTaskAsync(
        Guid projectId,
        string title,
        string requestedOutcome,
        CancellationToken cancellationToken = default)
    {
        if (await gateway.GetProjectContextAsync(projectId, cancellationToken) is null)
        {
            return Unreadable("project");
        }

        var outcome = await lifecycle.CreateTaskAsync(
            new CreateTaskRequest(projectId, title, requestedOutcome), cancellationToken);

        return outcome.Status == ProjectLifecycleStatus.Succeeded
            ? new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Done,
                $"Created the task \"{title.Trim()}\". It is Ready and nothing is running on it — say so "
                + "if you want a session started.",
                ProjectId: outcome.ProjectId,
                TaskId: outcome.TaskId)
            : Translate(outcome);
    }

    public async Task<FamiliarLifecycleResult> UpdateTaskStatusAsync(
        Guid taskId,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<TaskStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Rejected,
                $"'{status}' is not a task status. Use one of: {string.Join(", ", Enum.GetNames<TaskStatus>())}.");
        }

        if (await gateway.GetTaskDetailAsync(taskId, cancellationToken) is null)
        {
            return Unreadable("task");
        }

        var outcome = await lifecycle.UpdateTaskStatusAsync(new UpdateTaskStatusRequest(taskId, parsed), cancellationToken);

        if (outcome.Status != ProjectLifecycleStatus.Succeeded)
        {
            return Translate(outcome);
        }

        // A retired decision is said out loud. Somebody who was told a step was waiting on them is
        // entitled to know it no longer is, and to know this action is why.
        var detail = outcome.RetiredDecisions > 0
            ? $"Task status is now {parsed}. The step that was waiting on you no longer applies, so it is "
              + "no longer waiting."
            : $"Task status is now {parsed}.";

        return new FamiliarLifecycleResult(
            FamiliarLifecycleOutcome.Done, detail, ProjectId: outcome.ProjectId, TaskId: outcome.TaskId);
    }

    public async Task<FamiliarLifecycleResult> RecordTaskContextAsync(
        Guid taskId,
        string category,
        string title,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCategory(category, out var kind, out var rejection))
        {
            return rejection;
        }

        if (await gateway.GetTaskDetailAsync(taskId, cancellationToken) is null)
        {
            return Unreadable("task");
        }

        var outcome = await lifecycle.RecordTaskContextAsync(
            new RecordTaskContextRequest(taskId, kind, title, content, RelayedProvenance, RecordedBy),
            cancellationToken);

        return outcome.Status == ProjectLifecycleStatus.Succeeded
            ? new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Done,
                $"Recorded {kind} context on that task.",
                ProjectId: outcome.ProjectId,
                TaskId: outcome.TaskId,
                ContextEntryId: outcome.ContextEntryId)
            : Translate(outcome);
    }

    public async Task<FamiliarLifecycleResult> RecordProjectContextAsync(
        Guid projectId,
        string category,
        string title,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseCategory(category, out var kind, out var rejection))
        {
            return rejection;
        }

        if (await gateway.GetProjectContextAsync(projectId, cancellationToken) is null)
        {
            return Unreadable("project");
        }

        // The same service the Demiplane's project page and the machine-local route already use.
        var outcome = await projectContext.RecordAsync(
            new RecordProjectContextRequest(projectId, kind, title, content, RelayedProvenance, RecordedBy),
            cancellationToken);

        return outcome.Status switch
        {
            RecordProjectContextStatus.Recorded => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Done,
                $"Recorded {kind} context on that project.",
                ProjectId: projectId,
                ContextEntryId: outcome.ContextEntryId),

            RecordProjectContextStatus.ProjectNotFound => Unreadable("project"),

            RecordProjectContextStatus.ProjectInactive => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.NotCurrentlyLegal, "That project is not active. Nothing was changed."),

            RecordProjectContextStatus.ContextMoved => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Stale, "The project's context moved. Nothing was changed."),

            RecordProjectContextStatus.DatabaseBusy => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Busy, "The database was busy. Nothing was changed — this can be retried."),

            _ => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Rejected,
                outcome.ValidationMessage ?? "That could not be recorded. Nothing was changed.")
        };
    }

    private static bool TryParseCategory(
        string category,
        out ContextEntryKind kind,
        out FamiliarLifecycleResult rejection)
    {
        if (Enum.TryParse(category, ignoreCase: true, out kind) && Enum.IsDefined(kind))
        {
            rejection = null!;
            return true;
        }

        rejection = new FamiliarLifecycleResult(
            FamiliarLifecycleOutcome.Rejected,
            $"'{category}' is not a context category. Use one of: {string.Join(", ", Enum.GetNames<ContextEntryKind>())}.");

        return false;
    }

    /// <summary>
    /// Unreadable and non-existent answer identically. Telling the two apart would disclose that a
    /// record exists which the user chose to keep out of this connection's reach.
    /// </summary>
    private static FamiliarLifecycleResult Unreadable(string noun) =>
        new(FamiliarLifecycleOutcome.NotFound,
            $"No readable {noun} has that id. Nothing was changed.");

    private static FamiliarLifecycleResult Translate(ProjectLifecycleOutcome outcome) =>
        outcome.Status switch
        {
            ProjectLifecycleStatus.NotFound => Unreadable("record"),

            ProjectLifecycleStatus.ProjectInactive => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.NotCurrentlyLegal, "That project is not active. Nothing was changed."),

            ProjectLifecycleStatus.ContextMoved => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Stale, "That changed after you were shown it. Nothing was changed."),

            ProjectLifecycleStatus.NameTaken => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.NameTaken, "That name is already in use. Nothing was changed."),

            ProjectLifecycleStatus.DatabaseBusy => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Busy, "The database was busy. Nothing was changed — this can be retried."),

            _ => new FamiliarLifecycleResult(
                FamiliarLifecycleOutcome.Rejected,
                outcome.ValidationMessage ?? "That request was not usable. Nothing was changed.")
        };
}
