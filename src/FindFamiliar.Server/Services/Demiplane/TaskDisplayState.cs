namespace FindFamiliar.Server.Services.Demiplane;

/// <summary>
/// The normalized state a task shows a human. Deliberately separate from <c>TaskStatus</c> and
/// <c>AgentSessionStatus</c>, which are execution authority: this enum answers "what should a person
/// understand about this task", and nothing consults it to decide what may run.
/// </summary>
public enum TaskDisplayState
{
    /// <summary>No session has ever run.</summary>
    NotStarted,

    /// <summary>Something must happen elsewhere before this task moves. See the reason.</summary>
    Waiting,

    /// <summary>A session is claimed and executing now.</summary>
    Running,

    /// <summary>A human decision is outstanding. This is the only state that asks for the user.</summary>
    NeedsAttention,

    /// <summary>A human marked the task blocked.</summary>
    Blocked,

    /// <summary>A human marked the task complete.</summary>
    Succeeded,

    /// <summary>A session ended because something went wrong, not because a human stopped it.</summary>
    Failed,

    /// <summary>A human deliberately stopped the work.</summary>
    Cancelled
}

/// <summary>
/// Why a task is in its display state. A code rather than a string so tests assert meaning, the UI
/// chooses its own wording, and no view has to re-derive the rule.
/// </summary>
public enum TaskDisplayReasonCode
{
    /// <summary>No further explanation applies.</summary>
    None,

    NeverStarted,

    /// <summary>A proposed next step is waiting for approval or decline (Sprint 09).</summary>
    AwaitingHumanApproval,

    /// <summary>A Reviewer finished. Completing a task is always a human decision.</summary>
    AwaitingHumanDecisionAfterReview,

    /// <summary>The proposed step was declined, so nothing will happen without a new decision.</summary>
    ProposedStepDeclined,

    /// <summary>Started and unclaimed, and at least one enabled worker could claim it.</summary>
    AwaitingWorkerPickup,

    /// <summary>
    /// Started and unclaimed, and no enabled worker declares this role. The task cannot progress and
    /// — because a task may hold only one Started session — nothing else can start on it either.
    /// </summary>
    NoWorkerForRole,

    /// <summary>Claimed and executing.</summary>
    SessionRunning,

    /// <summary>Claimed, but the lease expired without a result. Recoverable: it becomes claimable again.</summary>
    LeaseExpired,

    /// <summary>A human marked the task blocked. The domain records no further reason.</summary>
    MarkedBlockedByHuman,

    /// <summary>A human marked the task complete.</summary>
    MarkedCompleteByHuman,

    /// <summary>A human cancelled the session, with their own reason.</summary>
    CancelledByHuman,

    // Failure categories below are recognised from the fixed diagnostic strings this codebase's own
    // runner writes when it records a durable cancellation. They are never inferred from
    // model-authored text.

    /// <summary>The provider runtime could not be launched.</summary>
    ProviderRuntimeLaunchFailed,

    /// <summary>The provider run exceeded its time limit.</summary>
    ProviderRunTimedOut,

    /// <summary>
    /// The provider exited non-zero. Note that a usage-limit rejection currently arrives here too:
    /// the adapter cannot yet distinguish exhaustion from any other provider error.
    /// </summary>
    ProviderRequestFailed,

    /// <summary>The provider returned output the adapter could not use.</summary>
    ProviderResponseUnusable,

    /// <summary>
    /// The session stopped because the provider had no capacity left. This is a scheduling
    /// condition, not an implementation failure, and never maps to <see cref="TaskDisplayState.Failed"/>.
    /// Not yet reachable from live data — see ADR-0011.
    /// </summary>
    WaitingForProviderCapacity,

    /// <summary>
    /// More than one Started session exists. Unreachable through the application since ADR-0010's
    /// index; it indicates a database restored from before that migration.
    /// </summary>
    MultipleStartedSessions,

    /// <summary>
    /// The state is real but the data cannot explain it. Shown as unknown rather than guessed.
    /// </summary>
    Unknown
}
