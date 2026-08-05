using Microsoft.Extensions.Logging;

namespace FindFamiliar.Server.Services.Providers;

public interface IProviderCapacityService
{
    Task<IReadOnlyList<ProviderCapacitySnapshot>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Aggregates every registered <see cref="IProviderCapacityReader"/> for the readiness strip.
///
/// Two properties matter more than the data itself:
///
/// 1. <b>A reader cannot break the page.</b> Each is invoked independently and bounded by a timeout.
///    A throw, a hang or a cancellation becomes an Unavailable reading carrying the error, so a
///    misbehaving provider integration costs the user a strip entry rather than the whole Demiplane.
/// 2. <b>Readers cannot contaminate each other.</b> One reader's failure or slowness leaves the others
///    untouched, and each snapshot carries its own provider, source and observation time.
/// </summary>
public sealed class ProviderCapacityService(
    IEnumerable<IProviderCapacityReader> readers,
    TimeProvider timeProvider,
    ILogger<ProviderCapacityService> logger) : IProviderCapacityService
{
    /// <summary>
    /// A reader gets this long before it is abandoned. Short on purpose: the readiness strip is
    /// contextual information, and no provider integration should be able to hold a page open.
    /// </summary>
    public static readonly TimeSpan ReaderTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Beyond this age a reading is labelled stale in the UI rather than trusted or hidden.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<ProviderCapacitySnapshot>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshots = await Task.WhenAll(readers.Select(reader => ReadSafelyAsync(reader, cancellationToken)));

        return snapshots
            .OrderBy(snapshot => snapshot.Provider, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<ProviderCapacitySnapshot> ReadSafelyAsync(
        IProviderCapacityReader reader,
        CancellationToken cancellationToken)
    {
        var provider = SafeProviderName(reader);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ReaderTimeout);

            return await reader.GetCapacityAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Provider capacity reader {Provider} timed out.", provider);
            return ProviderCapacitySnapshot.Faulted(
                provider,
                timeProvider.GetUtcNow(),
                source: "reader-timeout",
                error: "The capacity reader did not respond in time.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Deliberately broad: a third-party reader may throw anything, and none of it is worth
            // losing the page over. The message is ours, not the exception's, so a reader cannot put
            // a stack trace or a credential on screen.
            logger.LogWarning(exception, "Provider capacity reader {Provider} failed.", provider);
            return ProviderCapacitySnapshot.Faulted(
                provider,
                timeProvider.GetUtcNow(),
                source: "reader-failed",
                error: "The capacity reader failed. See the server log for details.");
        }
    }

    /// <summary>Even the provider name comes from a reader, so reading it is guarded too.</summary>
    private static string SafeProviderName(IProviderCapacityReader reader)
    {
        try
        {
            return string.IsNullOrWhiteSpace(reader.Provider) ? "Unnamed provider" : reader.Provider;
        }
        catch
        {
            return "Unnamed provider";
        }
    }
}
