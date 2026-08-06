using FindFamiliar.Server.Services.Demiplane;

namespace FindFamiliar.Server.Services.Familiar.Chat.Brief;

/// <summary>One task, reduced to what a system-wide answer needs.</summary>
/// <remarks>
/// <see cref="DisplayState"/> and <see cref="ReasonText"/> are copied from the Demiplane's own
/// classification rather than derived again. ADR-0011 settled that the Demiplane owns what a task's
/// state is; a second opinion composed here would be a second set of rules to keep in step, and the
/// Familiar contradicting the Demiplane about the same task is precisely the failure this project
/// exists to prevent.
/// </remarks>
public sealed record BriefTask(
    Guid TaskId,
    string Title,
    TaskDisplayState DisplayState,
    string ReasonText,
    bool NeedsHumanAttention);

/// <summary>One project's shape at a glance, plus whichever of its tasks matter most right now.</summary>
public sealed record BriefProject(
    Guid ProjectId,
    string Name,
    string Purpose,
    int TotalTasks,
    int NeedsAttentionCount,
    int RunningCount,
    IReadOnlyList<BriefTask> Tasks,
    int TasksOmitted);

/// <summary>
/// The whole system as a model is shown it: every project, its health, what is running, and what is
/// waiting on a human.
///
/// This is the Demiplane's projection turned outward — the same facts a person reads on a per-project
/// page, rolled up across all projects and serialised for a reader that has no screen. It is
/// system-wide because cross-project questions are the point; a per-project brief would have made
/// "what should I work on?" unanswerable, which is the question the roadmap actually cares about.
///
/// Everything here is counted from persisted rows. Nothing is inferred, estimated, or predicted, and
/// there is no field for an opinion — <see cref="Limitations"/> is how the brief says what it could
/// not see, in the same spirit as the Familiar page's "What I can't see", so a model reading this is
/// told the edges of its own knowledge rather than left to assume it has everything.
/// </summary>
public sealed record FamiliarStandingBrief(
    IReadOnlyList<BriefProject> Projects,
    int TotalProjects,
    int ProjectsOmitted,
    int SensitiveProjectsWithheld,
    IReadOnlyList<string> Limitations,
    DateTimeOffset ObservedAt)
{
    /// <summary>
    /// A bound on the whole brief, in characters rather than tokens.
    ///
    /// Characters because they are what this application can count exactly; tokens are a provider's
    /// business and vary by tokenizer. Roughly four characters to a token puts this near the ~2k
    /// tokens the sprint plan budgeted, and the writer trims to fit rather than trusting the estimate.
    /// </summary>
    public const int MaxCharacters = 8_000;

    /// <summary>Projects carried in full before the brief starts summarising instead.</summary>
    public const int MaxProjects = 12;

    /// <summary>Tasks carried per project. The counts above always describe every task, not these.</summary>
    public const int MaxTasksPerProject = 8;

    public bool IsEmpty => Projects.Count == 0 && SensitiveProjectsWithheld == 0;
}
