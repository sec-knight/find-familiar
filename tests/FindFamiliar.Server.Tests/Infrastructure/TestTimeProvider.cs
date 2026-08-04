namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// A manually advanced clock. Lease expiry and heartbeat staleness are time-dependent behaviors,
/// and controlling the clock is what lets those tests be deterministic instead of sleeping for
/// real durations.
/// </summary>
public sealed class TestTimeProvider(DateTimeOffset nowUtc) : TimeProvider
{
    public DateTimeOffset NowUtc { get; private set; } = nowUtc;

    public override DateTimeOffset GetUtcNow() => NowUtc;

    public void Advance(TimeSpan delta) => NowUtc += delta;
}
