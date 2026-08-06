using FindFamiliar.FakeReasoningProvider;
using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Familiar.Reasoning;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The send flow: two transactions, a bounded request, and a provider that cannot reach the database.
///
/// The property most of these exist to protect is the one the two-transaction shape was chosen for —
/// a person's words survive a provider that hangs, faults, or returns something unusable. The rest
/// protect the boundary: nothing a provider says about a failure reaches the page or a column.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarConversationServiceTests
{
    // ---------------------------------------------------------------- durability

    /// <summary>
    /// The load-bearing test for the two-transaction shape: the provider throws, and the human
    /// message is still there afterwards.
    /// </summary>
    [Fact]
    public async Task The_human_message_is_durable_before_the_provider_is_called()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueThrow(new InvalidOperationException("provider exploded"));

        var result = await NewService(dbContext, provider).SendAsync(project.Id, "why is this blocked?");

        Assert.Equal(FamiliarSendStatus.Reported, result.Status);

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal(FamiliarMessageAuthor.Human, messages[0].Author);
        Assert.Equal("why is this blocked?", messages[0].Content);
    }

    /// <summary>
    /// The provider is only ever called after the human message is committed — asserted by observing
    /// the committed row from an independent context at the moment the provider runs.
    /// </summary>
    [Fact]
    public async Task The_human_message_is_committed_before_the_provider_runs()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        await using var observer = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        var committedWhenCalled = 0;
        provider.Fallback = _ =>
        {
            committedWhenCalled = observer.FamiliarMessages.AsNoTracking().Count();
            return FamiliarReasoningOutcome.Answered("Answer.", new FamiliarProviderMetadata("Fake", "fake-model-1", null));
        };

        await NewService(dbContext, provider).SendAsync(project.Id, "a question");

        Assert.Equal(1, committedWhenCalled);
    }

    // ---------------------------------------------------------------- validation

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public async Task An_empty_message_writes_nothing(string message)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        var result = await NewService(dbContext, provider).SendAsync(project.Id, message);

        Assert.Equal(FamiliarSendStatus.Invalid, result.Status);
        Assert.NotNull(result.ValidationMessage);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(await dbContext.FamiliarConversations.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.FamiliarMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_message_over_the_cap_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        var oversized = new string('x', FamiliarConversationService.MaxUserMessageCharacters + 1);

        var result = await NewService(dbContext, provider).SendAsync(project.Id, oversized);

        Assert.Equal(FamiliarSendStatus.Invalid, result.Status);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(await dbContext.FamiliarMessages.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_message_at_exactly_the_cap_is_accepted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("Fine.");

        var atCap = new string('x', FamiliarConversationService.MaxUserMessageCharacters);
        var result = await NewService(dbContext, provider).SendAsync(project.Id, atCap);

        Assert.Equal(FamiliarSendStatus.Answered, result.Status);
    }

    [Fact]
    public async Task An_unknown_project_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var provider = new ScriptedFamiliarReasoningProvider();
        var result = await NewService(dbContext, provider).SendAsync(Guid.NewGuid(), "hello");

        Assert.Equal(FamiliarSendStatus.ProjectNotFound, result.Status);
        Assert.Equal(0, provider.CallCount);
        Assert.Empty(await dbContext.FamiliarConversations.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- failure wording

    /// <summary>Every failure status produces its System note and nothing else.</summary>
    [Theory]
    [InlineData(FamiliarReasoningStatus.Unavailable, "provider-unavailable")]
    [InlineData(FamiliarReasoningStatus.Unauthenticated, "provider-unauthenticated")]
    [InlineData(FamiliarReasoningStatus.TimedOut, "provider-timeout")]
    [InlineData(FamiliarReasoningStatus.RateLimited, "provider-rate-limited")]
    [InlineData(FamiliarReasoningStatus.Malformed, "provider-response-unusable")]
    [InlineData(FamiliarReasoningStatus.Declined, "provider-declined")]
    public async Task Each_failure_status_writes_its_fixed_code(
        FamiliarReasoningStatus status,
        string expectedCode)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueFailure(status);

        var result = await NewService(dbContext, provider).SendAsync(project.Id, "a question");

        Assert.Equal(FamiliarSendStatus.Reported, result.Status);

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal(2, messages.Count);
        Assert.Equal(FamiliarMessageAuthor.System, messages[1].Author);
        Assert.Equal(expectedCode, messages[1].FailureCode);
        Assert.Equal(FamiliarMessageDelivery.Failed, messages[1].Delivery);

        // A System note is not speech: it carries no provider attribution.
        Assert.Null(messages[1].ProviderName);
        Assert.Null(messages[1].ProviderModel);
    }

    /// <summary>
    /// The unconfigured default is distinguished from a configured provider that could not be
    /// reached — different facts, different sentences.
    /// </summary>
    [Fact]
    public async Task An_unconfigured_provider_reports_that_none_is_configured()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var result = await NewService(dbContext, new UnconfiguredFamiliarReasoningProvider())
            .SendAsync(project.Id, "what is happening here?");

        Assert.Equal(FamiliarSendStatus.Reported, result.Status);

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal("provider-not-configured", messages[1].FailureCode);
        Assert.Contains("No reasoning provider is configured", messages[1].Content, StringComparison.Ordinal);
    }

    /// <summary>An Answered with no reply has broken the contract, and is recorded as unusable.</summary>
    [Fact]
    public async Task An_answered_outcome_with_no_reply_is_treated_as_unusable()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.Enqueue(new FamiliarReasoningOutcome(
            FamiliarReasoningStatus.Answered, "   ", [], [], new FamiliarProviderMetadata("Fake", "fake-model-1", null), null));

        await NewService(dbContext, provider).SendAsync(project.Id, "a question");

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal(FamiliarMessageAuthor.System, messages[1].Author);
        Assert.Equal("provider-response-unusable", messages[1].FailureCode);
    }

    // ---------------------------------------------------------------- redaction

    /// <summary>
    /// The redaction proof, with a synthetic credential and a machine path planted in the provider's
    /// Detail so there is something real to catch. Neither may reach any column.
    /// </summary>
    [Fact]
    public async Task Provider_detail_never_reaches_the_database()
    {
        const string FakeCredential = "sk-ant-not-a-real-key-0000000000000000";
        const string FakePath = "/srv/familiar/secrets/runner-bridge.token";
        const string FakeHost = "https://api.example.invalid/v1/messages";

        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueFailure(
            FamiliarReasoningStatus.Unauthenticated,
            $"401 from {FakeHost} using {FakeCredential} configured at {FakePath}");

        await NewService(dbContext, provider).SendAsync(project.Id, "a question");

        var everythingStored = string.Join(
            "\n",
            (await dbContext.FamiliarMessages.AsNoTracking().ToListAsync())
                .SelectMany(message => new[] { message.Content, message.FailureCode, message.ProviderName, message.ProviderModel })
                .Where(value => value is not null));

        Assert.DoesNotContain(FakeCredential, everythingStored, StringComparison.Ordinal);
        Assert.DoesNotContain(FakePath, everythingStored, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeHost, everythingStored, StringComparison.Ordinal);
        Assert.DoesNotContain("401", everythingStored, StringComparison.Ordinal);
    }

    /// <summary>An exception message from a misbehaving provider is not carried anywhere either.</summary>
    [Fact]
    public async Task A_provider_exception_message_never_reaches_the_database()
    {
        const string Secret = "sk-ant-not-a-real-key-1111111111111111";

        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueThrow(new InvalidOperationException($"boom at /home/wizard/app with {Secret}"));

        await NewService(dbContext, provider).SendAsync(project.Id, "a question");

        var stored = string.Join("\n",
            (await dbContext.FamiliarMessages.AsNoTracking().ToListAsync()).Select(message => message.Content));

        Assert.DoesNotContain(Secret, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/wizard", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("boom", stored, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- cancellation and timeout

    /// <summary>The application's own bound elapsing is a timeout, and says so.</summary>
    [Fact]
    public async Task A_provider_that_hangs_is_recorded_as_a_timeout()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueHang();

        var result = await NewService(dbContext, provider, timeoutSeconds: 5).SendAsync(project.Id, "a question");

        Assert.Equal(FamiliarSendStatus.Reported, result.Status);

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal("provider-timeout", messages[1].FailureCode);
        Assert.Contains("within 5 seconds", messages[1].Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller who went away is not a provider that timed out. The cancellation propagates rather
    /// than being recorded as a provider failure the provider never had.
    /// </summary>
    [Fact]
    public async Task Caller_cancellation_is_not_swallowed_as_a_timeout()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        using var caller = new CancellationTokenSource();

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.Fallback = _ =>
        {
            caller.Cancel();
            caller.Token.ThrowIfCancellationRequested();
            return null!;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NewService(dbContext, provider).SendAsync(project.Id, "a question", caller.Token));

        // The human message committed before the provider ran, so abandoning the request does not
        // lose what the person typed.
        dbContext.ChangeTracker.Clear();
        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Single(messages);
        Assert.Equal(FamiliarMessageAuthor.Human, messages[0].Author);
    }

    // ---------------------------------------------------------------- history bounds

    /// <summary>History is capped, and System notes are never fed back.</summary>
    [Fact]
    public async Task History_is_capped_and_excludes_system_notes()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var conversation = await SeedConversationAsync(dbContext, project.Id);

        // Twenty visible turns plus a System note the provider must never see.
        for (var i = 1; i <= 20; i++)
        {
            dbContext.FamiliarMessages.Add(NewMessage(
                conversation.Id,
                i,
                i % 2 == 1 ? FamiliarMessageAuthor.Human : FamiliarMessageAuthor.Familiar,
                $"turn {i}"));
        }

        dbContext.FamiliarMessages.Add(new FamiliarMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            Author = FamiliarMessageAuthor.System,
            Sequence = 21,
            Content = "The reasoning provider could not be reached.",
            CreatedUtc = DateTime.UtcNow,
            Delivery = FamiliarMessageDelivery.Failed,
            FailureCode = "provider-unavailable"
        });

        await dbContext.SaveChangesAsync();

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("Answer.");

        await NewService(dbContext, provider).SendAsync(project.Id, "the newest question");

        var sent = Assert.Single(provider.Requests);

        Assert.Equal(FamiliarConversationService.MaxHistoryTurns, sent.History.Count);
        Assert.DoesNotContain(sent.History, turn => turn.Author == FamiliarMessageAuthor.System);
        Assert.DoesNotContain(sent.History, turn => turn.Content.Contains("could not be reached", StringComparison.Ordinal));

        // Oldest first, and the most recent turns are the ones kept.
        Assert.Equal("turn 11", sent.History[0].Content);
        Assert.Equal("turn 20", sent.History[^1].Content);

        // The current message is carried separately, not as the last turn of history.
        Assert.Equal("the newest question", sent.UserMessage);
    }

    // ---------------------------------------------------------------- size bounds

    /// <summary>
    /// An over-budget snapshot is never sent. The refusal happens before the provider is reached, so
    /// this asserts the call count rather than the outcome alone.
    /// </summary>
    [Fact]
    public async Task An_over_budget_snapshot_is_never_sent()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();

        var result = await NewService(dbContext, provider, snapshots: new OverBudgetSnapshotService(project.Id))
            .SendAsync(project.Id, "a question");

        Assert.Equal(FamiliarSendStatus.Reported, result.Status);
        Assert.Equal(0, provider.CallCount);

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal("snapshot-too-large", messages[1].FailureCode);
        Assert.Contains("I did not send it", messages[1].Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole request is measured, not just the snapshot. A conversation of maximum-length turns
    /// is far larger than the snapshot budget and must be trimmed by measurement.
    /// </summary>
    [Fact]
    public async Task History_is_trimmed_by_measurement_so_the_request_fits()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);
        var conversation = await SeedConversationAsync(dbContext, project.Id);

        // Ten turns of 8 000 characters is 80 000 — twice the envelope budget on its own.
        for (var i = 1; i <= 10; i++)
        {
            dbContext.FamiliarMessages.Add(NewMessage(
                conversation.Id,
                i,
                i % 2 == 1 ? FamiliarMessageAuthor.Human : FamiliarMessageAuthor.Familiar,
                new string('x', FamiliarMessage.MaxContentLength)));
        }

        await dbContext.SaveChangesAsync();

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("Answer.");

        var result = await NewService(dbContext, provider).SendAsync(project.Id, "the newest question");

        Assert.Equal(FamiliarSendStatus.Answered, result.Status);

        var sent = Assert.Single(provider.Requests);
        Assert.True(sent.History.Count < FamiliarConversationService.MaxHistoryTurns,
            "A count-based bound is not a size bound; history must be trimmed by measurement.");

        var measured = FamiliarRequestEnvelope.Measure(
            sent.Snapshot, sent.History, sent.UserMessage, sent.BehaviorContract);
        Assert.True(measured <= FamiliarRequestEnvelope.MaxEnvelopeCharacters);

        // A bound that bit is stated, not left for the reader to notice.
        Assert.Contains(sent.Snapshot.Limitations, line => line.Contains("too large to include", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- replies and evidence

    [Fact]
    public async Task An_answer_is_stored_with_its_provider_and_model()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider("Fake", "fake-model-1");
        provider.EnqueueAnswer("Nothing is running on this project.");

        var result = await NewService(dbContext, provider).SendAsync(project.Id, "what is running?");

        Assert.Equal(FamiliarSendStatus.Answered, result.Status);

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal(FamiliarMessageAuthor.Familiar, messages[1].Author);
        Assert.Equal("Nothing is running on this project.", messages[1].Content);
        Assert.Equal("Fake", messages[1].ProviderName);
        Assert.Equal("fake-model-1", messages[1].ProviderModel);
        Assert.Equal(FamiliarMessageDelivery.Delivered, messages[1].Delivery);
        Assert.Null(messages[1].FailureCode);
    }

    /// <summary>
    /// A cited id that was in the snapshot becomes evidence with a label this application composed;
    /// an invented id is dropped without comment.
    /// </summary>
    [Fact]
    public async Task Evidence_is_kept_only_for_ids_present_in_the_snapshot()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);
        var task = await SeedTaskAsync(dbContext, project.Id, "Cloudflare tunnel");

        var invented = Guid.NewGuid();

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("It is blocked.", evidenceIds: [task.Id, invented]);

        await NewService(dbContext, provider).SendAsync(project.Id, "why?");

        var evidence = await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync();

        var kept = Assert.Single(evidence);
        Assert.Equal(task.Id, kept.ReferenceId);
        Assert.Equal(FamiliarEvidenceKind.Task, kept.Kind);

        // The label is server-composed from the persisted row, never provider prose.
        Assert.Contains("Cloudflare tunnel", kept.Label, StringComparison.Ordinal);

        Assert.DoesNotContain(evidence, row => row.ReferenceId == invented);
    }

    /// <summary>An invented citation is dropped silently — no note, no System message, no complaint.</summary>
    [Fact]
    public async Task An_invented_citation_produces_no_user_visible_complaint()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("A reply.", evidenceIds: [Guid.NewGuid(), Guid.NewGuid()]);

        await NewService(dbContext, provider).SendAsync(project.Id, "why?");

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal(2, messages.Count);
        Assert.DoesNotContain(messages, message => message.Author == FamiliarMessageAuthor.System);
        Assert.Empty(await dbContext.FamiliarEvidence.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// This slice creates no proposals. A draft in the outcome is inert: no row, no dispatch, nothing.
    /// </summary>
    [Fact]
    public async Task An_action_draft_creates_no_proposal_in_this_slice()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer(
            "I could create a task.",
            actions: [new ProposedActionDraft("CreateTask", "A task", "An outcome", null)]);

        await NewService(dbContext, provider).SendAsync(project.Id, "make me a task");

        Assert.Empty(await dbContext.FamiliarActionProposals.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.Tasks.AsNoTracking().Where(t => t.ProjectId == project.Id).ToListAsync());
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- sequencing

    [Fact]
    public async Task Messages_take_consecutive_sequences_and_reuse_one_conversation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("First answer.").EnqueueAnswer("Second answer.");

        var service = NewService(dbContext, provider);
        await service.SendAsync(project.Id, "first");
        await service.SendAsync(project.Id, "second");

        Assert.Single(await dbContext.FamiliarConversations.AsNoTracking().ToListAsync());

        var messages = await ReadMessagesAsync(dbContext, project.Id);
        Assert.Equal([1, 2, 3, 4], messages.Select(message => message.Sequence).ToArray());
        Assert.Equal("first", messages[0].Content);
        Assert.Equal("First answer.", messages[1].Content);
        Assert.Equal("second", messages[2].Content);
    }

    /// <summary>A read still writes nothing, including after a conversation exists.</summary>
    [Fact]
    public async Task Get_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("An answer.");
        var service = NewService(dbContext, provider);
        await service.SendAsync(project.Id, "a question");

        var before = await CountsAsync(dbContext);
        Assert.NotNull(await service.GetAsync(project.Id));
        Assert.NotNull(await service.GetAsync(project.Id));

        Assert.Equal(before, await CountsAsync(dbContext));
    }

    [Fact]
    public async Task Get_returns_null_for_a_project_nobody_has_spoken_to()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext);

        Assert.Null(await NewService(dbContext, new ScriptedFamiliarReasoningProvider()).GetAsync(project.Id));
    }

    /// <summary>A conversation is reached through its project; another project's is never returned.</summary>
    [Fact]
    public async Task Another_projects_conversation_is_never_returned()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();
        var mine = await SeedProjectAsync(dbContext);
        var theirs = await SeedProjectAsync(dbContext);

        var provider = new ScriptedFamiliarReasoningProvider();
        provider.EnqueueAnswer("Theirs.");
        await NewService(dbContext, provider).SendAsync(theirs.Id, "their question");

        Assert.Null(await NewService(dbContext, provider).GetAsync(mine.Id));

        var view = await NewService(dbContext, provider).GetAsync(theirs.Id);
        Assert.NotNull(view);
        Assert.Equal(theirs.Id, view.ProjectId);
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarConversationService NewService(
        FamiliarDbContext dbContext,
        IFamiliarReasoningProvider provider,
        int timeoutSeconds = 60,
        IProjectSnapshotService? snapshots = null) =>
        new(
            dbContext,
            snapshots ?? new ProjectSnapshotService(
                dbContext,
                new DemiplaneProjectionService(
                    dbContext,
                    new ProviderCapacityService(
                        [new UnknownProviderCapacityReader("Claude", TimeProvider.System, "No usage surface is exposed.")],
                        TimeProvider.System,
                        NullLogger<ProviderCapacityService>.Instance),
                    TimeProvider.System),
                TimeProvider.System),
            provider,
            Options.Create(new FamiliarReasoningOptions { TimeoutSeconds = timeoutSeconds }),
            TimeProvider.System);

    private static async Task<List<FamiliarMessage>> ReadMessagesAsync(FamiliarDbContext dbContext, Guid projectId)
    {
        dbContext.ChangeTracker.Clear();

        return await dbContext.FamiliarMessages
            .AsNoTracking()
            .Where(message => message.Conversation.ProjectId == projectId)
            .OrderBy(message => message.Sequence)
            .ToListAsync();
    }

    private static async Task<string> CountsAsync(FamiliarDbContext dbContext) =>
        $"conversations={await dbContext.FamiliarConversations.AsNoTracking().CountAsync()};"
        + $"messages={await dbContext.FamiliarMessages.AsNoTracking().CountAsync()};"
        + $"evidence={await dbContext.FamiliarEvidence.AsNoTracking().CountAsync()};"
        + $"proposals={await dbContext.FamiliarActionProposals.AsNoTracking().CountAsync()}";

    private static FamiliarMessage NewMessage(
        Guid conversationId,
        int sequence,
        FamiliarMessageAuthor author,
        string content) => new()
    {
        Id = Guid.NewGuid(),
        ConversationId = conversationId,
        Author = author,
        Sequence = sequence,
        Content = content,
        CreatedUtc = DateTime.UtcNow,
        ProviderName = author == FamiliarMessageAuthor.Familiar ? "Fake" : null,
        ProviderModel = author == FamiliarMessageAuthor.Familiar ? "fake-model-1" : null,
        Delivery = FamiliarMessageDelivery.Delivered
    };

    private static async Task<FamiliarConversation> SeedConversationAsync(FamiliarDbContext dbContext, Guid projectId)
    {
        var conversation = new FamiliarConversation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.FamiliarConversations.Add(conversation);
        await dbContext.SaveChangesAsync();
        return conversation;
    }

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Conversation project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarConversationServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }

    private static async Task<FamiliarTask> SeedTaskAsync(FamiliarDbContext dbContext, Guid projectId, string title)
    {
        var task = new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            RequestedOutcome = "Seeded for FamiliarConversationServiceTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Tasks.Add(task);
        await dbContext.SaveChangesAsync();
        return task;
    }

    /// <summary>
    /// Returns a snapshot that is over budget after every documented reduction, so the refusal path
    /// can be exercised without building a project of thousands of rows.
    /// </summary>
    private sealed class OverBudgetSnapshotService(Guid projectId) : IProjectSnapshotService
    {
        public Task<ProjectSnapshotResult> GetSnapshotAsync(
            Guid requestedProjectId,
            CancellationToken cancellationToken = default)
        {
            if (requestedProjectId != projectId)
            {
                return Task.FromResult(ProjectSnapshotResult.ProjectNotFound());
            }

            var snapshot = new ProjectSnapshot(
                projectId,
                "Enormous project",
                "Purpose.",
                false,
                ProjectStatus.Active,
                1,
                [], [], [], [],
                new SnapshotHealth(0, [], 0, false),
                [],
                new SnapshotWorkforce(0, [], 0, 0, 0),
                ["This project is larger than the budget."],
                ProjectSnapshot.MaxSnapshotCharacters + 1,
                false,
                DateTimeOffset.UnixEpoch);

            return Task.FromResult(ProjectSnapshotResult.TooLarge(snapshot));
        }
    }
}
