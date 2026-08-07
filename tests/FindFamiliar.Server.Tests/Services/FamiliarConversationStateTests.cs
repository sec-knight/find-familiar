using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;
using FindFamiliar.Server.Tests.Infrastructure;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// What the conversation tells the model about itself.
///
/// This exists because of a real failure, on the first end-to-end test anybody ran. Asked to plan a
/// change, the Familiar drafted a plan; the person said "I approved it"; the Familiar answered "The
/// plan is now approved" and cited a context entry as evidence. Nothing had been approved. The
/// citation was genuine and checkable — slice 2 validated it — but the claim was not the kind of
/// thing a citation supports, and the model had no way to know either way: a plan's existence and
/// status were simply not in its context.
///
/// The same turn produced the other half of the failure: it told the person a human with write access
/// would have to go and run a session by hand. That is false. An approved plan item naming a role
/// starts exactly that session.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarConversationStateTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The load-bearing one. A plan awaiting a decision says so, and says outright that being told it
    /// was approved is not evidence that it was.
    /// </summary>
    [Fact]
    public async Task A_pending_plan_is_reported_as_undecided()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedPlanAsync(dbContext, FamiliarPlanStatus.Pending, AgentSessionRole.Implementer);

        var text = FamiliarConversationStateWriter.Write(
            await new FamiliarConversationStateService(dbContext).ReadAsync(chatId));

        Assert.Contains("waiting for the person to press Approve or Decline", text, StringComparison.Ordinal);
        Assert.Contains("Nothing in it has been created", text, StringComparison.Ordinal);
        Assert.Contains("Do not say it has been approved", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_plan_reports_what_it_created()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedPlanAsync(
            dbContext, FamiliarPlanStatus.Approved, AgentSessionRole.Implementer, createdTasks: true);

        var text = FamiliarConversationStateWriter.Write(
            await new FamiliarConversationStateService(dbContext).ReadAsync(chatId));

        Assert.Contains("was approved", text, StringComparison.Ordinal);
        Assert.Contains("2 task(s) were created", text, StringComparison.Ordinal);
        Assert.Contains("one Implementer session was started", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_plan_that_started_nothing_says_so()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedPlanAsync(dbContext, FamiliarPlanStatus.Approved, role: null, createdTasks: true);

        var text = FamiliarConversationStateWriter.Write(
            await new FamiliarConversationStateService(dbContext).ReadAsync(chatId));

        Assert.Contains("no session was started", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_declined_plan_says_nothing_was_created()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedPlanAsync(dbContext, FamiliarPlanStatus.Declined, AgentSessionRole.Implementer);

        var text = FamiliarConversationStateWriter.Write(
            await new FamiliarConversationStateService(dbContext).ReadAsync(chatId));

        Assert.Contains("was declined", text, StringComparison.Ordinal);
        Assert.Contains("Nothing was created", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_with_no_plan_says_so()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var text = FamiliarConversationStateWriter.Write(
            await new FamiliarConversationStateService(dbContext).ReadAsync(Guid.NewGuid()));

        Assert.Contains("No plan has been drafted", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The capability statement is unconditional, because the falsehood it prevents is told before any
    /// plan exists: asked for a change, the Familiar said somebody would have to go and do it by hand.
    /// </summary>
    [Fact]
    public void What_approving_causes_is_always_stated()
    {
        foreach (var text in new[]
                 {
                     FamiliarConversationStateWriter.Write(null),
                     FamiliarConversationStateWriter.Write(
                         new FamiliarConversationPlanState(FamiliarPlanStatus.Pending, 1, 0, null))
                 })
        {
            // Flattened, because the prompt is a wrapped raw literal and the assertion is about what
            // it says rather than where its lines break.
            var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

            Assert.Contains("Nobody has to go and do it by hand", flat, StringComparison.Ordinal);
            Assert.Contains("plan it with an Implementer item", flat, StringComparison.Ordinal);
            Assert.Contains("is not evidence that they did", flat, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Which button was pressed, told outright. Drafting happens outside the reply, so a model told
    /// neither will guess — and it did: "Plan drafted. Approve it to start the session." on a turn
    /// where nothing was drafted and there was nothing to approve.
    /// </summary>
    [Fact]
    public void An_ordinary_turn_says_no_plan_is_being_drafted()
    {
        var text = FamiliarConversationStateWriter.Write(null, planRequestedThisTurn: false);

        Assert.Contains("NO plan is being drafted", text, StringComparison.Ordinal);
        Assert.Contains("Nothing you write creates one", text, StringComparison.Ordinal);
        Assert.Contains("press \"Plan this\"", text, StringComparison.Ordinal);
        Assert.Contains("Never announce a plan you have not been asked to draft", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_turn_says_a_plan_is_being_drafted()
    {
        var text = FamiliarConversationStateWriter.Write(null, planRequestedThisTurn: true);

        Assert.Contains("a plan IS being drafted", text, StringComparison.Ordinal);
        Assert.Contains("Do not say it has been approved", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NO plan is being drafted", text, StringComparison.Ordinal);
    }

    /// <summary>The newest plan is the one on screen, so it is the one described.</summary>
    [Fact]
    public async Task The_most_recent_plan_is_the_one_reported()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var chatId = await SeedPlanAsync(dbContext, FamiliarPlanStatus.Declined, AgentSessionRole.Planner);
        await SeedPlanAsync(dbContext, FamiliarPlanStatus.Pending, AgentSessionRole.Implementer, chatId: chatId);

        var state = await new FamiliarConversationStateService(dbContext).ReadAsync(chatId);

        Assert.Equal(FamiliarPlanStatus.Pending, state!.Status);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Guid> SeedPlanAsync(
        FamiliarDbContext dbContext,
        FamiliarPlanStatus status,
        AgentSessionRole? role,
        bool createdTasks = false,
        Guid? chatId = null)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"State project {Guid.NewGuid():N}",
            Purpose = "Seeded for FamiliarConversationStateTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Projects.Add(project);

        if (chatId is null)
        {
            var chat = new FamiliarChat
            {
                Id = Guid.NewGuid(),
                Title = "Planning",
                CreatedUtc = Now,
                UpdatedUtc = Now
            };

            dbContext.FamiliarChats.Add(chat);
            chatId = chat.Id;
        }

        var turn = new FamiliarChatTurn
        {
            Id = Guid.NewGuid(),
            ChatId = chatId.Value,
            Sequence = Random.Shared.Next(1, 100_000),
            State = FamiliarChatTurnState.Completed,
            UserText = "plan it",
            Output = "Here is what I would do.",
            CreatedUtc = Now
        };

        dbContext.FamiliarChatTurns.Add(turn);

        dbContext.FamiliarPlanProposals.Add(new FamiliarPlanProposal
        {
            Id = Guid.NewGuid(),
            ChatId = chatId.Value,
            TurnId = turn.Id,
            ProjectId = project.Id,
            Status = status,
            ConcurrencyToken = Guid.NewGuid(),
            ObservedContextRevision = 0,
            Summary = "A plan.",
            CreatedUtc = status == FamiliarPlanStatus.Pending ? Now.AddMinutes(5) : Now,
            UpdatedUtc = Now,
            Items =
            [
                new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = 0,
                    Title = "First",
                    RequestedOutcome = "An outcome.",
                    Role = role,
                    IsIncluded = true,
                    CreatedTaskId = createdTasks ? Guid.NewGuid() : null
                },
                new FamiliarPlanItem
                {
                    Id = Guid.NewGuid(),
                    Position = 1,
                    Title = "Second",
                    RequestedOutcome = "Another outcome.",
                    Role = null,
                    IsIncluded = true,
                    CreatedTaskId = createdTasks ? Guid.NewGuid() : null
                }
            ]
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return chatId.Value;
    }
}
