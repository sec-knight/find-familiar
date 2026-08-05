using System.Diagnostics;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The provider readiness layer.
///
/// Two properties are load-bearing and both are about not lying: a reader that cannot determine
/// capacity says Unknown rather than guessing, and a reader that misbehaves costs one strip entry
/// rather than the whole Demiplane.
/// </summary>
public sealed class ProviderCapacityServiceTests
{
    [Fact]
    public async Task The_shipped_reader_reports_unknown_and_says_why()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var reader = new UnknownProviderCapacityReader("Claude", time, "No usage surface exists.");

        var snapshot = await reader.GetCapacityAsync();

        Assert.Equal("Claude", snapshot.Provider);
        Assert.Equal(ProviderCapacityStatus.Unknown, snapshot.Status);
        Assert.Equal(ProviderCapacityConfidence.None, snapshot.Confidence);

        // Nothing quantitative may be invented to fill the UI.
        Assert.Empty(snapshot.Windows);
        Assert.Null(snapshot.CreditsRemaining);
        Assert.Null(snapshot.ResetsAt);
        Assert.Equal("No usage surface exists.", snapshot.Detail);
    }

    [Fact]
    public async Task A_throwing_reader_becomes_a_visible_unavailable_entry_and_the_others_still_report()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var service = NewService(
            time,
            new ThrowingReader("Broken"),
            new UnknownProviderCapacityReader("Claude", time, "No usage surface exists."));

        var snapshots = await service.GetAllAsync();

        Assert.Equal(2, snapshots.Count);

        var broken = Assert.Single(snapshots, snapshot => snapshot.Provider == "Broken");
        Assert.Equal(ProviderCapacityStatus.Unavailable, broken.Status);
        Assert.False(string.IsNullOrWhiteSpace(broken.Error));

        // Contamination check: the healthy reader is unaffected.
        var claude = Assert.Single(snapshots, snapshot => snapshot.Provider == "Claude");
        Assert.Equal(ProviderCapacityStatus.Unknown, claude.Status);
        Assert.Null(claude.Error);
    }

    /// <summary>
    /// The exception's own message never reaches the page: a third-party reader could put a stack
    /// trace, a path or a credential in it.
    /// </summary>
    [Fact]
    public async Task A_reader_failure_does_not_leak_its_exception_text()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var service = NewService(time, new ThrowingReader("Broken", "token=sk-secret-value at C:\\Users\\someone"));

        var snapshot = Assert.Single(await service.GetAllAsync());

        Assert.DoesNotContain("sk-secret-value", snapshot.Error!, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\Users", snapshot.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hanging_reader_is_abandoned_rather_than_holding_the_page_open()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var service = NewService(time, new HangingReader("Slow"));

        var snapshot = Assert.Single(await service.GetAllAsync());

        Assert.Equal(ProviderCapacityStatus.Unavailable, snapshot.Status);
        Assert.Equal("reader-timeout", snapshot.Source);
    }

    [Fact]
    public async Task A_reader_that_throws_from_its_provider_name_is_still_survivable()
    {
        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var service = NewService(time, new HostileNameReader());

        var snapshot = Assert.Single(await service.GetAllAsync());

        Assert.Equal("Unnamed provider", snapshot.Provider);
        Assert.Equal(ProviderCapacityStatus.Unavailable, snapshot.Status);
    }

    [Fact]
    public async Task No_readers_yields_an_empty_strip_rather_than_a_failure()
    {
        var service = NewService(new TestTimeProvider(DateTimeOffset.UtcNow));

        Assert.Empty(await service.GetAllAsync());
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(30, true)]
    public void A_reading_is_stale_only_once_it_is_older_than_the_window(int ageMinutes, bool expectedStale)
    {
        var observedAt = DateTimeOffset.UtcNow.AddMinutes(-ageMinutes);
        var snapshot = ProviderCapacitySnapshot.Unknown("Claude", observedAt, "test");

        Assert.Equal(
            expectedStale,
            snapshot.IsStale(DateTimeOffset.UtcNow, ProviderCapacityService.StaleAfter));
    }

    /// <summary>
    /// A reader that does have real data keeps it, including the fields the honest reader leaves
    /// null. This is the shape a future live reader must produce.
    /// </summary>
    [Fact]
    public async Task A_reader_with_real_data_carries_its_windows_and_confidence_through()
    {
        var observedAt = DateTimeOffset.UtcNow;
        var time = new TestTimeProvider(observedAt);
        var reported = new ProviderCapacitySnapshot(
            "Example",
            ProviderCapacityStatus.Constrained,
            ProviderCapacityConfidence.ProviderReported,
            observedAt,
            "example-api",
            [new ProviderUsageWindow("Five-hour window", 72, TimeSpan.FromHours(5), observedAt.AddHours(1))],
            CreditsRemaining: 12.5m,
            ResetsAt: observedAt.AddHours(1));

        var service = NewService(time, new FixedReader(reported));

        var snapshot = Assert.Single(await service.GetAllAsync());

        Assert.Equal(ProviderCapacityStatus.Constrained, snapshot.Status);
        Assert.Equal(ProviderCapacityConfidence.ProviderReported, snapshot.Confidence);
        Assert.Equal(72, Assert.Single(snapshot.Windows).UsedPercent);
        Assert.Equal(12.5m, snapshot.CreditsRemaining);
    }

    private static ProviderCapacityService NewService(TimeProvider time, params IProviderCapacityReader[] readers) =>
        new(readers, time, NullLogger<ProviderCapacityService>.Instance);

    private sealed class ThrowingReader(string provider, string? message = null) : IProviderCapacityReader
    {
        public string Provider { get; } = provider;

        public Task<ProviderCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message ?? "Reader exploded.");
    }

    private sealed class HangingReader(string provider) : IProviderCapacityReader
    {
        public string Provider { get; } = provider;

        public async Task<ProviderCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class HostileNameReader : IProviderCapacityReader
    {
        public string Provider => throw new InvalidOperationException("Even the name misbehaves.");

        public Task<ProviderCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("And so does the reading.");
    }

    private sealed class FixedReader(ProviderCapacitySnapshot snapshot) : IProviderCapacityReader
    {
        public string Provider => snapshot.Provider;

        public Task<ProviderCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
}
