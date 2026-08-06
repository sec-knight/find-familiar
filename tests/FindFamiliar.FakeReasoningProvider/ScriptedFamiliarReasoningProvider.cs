using FindFamiliar.Server.Services.Familiar.Reasoning;

namespace FindFamiliar.FakeReasoningProvider;

/// <summary>
/// A reasoning provider whose every answer is scripted, mirroring <c>FindFamiliar.FakeAdapter</c>.
///
/// It exists so the conversation flow can be driven through every branch of specification §7 with no
/// credential, no network and no real model — the same reason the fake adapter exists for the runner.
/// It records what it was asked, so a test can assert what was <b>not</b> sent as easily as what was:
/// that System notes never reach history, that history is bounded, that the request that went out is
/// the one that was measured.
/// </summary>
public sealed class ScriptedFamiliarReasoningProvider : IFamiliarReasoningProvider
{
    private readonly Queue<Func<FamiliarReasoningRequest, CancellationToken, Task<FamiliarReasoningOutcome>>> _script = new();

    public ScriptedFamiliarReasoningProvider(string provider = "Fake", string? model = "fake-model-1")
    {
        Provider = provider;
        Model = model;
    }

    public string Provider { get; }

    public string? Model { get; }

    /// <summary>Every request this provider was handed, in order.</summary>
    public List<FamiliarReasoningRequest> Requests { get; } = [];

    public int CallCount => Requests.Count;

    /// <summary>What is returned once the script runs out. Defaults to an ordinary reply.</summary>
    public Func<FamiliarReasoningRequest, FamiliarReasoningOutcome>? Fallback { get; set; }

    public ScriptedFamiliarReasoningProvider Enqueue(FamiliarReasoningOutcome outcome)
    {
        _script.Enqueue((_, _) => Task.FromResult(outcome));
        return this;
    }

    public ScriptedFamiliarReasoningProvider EnqueueAnswer(
        string reply,
        IReadOnlyList<Guid>? evidenceIds = null,
        IReadOnlyList<ProposedActionDraft>? actions = null) =>
        Enqueue(FamiliarReasoningOutcome.Answered(reply, Metadata(), actions, evidenceIds));

    public ScriptedFamiliarReasoningProvider EnqueueFailure(
        FamiliarReasoningStatus status,
        string detail = "Scripted failure.") =>
        Enqueue(FamiliarReasoningOutcome.Failed(status, Metadata(), detail));

    /// <summary>
    /// Throws from inside the provider, which the interface forbids.
    ///
    /// The point is that an implementation may break its contract anyway, and the conversation
    /// service must still render a page rather than a 500 — and must not carry the exception's text
    /// anywhere near the database or the user.
    /// </summary>
    public ScriptedFamiliarReasoningProvider EnqueueThrow(Exception exception)
    {
        _script.Enqueue((_, _) => throw exception);
        return this;
    }

    /// <summary>Hangs until cancelled, so the caller's timeout is what ends the call.</summary>
    public ScriptedFamiliarReasoningProvider EnqueueHang()
    {
        _script.Enqueue(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new UnreachableException();
        });

        return this;
    }

    public async Task<FamiliarReasoningOutcome> RespondAsync(
        FamiliarReasoningRequest request,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (_script.Count > 0)
        {
            return await _script.Dequeue()(request, cancellationToken);
        }

        return Fallback?.Invoke(request)
            ?? FamiliarReasoningOutcome.Answered("Scripted reply.", Metadata());
    }

    private FamiliarProviderMetadata Metadata() => new(Provider, Model, null);

    private sealed class UnreachableException() : InvalidOperationException("Unreachable.");
}
