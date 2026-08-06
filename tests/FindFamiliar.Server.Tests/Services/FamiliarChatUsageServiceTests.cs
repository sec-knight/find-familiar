using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The usage roll-up behind the dashboard panel.
///
/// The distinction these protect is between <i>reported zero</i> and <i>never reported</i>. A provider
/// that says nothing about prompt caching leaves the figure unknown, and showing 0% there would be a
/// confident claim about something never observed — on a dashboard, next to figures that are real. The
/// same rule applies to turns carrying no attribution: they are surfaced as excluded rather than
/// folded silently into totals that would then appear to cover everything.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarChatUsageServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_untouched_system_reports_nothing_rather_than_zeroes()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var usage = await new FamiliarChatUsageService(dbContext).GetUsageAsync();

        Assert.False(usage.HasAnything);
        Assert.Empty(usage.ByModel);
        Assert.Null(usage.CacheHitRate);
    }

    [Fact]
    public async Task Turns_are_grouped_by_provider_and_model()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);

        await AddTurnAsync(dbContext, chatId, 1, "xAI", "grok-a", 100, 10, 40);
        await AddTurnAsync(dbContext, chatId, 2, "xAI", "grok-a", 200, 20, 60);
        await AddTurnAsync(dbContext, chatId, 3, "xAI", "grok-b", 50, 5, null);

        var usage = await new FamiliarChatUsageService(dbContext).GetUsageAsync();

        Assert.Equal(3, usage.Turns);
        Assert.Equal(2, usage.ByModel.Count);
        Assert.Equal(350, usage.TotalInputTokens);
        Assert.Equal(35, usage.TotalOutputTokens);

        var first = usage.ByModel[0];
        Assert.Equal("grok-a", first.ProviderModel);
        Assert.Equal(2, first.Turns);
        Assert.Equal(100, first.CachedInputTokens);
    }

    /// <summary>
    /// The load-bearing distinction. A model that never reported caching reads as unknown, not as a
    /// cache that missed — the dashboard says "not reported" rather than printing 0%.
    /// </summary>
    [Fact]
    public async Task A_model_that_never_reported_caching_reads_as_unknown()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);
        await AddTurnAsync(dbContext, chatId, 1, "xAI", "grok-quiet", 100, 10, cachedInputTokens: null);

        var usage = await new FamiliarChatUsageService(dbContext).GetUsageAsync();

        var model = Assert.Single(usage.ByModel);
        Assert.Null(model.CachedInputTokens);
        Assert.Null(model.CacheHitRate);
        Assert.Equal("not reported", FindFamiliar.Server.Pages.IndexModel.CacheHitRateLabel(model.CacheHitRate));
    }

    /// <summary>A reported zero is a real observation and reads as 0%, not as unknown.</summary>
    [Fact]
    public async Task A_reported_zero_is_distinguishable_from_no_report()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);
        await AddTurnAsync(dbContext, chatId, 1, "xAI", "grok-cold", 100, 10, cachedInputTokens: 0);

        var usage = await new FamiliarChatUsageService(dbContext).GetUsageAsync();

        var model = Assert.Single(usage.ByModel);
        Assert.Equal(0, model.CachedInputTokens);
        Assert.Equal(0d, model.CacheHitRate);
        Assert.Equal("0 %", FindFamiliar.Server.Pages.IndexModel.CacheHitRateLabel(model.CacheHitRate));
    }

    /// <summary>
    /// Turns with no attribution — everything answered before a provider was configured — are counted
    /// and surfaced, so the totals are visibly a subset rather than appearing to cover everything.
    /// </summary>
    [Fact]
    public async Task Turns_without_attribution_are_counted_separately()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);

        await AddTurnAsync(dbContext, chatId, 1, "xAI", "grok-a", 100, 10, 40);
        await AddTurnAsync(dbContext, chatId, 2, provider: null, model: null, null, null, null);

        var usage = await new FamiliarChatUsageService(dbContext).GetUsageAsync();

        Assert.Equal(2, usage.Turns);
        Assert.Equal(1, usage.TurnsWithoutMetadata);
        Assert.Equal(100, usage.TotalInputTokens);
    }

    [Fact]
    public async Task Failed_turns_are_counted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedChatAsync(dbContext);

        await AddTurnAsync(dbContext, chatId, 1, "xAI", "grok-a", 100, 10, 40);
        await AddTurnAsync(
            dbContext, chatId, 2, "xAI", "grok-a", 20, null, null, FamiliarChatTurnState.Failed, "chat-timed-out");

        var usage = await new FamiliarChatUsageService(dbContext).GetUsageAsync();

        Assert.Equal(2, usage.Turns);
        Assert.Equal(1, usage.FailedTurns);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Guid> SeedChatAsync(FamiliarDbContext dbContext)
    {
        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Seeded for FamiliarChatUsageServiceTests",
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.FamiliarChats.Add(chat);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return chat.Id;
    }

    private static async Task AddTurnAsync(
        FamiliarDbContext dbContext,
        Guid chatId,
        int sequence,
        string? provider,
        string? model,
        int? inputTokens,
        int? outputTokens,
        int? cachedInputTokens,
        FamiliarChatTurnState state = FamiliarChatTurnState.Completed,
        string? failureCode = null)
    {
        dbContext.FamiliarChatTurns.Add(new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Sequence = sequence,
            State = state,
            UserText = "Seeded for FamiliarChatUsageServiceTests.",
            Output = "An answer.",
            ProviderName = provider,
            ProviderModel = model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            CachedInputTokens = cachedInputTokens,
            FailureCode = failureCode,
            CreatedUtc = Now,
            CompletedUtc = Now
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }
}
