using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>What one model has been asked to do, and what it reported back.</summary>
/// <param name="CachedInputTokens">
/// Null when the provider never reported a cached figure — which is not the same as zero. A provider
/// that says nothing about caching leaves this unknown, and the page says unknown.
/// </param>
public sealed record FamiliarChatModelUsage(
    string ProviderName,
    string ProviderModel,
    int Turns,
    int InputTokens,
    int OutputTokens,
    int? CachedInputTokens)
{
    /// <summary>
    /// The share of input served from the provider's prefix cache, or null when unreported.
    ///
    /// This is the number that says whether the stable-to-volatile prompt ordering is still working.
    /// It should climb as conversations lengthen; if it collapses, something has started varying in
    /// the prompt's head and every turn is costing several times what it should.
    /// </summary>
    public double? CacheHitRate =>
        CachedInputTokens is { } cached && InputTokens > 0 ? (double)cached / InputTokens : null;
}

/// <summary>
/// This server's own record of what it has sent to conversational providers.
///
/// <b>Deliberately not a billing figure, and the page must not present it as one.</b> These are counts
/// the provider reported on turns that got far enough to report anything: a request rejected before it
/// answered contributes nothing here and may still have been billed, and a provider's invoice includes
/// rounding, minimums and pricing this application knows nothing about. Calling this "spend" would be
/// exactly the confident-but-wrong number this project keeps refusing to print.
///
/// What it is good for is the question a person actually has — what has the Familiar been doing, on
/// which model, and is the prompt cache working — and it answers that from records this server owns,
/// with no API call, no second credential, and nothing restated second-hand.
/// </summary>
public interface IFamiliarChatUsageService
{
    Task<FamiliarChatUsage> GetUsageAsync(CancellationToken cancellationToken = default);
}

/// <param name="TurnsWithoutMetadata">
/// Completed turns that carry no provider attribution — everything answered before a provider was
/// configured, plus anything a generator produced without reporting. Surfaced rather than hidden, so
/// the totals below are visibly a subset of all conversation rather than appearing to be all of it.
/// </param>
public sealed record FamiliarChatUsage(
    int Conversations,
    int Turns,
    int FailedTurns,
    int TurnsWithoutMetadata,
    IReadOnlyList<FamiliarChatModelUsage> ByModel)
{
    public int TotalInputTokens => ByModel.Sum(model => model.InputTokens);

    public int TotalOutputTokens => ByModel.Sum(model => model.OutputTokens);

    public int? TotalCachedInputTokens =>
        ByModel.Any(model => model.CachedInputTokens is not null)
            ? ByModel.Sum(model => model.CachedInputTokens ?? 0)
            : null;

    public double? CacheHitRate =>
        TotalCachedInputTokens is { } cached && TotalInputTokens > 0
            ? (double)cached / TotalInputTokens
            : null;

    public bool HasAnything => Turns > 0;
}

public sealed class FamiliarChatUsageService(FamiliarDbContext dbContext) : IFamiliarChatUsageService
{
    public async Task<FamiliarChatUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        var conversations = await dbContext.FamiliarChats.AsNoTracking().CountAsync(cancellationToken);

        var turns = await dbContext.FamiliarChatTurns.AsNoTracking().CountAsync(cancellationToken);

        var failed = await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .CountAsync(turn => turn.State == FamiliarChatTurnState.Failed, cancellationToken);

        // Grouped in the database rather than by loading every turn: this runs on a dashboard render,
        // and a conversation history is the one table here with no natural bound on its size.
        var byModel = await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .Where(turn => turn.ProviderName != null && turn.ProviderModel != null)
            .GroupBy(turn => new { turn.ProviderName, turn.ProviderModel })
            .Select(group => new
            {
                group.Key.ProviderName,
                group.Key.ProviderModel,
                Turns = group.Count(),
                InputTokens = group.Sum(turn => turn.InputTokens ?? 0),
                OutputTokens = group.Sum(turn => turn.OutputTokens ?? 0),
                // Distinguishes "reported zero" from "never reported": if no turn on this model ever
                // carried a cached figure, the sum stays null and the page says unknown.
                ReportedCache = group.Count(turn => turn.CachedInputTokens != null),
                CachedInputTokens = group.Sum(turn => turn.CachedInputTokens ?? 0)
            })
            .OrderByDescending(group => group.Turns)
            .ToListAsync(cancellationToken);

        var attributed = byModel.Sum(model => model.Turns);

        return new FamiliarChatUsage(
            conversations,
            turns,
            failed,
            turns - attributed,
            byModel
                .Select(model => new FamiliarChatModelUsage(
                    model.ProviderName!,
                    model.ProviderModel!,
                    model.Turns,
                    model.InputTokens,
                    model.OutputTokens,
                    model.ReportedCache > 0 ? model.CachedInputTokens : null))
                .ToList());
    }
}
