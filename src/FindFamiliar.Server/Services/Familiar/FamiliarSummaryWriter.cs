using FindFamiliar.Server.Services.Demiplane;

namespace FindFamiliar.Server.Services.Familiar;

/// <summary>
/// The Familiar's account of a whole project, composed from the snapshot alone.
///
/// Every field is nullable or empty where the snapshot does not support a statement, and the page
/// renders nothing for it — the same discipline <see cref="FamiliarSummaryComposer"/> applies to a
/// single task. A null here means "the data does not say", never "nothing is wrong".
/// </summary>
public sealed record FamiliarProjectSummary(
    string ProjectStatement,
    string ActivityStatement,
    string? AttentionStatement,
    IReadOnlyList<string> AttentionDetails,
    string? BlockedStatement,
    IReadOnlyList<string> BlockedDetails,
    IReadOnlyList<string> Unknowns,
    IReadOnlyList<string> NextSteps)
{
    /// <summary>Every line this summary actually has, in reading order.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string> { ProjectStatement, ActivityStatement };

        if (AttentionStatement is not null)
        {
            lines.Add(AttentionStatement);
            lines.AddRange(AttentionDetails);
        }

        if (BlockedStatement is not null)
        {
            lines.Add(BlockedStatement);
            lines.AddRange(BlockedDetails);
        }

        lines.AddRange(NextSteps);
        lines.AddRange(Unknowns);

        return lines;
    }

    public string Render() => string.Join(Environment.NewLine, Lines());
}

/// <summary>
/// Writes the deterministic, provider-free summary of a project.
///
/// This exists for three reasons, and the third is the one that constrains it: it is the page's
/// floor when no reasoning provider is configured, it is what a reasoning answer is measured
/// against, and it is the one thing on the page a reviewer can diff against the database by hand.
/// That last property is lost the moment a sentence here is inferred rather than read.
///
/// So the rules are narrow. It states what the snapshot records and what the snapshot says it does
/// not know. It never explains a cause, because causes are not persisted. It never says an action
/// happened, because in this application only a persisted row says that. And it recommends only
/// what the Demiplane already recorded as the next action for a task — it has no advice of its own.
/// </summary>
public static class FamiliarSummaryWriter
{
    /// <summary>
    /// A summary that ends in a list of eight things to do is a list, not a summary. Three is the
    /// same bound the behaviour contract puts on the reasoning provider, so the floor and the
    /// provider answer cannot disagree about how much a person is being asked to hold.
    /// </summary>
    public const int MaxNextSteps = 3;

    /// <summary>How many tasks are named individually before the counts have to speak for the rest.</summary>
    public const int MaxNamedTasks = 3;

    public static FamiliarProjectSummary Compose(ProjectSnapshot snapshot)
    {
        var needingAttention = snapshot.Tasks
            .Where(task => task.NeedsHumanAttention)
            .ToList();

        var blocked = snapshot.Tasks
            .Where(task => task.DisplayState == TaskDisplayState.Blocked)
            .ToList();

        return new FamiliarProjectSummary(
            DescribeProject(snapshot),
            DescribeActivity(snapshot),
            DescribeAttention(snapshot),
            Name(needingAttention),
            DescribeBlocked(snapshot, blocked),
            Name(blocked),
            snapshot.Limitations,
            NextSteps(needingAttention));
    }

    private static string DescribeProject(ProjectSnapshot snapshot) =>
        $"{snapshot.ProjectName}. Project status {snapshot.ProjectStatus}, context revision {snapshot.ContextRevision}. "
        + $"{Count(snapshot.Health.TotalTasks, "task")} recorded.";

    private static string DescribeActivity(ProjectSnapshot snapshot)
    {
        var running = snapshot.Health.CountOf(TaskDisplayState.Running);
        var waiting = snapshot.Health.CountOf(TaskDisplayState.Waiting);

        if (running == 0 && waiting == 0)
        {
            // Supported by the counts, which cover the whole project rather than the tasks shown.
            return "Nothing is running and nothing is waiting for a worker.";
        }

        var parts = new List<string>();

        if (running > 0)
        {
            parts.Add($"{Count(running, "task")} {(running == 1 ? "is" : "are")} running.");
        }

        if (waiting > 0)
        {
            parts.Add($"{Count(waiting, "task")} {(waiting == 1 ? "is" : "are")} waiting for a worker.");
        }

        return string.Join(" ", parts);
    }

    private static string? DescribeAttention(ProjectSnapshot snapshot) =>
        snapshot.Health.NeedsAttentionCount == 0
            ? null
            : $"{Count(snapshot.Health.NeedsAttentionCount, "task")} "
              + $"{(snapshot.Health.NeedsAttentionCount == 1 ? "needs" : "need")} your attention.";

    private static string? DescribeBlocked(ProjectSnapshot snapshot, IReadOnlyList<SnapshotTask> blocked)
    {
        var blockedCount = snapshot.Health.CountOf(TaskDisplayState.Blocked);

        if (blockedCount == 0 && blocked.Count == 0)
        {
            return null;
        }

        return $"{Count(blockedCount, "task")} {(blockedCount == 1 ? "is" : "are")} blocked.";
    }

    /// <summary>
    /// Names tasks with the reason the Demiplane recorded, quoted rather than paraphrased. A
    /// paraphrase here would be a second wording of a state rule that has one home.
    /// </summary>
    private static IReadOnlyList<string> Name(IReadOnlyList<SnapshotTask> tasks) =>
        tasks
            .Take(MaxNamedTasks)
            .Select(task => $"\"{task.Title}\": {task.ReasonText}")
            .ToList();

    private static IReadOnlyList<string> NextSteps(IReadOnlyList<SnapshotTask> needingAttention) =>
        needingAttention
            .Where(task => !string.IsNullOrWhiteSpace(task.RecommendedNextAction))
            .Take(MaxNextSteps)
            .Select(task => $"\"{task.Title}\": {task.RecommendedNextAction}")
            .ToList();

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
}
