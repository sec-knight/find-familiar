using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// The worker that keeps the repository snapshot current, on the do lane and out of anybody's way.
///
/// Two triggers, and the second exists because the first is not guaranteed:
///
/// - the post-commit hook, through <see cref="RepositorySnapshotQueue"/>, which makes a snapshot
///   current within seconds of the repository actually moving;
/// - a timer, which makes it current anyway on a machine where the hook was never installed, or was
///   installed in one worktree and not the three others, or was wiped by a fresh clone.
///
/// A snapshot is also taken at startup, so a server that has been down through a week of commits does
/// not serve a week-old repository until the first tick.
///
/// Deliberately not on sprint boundaries. Tying this to a ceremony is how the previous arrangement
/// failed: the ceremony is the thing people skip when they are busy, which is exactly when the
/// repository is moving fastest and the snapshot matters most.
/// </summary>
public sealed class RepositorySnapshotHost(
    RepositorySnapshotQueue queue,
    IServiceScopeFactory scopeFactory,
    IOptions<RepositorySnapshotOptions> options,
    ILogger<RepositorySnapshotHost> logger) : BackgroundService
{
    private readonly RepositorySnapshotOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IsConfigured())
        {
            // Off by default, and said out loud once rather than discovered by a reader wondering why
            // the snapshot entry is missing.
            logger.LogInformation(
                "Repository snapshots are not configured ({Section}); none will be taken.",
                RepositorySnapshotOptions.SectionName);
            return;
        }

        queue.Request();

        using var timer = new PeriodicTimer(_options.Interval);

        var ticking = TickAsync(timer, stoppingToken);

        try
        {
            await foreach (var _ in queue.ReadAllAsync(stoppingToken))
            {
                await CaptureAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }

        await ticking;
    }

    private async Task TickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                queue.Request();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// One capture, in its own scope, and it never throws.
    ///
    /// A snapshot is a convenience that must not be able to take the server's background host down
    /// with it. Failing loudly in the log and trying again on the next trigger is the correct
    /// behaviour for something whose worst failure mode is that the Familiar's picture of the
    /// repository is one commit out of date.
    /// </summary>
    private async Task CaptureAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<IRepositorySnapshotService>();

            await snapshots.CaptureAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A repository state snapshot could not be captured.");
        }
    }
}
