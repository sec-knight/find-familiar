using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Services.Familiar.Chat;

/// <summary>
/// Turns a streaming provider into a generation the host can run: assemble the request, stream the
/// reply into the turn, classify the ending.
///
/// The seam split is deliberate. <see cref="IFamiliarChatProvider"/> is the wire — swappable, stateless,
/// knowing nothing about turns or a database. This class is the part that knows what a conversation
/// is, and it is where slice 3's standing brief and slice 4's tool results will be assembled. Keeping
/// them apart is what lets the provider be replaced without touching context assembly, and context
/// assembly to be tested without a network.
///
/// It is also the chokepoint through which everything leaves this machine. Nothing reaches a provider
/// except the system prompt, prior visible turns of this one conversation, and the person's message —
/// there is no project state in a slice 2 request because there is no code here that reads any.
/// </summary>
public sealed class ProviderFamiliarChatGenerator(
    FamiliarDbContext dbContext,
    IFamiliarChatProvider provider) : IFamiliarChatGenerator
{
    /// <summary>
    /// Prior exchanges sent back with a new message. A count, not a size — Sprint 12 caps working
    /// memory rather than compacting it, and a long conversation is allowed to degrade at the far end
    /// rather than be silently summarised into something the person never approved.
    /// </summary>
    public const int MaxHistoryTurns = 12;

    public string Name => provider.Name;

    public async Task<FamiliarChatGenerationOutcome> GenerateAsync(
        FamiliarChatGenerationRequest request,
        IFamiliarChatOutputSink sink,
        CancellationToken cancellationToken = default)
    {
        var history = await ReadHistoryAsync(request.ChatId, request.Sequence, cancellationToken);

        var prompt = new FamiliarChatRequest(
            FamiliarChatSystemPrompt.Text,
            history,
            request.UserText);

        var emitted = 0;
        var metadata = new FamiliarChatGenerationMetadata(provider.Name, provider.Model);
        FamiliarChatStreamEvent.Finished? finished = null;

        await foreach (var streamEvent in provider.StreamAsync(prompt, cancellationToken))
        {
            switch (streamEvent)
            {
                case FamiliarChatStreamEvent.Delta delta when delta.Text.Length > 0:
                    // Straight into the persisted turn. Nobody has to be listening for this to be
                    // kept, which is the property the whole lane is built around.
                    await sink.AppendAsync(delta.Text, cancellationToken);
                    emitted += delta.Text.Length;
                    break;

                case FamiliarChatStreamEvent.Finished terminal:
                    finished = terminal;
                    break;
            }
        }

        if (finished is null)
        {
            // The interface says exactly one Finished is emitted on every path. A stream that ends
            // without one has broken that contract, and a truncated reply must not be recorded as a
            // whole one.
            return await FailAsync(FamiliarChatProviderStatus.Malformed, emitted, metadata, sink, cancellationToken);
        }

        metadata = metadata with
        {
            // What actually answered, when the endpoint named it.
            ProviderModel = string.IsNullOrWhiteSpace(finished.Model) ? provider.Model : finished.Model,
            InputTokens = finished.InputTokens,
            OutputTokens = finished.OutputTokens
        };

        if (finished.Status != FamiliarChatProviderStatus.Completed)
        {
            return await FailAsync(finished.Status, emitted, metadata, sink, cancellationToken);
        }

        if (emitted == 0)
        {
            // A completed stream that said nothing. Recording it as a delivered empty reply would put
            // a silent bubble on the page and call it an answer.
            var note = FamiliarChatFailureWording.For(FamiliarChatProviderStatus.Completed);
            return FamiliarChatGenerationOutcome.Failed(note.Code, note.Sentence, metadata);
        }

        return FamiliarChatGenerationOutcome.Answered(metadata);
    }

    /// <summary>
    /// Records a failure, keeping whatever already arrived.
    ///
    /// A stream that stopped part way is two facts at once: the text on screen is real, and it is
    /// incomplete. Both are preserved — the partial reply stays, and a short note is appended through
    /// the same sink so the transcript never disagrees with what the person already read.
    /// </summary>
    private static async Task<FamiliarChatGenerationOutcome> FailAsync(
        FamiliarChatProviderStatus status,
        int emitted,
        FamiliarChatGenerationMetadata metadata,
        IFamiliarChatOutputSink sink,
        CancellationToken cancellationToken)
    {
        if (emitted == 0)
        {
            var note = FamiliarChatFailureWording.For(status);
            return FamiliarChatGenerationOutcome.Failed(note.Code, note.Sentence, metadata);
        }

        var truncated = FamiliarChatFailureWording.Truncated(status);
        await sink.AppendAsync(truncated.Sentence, cancellationToken);

        // No sentence: the host writes one only where no output exists, and output exists.
        return FamiliarChatGenerationOutcome.Failed(truncated.Code, null, metadata);
    }

    /// <summary>
    /// The exchanges sent back with this message: the most recent completed turns before it, oldest
    /// first.
    ///
    /// Failed turns are excluded. Their output is this application's own sentence about a component
    /// the Familiar cannot observe, and re-feeding it would teach a model to imitate error text — to
    /// write "the conversational provider could not be reached" as though it were an observation of
    /// its own.
    /// </summary>
    private async Task<IReadOnlyList<FamiliarChatHistoryTurn>> ReadHistoryAsync(
        Guid chatId,
        int beforeSequence,
        CancellationToken cancellationToken)
    {
        var recent = await dbContext.FamiliarChatTurns
            .AsNoTracking()
            .Where(turn =>
                turn.ChatId == chatId
                && turn.Sequence < beforeSequence
                && turn.State == FamiliarChatTurnState.Completed)
            .OrderByDescending(turn => turn.Sequence)
            .Take(MaxHistoryTurns)
            .Select(turn => new { turn.Sequence, turn.UserText, turn.Output })
            .ToListAsync(cancellationToken);

        return recent
            .OrderBy(turn => turn.Sequence)
            .Select(turn => new FamiliarChatHistoryTurn(turn.UserText, turn.Output))
            .ToList();
    }
}
