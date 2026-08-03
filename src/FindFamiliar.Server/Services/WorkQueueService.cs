using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Services;

public sealed class WorkQueueService(FamiliarDbContext dbContext) : IWorkQueueService
{
    public async Task<IReadOnlyList<WorkQueueItem>> GetActiveQueueAsync(CancellationToken cancellationToken = default)
    {
        var tasks = await dbContext.Tasks
            .AsNoTracking()
            .Include(task => task.Project)
            .Where(task => task.Status != TaskStatus.Completed)
            .ToListAsync(cancellationToken);

        var taskIds = tasks.Select(task => task.Id).ToList();

        var sessions = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => taskIds.Contains(session.TaskId))
            .ToListAsync(cancellationToken);

        var sessionsByTask = sessions
            .GroupBy(session => session.TaskId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AgentSession>)group.ToList());

        var items = new List<WorkQueueItem>(tasks.Count);

        foreach (var task in tasks)
        {
            var taskSessions = sessionsByTask.TryGetValue(task.Id, out var found)
                ? found
                : Array.Empty<AgentSession>();

            var (actionKind, label, activeSession, startedCount) = DetermineAction(taskSessions);

            items.Add(new WorkQueueItem(
                task.ProjectId,
                task.Project.Name,
                task.Id,
                task.Title,
                task.Status,
                task.UpdatedUtc,
                actionKind,
                label,
                activeSession?.Id,
                activeSession?.Role,
                startedCount));
        }

        return items
            .OrderByDescending(item => item.TaskUpdatedUtc)
            .ThenBy(item => item.TaskId)
            .ToList();
    }

    private static (WorkQueueActionKind Kind, string Label, AgentSession? ActiveSession, int StartedCount) DetermineAction(
        IReadOnlyList<AgentSession> sessions)
    {
        var startedSessions = sessions.Where(session => session.Status == AgentSessionStatus.Started).ToList();

        if (startedSessions.Count > 1)
        {
            return (
                WorkQueueActionKind.NeedsAttention,
                $"Needs attention: {startedSessions.Count} sessions are Started for this task at once.",
                null,
                startedSessions.Count);
        }

        if (startedSessions.Count == 1)
        {
            var active = startedSessions[0];
            return (
                WorkQueueActionKind.ContinueSession,
                $"Continue: open the {active.Role} assignment packet, capture its result, or cancel it.",
                active,
                1);
        }

        if (sessions.Count == 0)
        {
            return (WorkQueueActionKind.StartPlanner, "Start a Planner session.", null, 0);
        }

        var latestTerminal = sessions
            .OrderByDescending(session => session.CompletedUtc)
            .ThenByDescending(session => session.StartedUtc)
            .ThenByDescending(session => session.Id)
            .First();

        if (latestTerminal.Status == AgentSessionStatus.Cancelled)
        {
            return (
                WorkQueueActionKind.RetryRole,
                $"Retry the {latestTerminal.Role} session — the previous attempt was cancelled.",
                null,
                0);
        }

        return latestTerminal.Role switch
        {
            AgentSessionRole.Planner => (WorkQueueActionKind.StartImplementer, "Start an Implementer session.", null, 0),
            AgentSessionRole.Implementer => (WorkQueueActionKind.StartReviewer, "Start a Reviewer session.", null, 0),
            AgentSessionRole.Reviewer => (WorkQueueActionKind.HumanDecision, "Human decision: complete the task or request more work.", null, 0),
            _ => throw new InvalidOperationException($"Unmapped agent session role '{latestTerminal.Role}'.")
        };
    }
}
