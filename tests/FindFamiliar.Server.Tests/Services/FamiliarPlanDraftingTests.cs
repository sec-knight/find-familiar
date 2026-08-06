using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
using FindFamiliar.Server.Services.Familiar.Chat.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Drafting a plan, and everything it must not do.
///
/// The rule this file exists to hold: <b>drafting writes a proposal and nothing else.</b> No task, no
/// session, no context entry, no revision change. A Pending plan is a record of what a person will be
/// shown, and ADR-0014 makes that the only thing the talk lane may produce.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarPlanDraftingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_drafted_plan_is_persisted_with_its_items_in_order()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        await NewService(dbContext, Provider(
            """
            {"summary": "Two things.",
             "items": [
               {"title": "First", "requestedOutcome": "a", "role": "Planner"},
               {"title": "Second", "requestedOutcome": "b"}
             ]}
            """))
            .DraftAsync(Request(chatId, turnId, project.Id));

        var plan = await dbContext.FamiliarPlanProposals
            .AsNoTracking()
            .Include(candidate => candidate.Items)
            .SingleAsync();

        Assert.Equal(FamiliarPlanStatus.Pending, plan.Status);
        Assert.Equal(project.Id, plan.ProjectId);
        Assert.Equal(turnId, plan.TurnId);
        Assert.Equal("Two things.", plan.Summary);
        Assert.NotEqual(Guid.Empty, plan.ConcurrencyToken);
        Assert.Equal(project.ContextRevision, plan.ObservedContextRevision);
        Assert.Null(plan.DecidedUtc);

        var items = plan.Items.OrderBy(item => item.Position).ToList();
        Assert.Equal(["First", "Second"], items.Select(item => item.Title));
        Assert.Equal(AgentSessionRole.Planner, items[0].Role);
        Assert.Null(items[1].Role);
        Assert.All(items, item => Assert.True(item.IsIncluded));
        Assert.All(items, item => Assert.Null(item.CreatedTaskId));
    }

    /// <summary>
    /// The rule the whole slice rests on. Drafting proposes; it does not create.
    /// </summary>
    [Fact]
    public async Task Drafting_creates_no_task_no_session_and_no_context_entry()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        var revisionBefore = project.ContextRevision;

        await NewService(dbContext, Provider(
            """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o", "role": "Implementer"}]}"""))
            .DraftAsync(Request(chatId, turnId, project.Id));

        Assert.Empty(await dbContext.Tasks.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.AgentSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await dbContext.ContextEntries.AsNoTracking().ToListAsync());

        var after = await dbContext.Projects.AsNoTracking().SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(revisionBefore, after.ContextRevision);
    }

    /// <summary>
    /// At most one undecided plan per conversation, held by
    /// <c>IX_FamiliarPlanProposals_ChatId_Pending</c>. The second draft is refused by the database, not
    /// by a check somebody might forget to run, and the refusal must not fault the turn that caused it.
    /// </summary>
    [Fact]
    public async Task A_second_plan_cannot_be_drafted_while_one_is_undecided()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, firstTurn) = await SeedTurnAsync(dbContext);

        const string json = """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}""";

        await NewService(dbContext, Provider(json)).DraftAsync(Request(chatId, firstTurn, project.Id));

        var secondTurn = await AddTurnAsync(dbContext, chatId, sequence: 2);

        // Refused, and does not throw: a plan that cannot be drafted must not take down the reply that
        // asked for it, which is already durable and already read.
        await NewService(dbContext, Provider(json)).DraftAsync(Request(chatId, secondTurn, project.Id));

        Assert.Single(await dbContext.FamiliarPlanProposals.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A decided plan releases the slot. Otherwise one approved plan would end planning in that
    /// conversation forever.
    /// </summary>
    [Fact]
    public async Task A_decided_plan_frees_the_conversation_for_another()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, firstTurn) = await SeedTurnAsync(dbContext);

        const string json = """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}""";

        await NewService(dbContext, Provider(json)).DraftAsync(Request(chatId, firstTurn, project.Id));

        var first = await dbContext.FamiliarPlanProposals.SingleAsync();
        first.Status = FamiliarPlanStatus.Declined;
        first.DecidedUtc = Now.UtcDateTime;
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        var secondTurn = await AddTurnAsync(dbContext, chatId, sequence: 2);
        await NewService(dbContext, Provider(json)).DraftAsync(Request(chatId, secondTurn, project.Id));

        Assert.Equal(2, await dbContext.FamiliarPlanProposals.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// A provider that fails while drafting must leave no plan and must not disturb the conversational
    /// reply, which is already durable and already on the person's screen.
    /// </summary>
    [Theory]
    [InlineData(FamiliarChatProviderStatus.TimedOut)]
    [InlineData(FamiliarChatProviderStatus.Unavailable)]
    [InlineData(FamiliarChatProviderStatus.RateLimited)]
    public async Task A_failed_drafting_call_leaves_no_plan(FamiliarChatProviderStatus status)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        var provider = new ScriptedProvider();
        provider.Emit("""{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}""");
        provider.Finish(status);

        await NewService(dbContext, provider).DraftAsync(Request(chatId, turnId, project.Id));

        Assert.Empty(await dbContext.FamiliarPlanProposals.AsNoTracking().ToListAsync());

        var turn = await dbContext.FamiliarChatTurns.AsNoTracking().SingleAsync(candidate => candidate.Id == turnId);
        Assert.Equal("The conversational reply.", turn.Output);
        Assert.Equal(FamiliarChatTurnState.Completed, turn.State);
    }

    /// <summary>
    /// With no focus and several projects, "which project?" has no answer, and picking one would be
    /// inventing intent. No plan is drafted.
    /// </summary>
    [Fact]
    public async Task No_plan_is_drafted_when_the_project_is_ambiguous()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "One");
        await SeedProjectAsync(dbContext, "Two");
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        await NewService(dbContext, Provider(
            """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}"""))
            .DraftAsync(Request(chatId, turnId, focusProjectId: null));

        Assert.Empty(await dbContext.FamiliarPlanProposals.AsNoTracking().ToListAsync());
    }

    /// <summary>With exactly one project there is one answer, and using it is not a guess.</summary>
    [Fact]
    public async Task The_only_project_is_used_when_none_is_focused()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        await NewService(dbContext, Provider(
            """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}"""))
            .DraftAsync(Request(chatId, turnId, focusProjectId: null));

        Assert.Equal(project.Id, (await dbContext.FamiliarPlanProposals.AsNoTracking().SingleAsync()).ProjectId);
    }

    /// <summary>A sensitive project is not somewhere a plan may be drafted into.</summary>
    [Fact]
    public async Task No_plan_is_drafted_into_a_sensitive_project()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, isSensitive: true);
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        await NewService(dbContext, Provider(
            """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}"""))
            .DraftAsync(Request(chatId, turnId, project.Id));

        Assert.Empty(await dbContext.FamiliarPlanProposals.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_reply_with_no_plan_in_it_writes_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var (chatId, turnId) = await SeedTurnAsync(dbContext);

        await NewService(dbContext, Provider("I could not think of anything."))
            .DraftAsync(Request(chatId, turnId, project.Id));

        Assert.Empty(await dbContext.FamiliarPlanProposals.AsNoTracking().ToListAsync());
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarPlanDraftingService NewService(FamiliarDbContext dbContext, IFamiliarChatProvider provider) =>
        new(dbContext, provider, new TestTimeProvider(Now), NullLogger<FamiliarPlanDraftingService>.Instance);

    private static FamiliarPlanDraftRequest Request(Guid chatId, Guid turnId, Guid? focusProjectId) =>
        new(chatId, turnId, focusProjectId, "plan the next sprint", "The conversational reply.", null, null, []);

    private static ScriptedProvider Provider(string output)
    {
        var provider = new ScriptedProvider();
        provider.Emit(output);
        provider.Finish(FamiliarChatProviderStatus.Completed);
        return provider;
    }

    private static async Task<FamiliarProject> SeedProjectAsync(
        FamiliarDbContext dbContext,
        string name = "Find Familiar",
        bool isSensitive = false)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = "Seeded for FamiliarPlanDraftingTests.",
            Status = ProjectStatus.Active,
            IsSensitive = isSensitive,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        // Moved off zero so "the revision was recorded" is a real assertion rather than one a
        // default would satisfy by accident.
        project.IncrementContextRevision();

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private static async Task<(Guid ChatId, Guid TurnId)> SeedTurnAsync(FamiliarDbContext dbContext)
    {
        var chat = new FamiliarChat
        {
            Id = Guid.NewGuid(),
            Title = "Planning",
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        dbContext.FamiliarChats.Add(chat);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (chat.Id, await AddTurnAsync(dbContext, chat.Id, sequence: 1));
    }

    private static async Task<Guid> AddTurnAsync(FamiliarDbContext dbContext, Guid chatId, int sequence)
    {
        var turn = new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Sequence = sequence,
            State = FamiliarChatTurnState.Completed,
            UserText = "plan the next sprint",
            RequestedPlan = true,
            Output = "The conversational reply.",
            CreatedUtc = Now.UtcDateTime,
            CompletedUtc = Now.UtcDateTime
        };

        dbContext.FamiliarChatTurns.Add(turn);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return turn.Id;
    }

    /// <summary>A provider whose whole stream a test states outright.</summary>
    private sealed class ScriptedProvider : IFamiliarChatProvider
    {
        private readonly List<FamiliarChatStreamEvent> _events = [];

        public string Name => "scripted";

        public string Model => "scripted-model";

        public void Emit(params string[] fragments)
        {
            foreach (var fragment in fragments)
            {
                _events.Add(new FamiliarChatStreamEvent.Delta(fragment));
            }
        }

        public void Finish(FamiliarChatProviderStatus status) =>
            _events.Add(new FamiliarChatStreamEvent.Finished(status, "scripted-model"));

        public async IAsyncEnumerable<FamiliarChatStreamEvent> StreamAsync(
            FamiliarChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var streamEvent in _events)
            {
                yield return streamEvent;
            }

            await Task.CompletedTask;
        }
    }
}
