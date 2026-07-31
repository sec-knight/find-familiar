namespace FindFamiliar.Server.Services;

public interface IContextProjectionService
{
    Task<TaskContextDocument?> GetTaskContextAsync(Guid taskId, CancellationToken cancellationToken = default);
}
