namespace FindFamiliar.Server.Services;

public interface IWorkQueueService
{
    Task<IReadOnlyList<WorkQueueItem>> GetActiveQueueAsync(CancellationToken cancellationToken = default);
}
