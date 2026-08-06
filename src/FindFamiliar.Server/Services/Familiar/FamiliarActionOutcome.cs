namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// How a confirmation or dismissal ended.
///
/// Every member here is something the server actually observed. In particular there is no member
/// that means "something went wrong, probably a race" — <see cref="DatabaseBusy"/> and
/// <see cref="Failed"/> are distinct, and neither claims a competitor, because claiming one sends a
/// person looking for an actor who was never there (ADR-0011).
/// </summary>
public enum FamiliarActionStatusOutcome
{
    /// <summary>The proposal was consumed and its effects committed together.</summary>
    Confirmed,

    /// <summary>The proposal was consumed and nothing was created.</summary>
    Dismissed,

    /// <summary>
    /// A real competing decision already confirmed this. The durable links from that first
    /// confirmation are returned, so a replay reports the original work rather than creating more.
    /// </summary>
    AlreadyConfirmed,

    /// <summary>A real competing decision already dismissed this.</summary>
    AlreadyDismissed,

    /// <summary>The token presented was not the current one, so this view was out of date.</summary>
    StaleToken,

    /// <summary>No proposal with that id, or it belongs to another project.</summary>
    NotFound,

    /// <summary>The project is no longer active.</summary>
    ProjectInactive,

    /// <summary>The project's context revision moved after the human reviewed this. CreateTask only.</summary>
    ContextMoved,

    /// <summary>The target task already has a Started session. StartPlanner only.</summary>
    TaskAlreadyRunning,

    /// <summary>The target task no longer exists, or is not this project's.</summary>
    TargetTaskInvalid,

    /// <summary>The edited title or requested outcome is missing or too long.</summary>
    ValidationFailed,

    /// <summary>
    /// SQLite was busy or locked. Nothing was written and nobody else decided anything — this is a
    /// retry, not a lost race, on every path including acquiring the transaction and rolling back.
    /// </summary>
    DatabaseBusy,

    /// <summary>An unexpected fault. Never silently recategorised as any of the above.</summary>
    Failed
}

/// <param name="CreatedTaskId">The task a confirmation created, or the one the original confirmation created on replay.</param>
/// <param name="CreatedSessionId">The session a confirmation started, likewise durable across replays.</param>
/// <param name="ValidationMessage">Authored here, shown beside the edited field. Never provider text.</param>
public sealed record FamiliarActionOutcome(
    FamiliarActionStatusOutcome Status,
    Guid? CreatedTaskId = null,
    Guid? CreatedSessionId = null,
    string? ValidationMessage = null)
{
    public static FamiliarActionOutcome Of(FamiliarActionStatusOutcome status) => new(status);
}
