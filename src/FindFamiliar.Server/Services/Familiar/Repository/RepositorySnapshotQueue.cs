using System.Threading.Channels;

namespace FindFamiliar.Server.Services.Familiar.Repository;

/// <summary>
/// The request to take a snapshot, from whoever noticed the repository moved.
///
/// A channel of capacity one that drops writes when full, which is exactly the semantics wanted: a
/// snapshot describes the repository <i>now</i>, so ten commits landing in twenty seconds should
/// produce one capture of the final state rather than ten captures, nine of which are already wrong
/// by the time they are written. Coalescing is the feature.
///
/// Singleton, and holds no durable state. A request lost to a restart costs one capture; the timer in
/// <see cref="RepositorySnapshotHost"/> is what makes that acceptable, and the startup capture is what
/// makes it invisible.
/// </summary>
public sealed class RepositorySnapshotQueue
{
    private readonly Channel<byte> _channel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true
        });

    /// <summary>Asks for a capture. Never blocks, never fails, and never queues a second one.</summary>
    public void Request() => _channel.Writer.TryWrite(0);

    public IAsyncEnumerable<byte> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
