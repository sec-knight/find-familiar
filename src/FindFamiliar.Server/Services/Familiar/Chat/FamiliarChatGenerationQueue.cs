using System.Threading.Channels;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// The handoff from a request that accepted a turn to the host that generates it.
///
/// A singleton in-memory channel, and deliberately not a durable queue. The database already is the
/// durable record: a turn is <see cref="Domain.FamiliarChatTurnState.Pending"/> the moment it
/// commits, and the host's startup sweep re-enqueues every Pending turn it finds. So losing this
/// channel — a crash, a restart, a lost process — costs a scheduling hint and never a turn.
///
/// Unbounded, because the producer is a web request that has already committed. Blocking or dropping
/// there would mean a turn that is durable but unscheduled, which is exactly the state the sweep
/// exists to make impossible.
/// </summary>
public sealed class FamiliarChatGenerationQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    /// <summary>
    /// Schedules a committed turn. Never throws and never blocks: the caller has already made the
    /// turn durable, and failing here must not fail their request.
    /// </summary>
    public void Enqueue(Guid turnId) => _channel.Writer.TryWrite(turnId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
