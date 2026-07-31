using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services;

public sealed class ContextProjectionService(FamiliarDbContext dbContext) : IContextProjectionService
{
    public async Task<TaskContextDocument?> GetTaskContextAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await dbContext.Tasks
            .AsNoTracking()
            .Include(candidate => candidate.Project)
            .SingleOrDefaultAsync(candidate => candidate.Id == taskId, cancellationToken);

        if (task is null)
        {
            return null;
        }

        var entries = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == task.ProjectId
                && entry.State == ContextEntryState.Active
                && (entry.TaskId == null || entry.TaskId == task.Id))
            .OrderBy(entry => entry.CreatedUtc)
            .ToListAsync(cancellationToken);

        var sessions = await dbContext.AgentSessions
            .AsNoTracking()
            .Where(session => session.TaskId == task.Id)
            .OrderBy(session => session.StartedUtc)
            .Select(session => new AgentSessionDocument(
                session.Id,
                session.Role,
                session.Provider,
                session.ExternalSessionReference,
                session.Status,
                session.ContextRevisionRead,
                session.StartedUtc,
                session.CompletedUtc))
            .ToListAsync(cancellationToken);

        var projectEntries = entries
            .Where(entry => entry.TaskId is null)
            .Select(entry => new ContextEntryDocument(
                entry.Id,
                entry.Kind,
                entry.Title,
                entry.Content,
                entry.CreatedUtc,
                entry.SourceSessionId))
            .ToList();

        var taskEntries = entries
            .Where(entry => entry.TaskId.HasValue)
            .Select(entry => new ContextEntryDocument(
                entry.Id,
                entry.Kind,
                entry.Title,
                entry.Content,
                entry.CreatedUtc,
                entry.SourceSessionId))
            .ToList();

        return new TaskContextDocument(
            new ProjectContextDocument(
                task.Project.Id,
                task.Project.Name,
                task.Project.Purpose,
                task.Project.Status,
                task.Project.ContextRevision),
            new TaskContextTaskDocument(
                task.Id,
                task.Title,
                task.RequestedOutcome,
                task.Status,
                task.CreatedUtc,
                task.UpdatedUtc),
            projectEntries,
            taskEntries,
            sessions);
    }
}
