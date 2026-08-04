using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

[Collection(IntegrationTestCollection.Name)]
public sealed class WorkProposalServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Revision_updates_the_proposal_rotates_the_token_and_appends_history()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var other = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Ledger Sync");
        var (conversationId, before) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            before.ConcurrencyToken,
            other.Id,
            "A revised task title",
            "A revised requested outcome."));

        Assert.Equal(ProposalActionStatus.Success, outcome.Status);

        var after = await ReadProposalAsync(dbContext, conversationId);
        Assert.Equal(other.Id, after.ProjectId);
        Assert.Equal("A revised task title", after.Title);
        Assert.Equal("A revised requested outcome.", after.RequestedOutcome);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.NotEqual(before.ConcurrencyToken, after.ConcurrencyToken);
        Assert.Equal(other.ContextRevision, after.ObservedContextRevision);
        Assert.Equal(WorkProposalStatus.Pending, after.Status);
        Assert.Equal(AgentSessionRole.Planner, after.Role);

        // History is appended, never rewritten.
        var messages = await ReadMessagesAsync(dbContext, conversationId);
        Assert.Equal(3, messages.Count);
        Assert.Equal([1, 2, 3], messages.Select(message => message.Sequence).ToArray());
        Assert.Contains("Proposal revised (revision 2)", messages[2].Content, StringComparison.Ordinal);
        Assert.Contains("A revised task title", messages[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revision_creates_no_task_session_or_context_entry()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var revisionBefore = project.ContextRevision;
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));
        await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            proposal.ConcurrencyToken,
            project.Id,
            "Still nothing runs",
            "Revising must not dispatch anything."));

        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync());
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(dbContext, project.Id));
    }

    [Fact]
    public async Task A_stale_token_is_rejected_without_overwriting_newer_data()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, original) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            original.ConcurrencyToken,
            project.Id,
            "The newer title",
            "The newer outcome."));

        // A second form, rendered before the revision above, still carries the original token.
        var stale = await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            original.ConcurrencyToken,
            project.Id,
            "The stale title",
            "The stale outcome."));

        Assert.Equal(ProposalActionStatus.StaleProposal, stale.Status);

        var current = await ReadProposalAsync(dbContext, conversationId);
        Assert.Equal("The newer title", current.Title);
        Assert.Equal("The newer outcome.", current.RequestedOutcome);
        Assert.Equal(2, current.Revision);
    }

    [Fact]
    public async Task Revision_requires_an_active_project()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var archived = await ConversationIntakeServiceTests.SeedProjectAsync(
            dbContext,
            "Retired Project",
            ProjectStatus.Archived);
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            proposal.ConcurrencyToken,
            archived.Id,
            "Valid title",
            "Valid outcome."));

        Assert.Equal(ProposalActionStatus.ValidationFailed, outcome.Status);
        Assert.True(outcome.ValidationErrors!.ContainsKey(WorkProposalService.ProjectField));
        Assert.Equal(project.Id, (await ReadProposalAsync(dbContext, conversationId)).ProjectId);
    }

    [Theory]
    [InlineData("", "Valid outcome.", WorkProposalService.TitleField)]
    [InlineData("   ", "Valid outcome.", WorkProposalService.TitleField)]
    [InlineData("Valid title", "", WorkProposalService.RequestedOutcomeField)]
    public async Task Revision_validates_field_bounds(string title, string outcome, string expectedField)
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var result = await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            proposal.ConcurrencyToken,
            project.Id,
            title,
            outcome));

        Assert.Equal(ProposalActionStatus.ValidationFailed, result.Status);
        Assert.True(result.ValidationErrors!.ContainsKey(expectedField));
    }

    [Fact]
    public async Task An_over_long_title_is_rejected()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var result = await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            proposal.ConcurrencyToken,
            project.Id,
            new string('a', WorkProposal.MaxTitleLength + 1),
            "Valid outcome."));

        Assert.Equal(ProposalActionStatus.ValidationFailed, result.Status);
        Assert.True(result.ValidationErrors!.ContainsKey(WorkProposalService.TitleField));
    }

    [Fact]
    public async Task Refreshing_context_records_the_current_revision_and_invalidates_the_reviewed_token()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        // Something else advanced the project's context after the proposal was reviewed.
        var tracked = await dbContext.Projects.SingleAsync(candidate => candidate.Id == project.Id);
        tracked.IncrementContextRevision();
        tracked.IncrementContextRevision();
        await dbContext.SaveChangesAsync();
        var currentRevision = await CurrentRevisionAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));
        var outcome = await service.RefreshContextAsync(
            new ProposalActionRequest(conversationId, proposal.ConcurrencyToken));

        Assert.Equal(ProposalActionStatus.Success, outcome.Status);

        var after = await ReadProposalAsync(dbContext, conversationId);
        Assert.Equal(currentRevision, after.ObservedContextRevision);
        Assert.NotEqual(proposal.ConcurrencyToken, after.ConcurrencyToken);
        Assert.Equal(WorkProposalStatus.Pending, after.Status);

        // Refresh does not dispatch anything, and it does not move the project's revision either.
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
        Assert.Equal(currentRevision, await CurrentRevisionAsync(dbContext, project.Id));

        var messages = await ReadMessagesAsync(dbContext, conversationId);
        Assert.Contains("Review it again before approving", messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejection_is_terminal_and_creates_no_work()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var revisionBefore = project.ContextRevision;
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.RejectAsync(new ProposalActionRequest(conversationId, proposal.ConcurrencyToken));
        Assert.Equal(ProposalActionStatus.Success, outcome.Status);

        var conversation = await dbContext.Conversations
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == conversationId);
        var after = await ReadProposalAsync(dbContext, conversationId);

        Assert.Equal(ConversationStatus.Rejected, conversation.Status);
        Assert.Equal(WorkProposalStatus.Rejected, after.Status);
        Assert.Null(conversation.ApprovedTaskId);
        Assert.Null(conversation.ApprovedSessionId);
        Assert.Null(after.CreatedTaskId);
        Assert.Null(after.CreatedSessionId);
        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
        Assert.Equal(revisionBefore, await CurrentRevisionAsync(dbContext, project.Id));

        var messages = await ReadMessagesAsync(dbContext, conversationId);
        Assert.Contains("No task and no session were created", messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_proposal_cannot_be_revised_or_rejected_again()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await ConversationIntakeServiceTests.SeedProjectAsync(dbContext, "Find Familiar");
        var (conversationId, proposal) = await SeedConversationAsync(dbContext, project.Id);

        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));
        await service.RejectAsync(new ProposalActionRequest(conversationId, proposal.ConcurrencyToken));

        var currentToken = (await ReadProposalAsync(dbContext, conversationId)).ConcurrencyToken;

        var revise = await service.ReviseAsync(new ProposalRevisionRequest(
            conversationId,
            currentToken,
            project.Id,
            "Trying to revive it",
            "This must not be applied."));
        var rejectAgain = await service.RejectAsync(new ProposalActionRequest(conversationId, currentToken));

        Assert.Equal(ProposalActionStatus.AlreadyTerminal, revise.Status);
        Assert.Equal(ProposalActionStatus.AlreadyTerminal, rejectAgain.Status);

        var after = await ReadProposalAsync(dbContext, conversationId);
        Assert.Equal(WorkProposalStatus.Rejected, after.Status);
        Assert.NotEqual("Trying to revive it", after.Title);
    }

    [Fact]
    public async Task An_unknown_conversation_is_reported_as_not_found()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var service = new WorkProposalService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.RejectAsync(new ProposalActionRequest(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(ProposalActionStatus.NotFound, outcome.Status);
    }

    internal static async Task<(Guid ConversationId, WorkProposal Proposal)> SeedConversationAsync(
        FamiliarDbContext dbContext,
        Guid? projectId,
        string title = "Seeded proposal title",
        string requestedOutcome = "Seeded requested outcome.")
    {
        var observedRevision = projectId is { } id
            ? await dbContext.Projects
                .AsNoTracking()
                .Where(project => project.Id == id)
                .Select(project => (int?)project.ContextRevision)
                .SingleAsync()
            : null;

        var nowUtc = DateTime.UtcNow;

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Status = ConversationStatus.AwaitingApproval,
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        var proposal = new WorkProposal
        {
            Id = Guid.NewGuid(),
            ConversationId = conversation.Id,
            ProjectId = projectId,
            Title = title,
            RequestedOutcome = requestedOutcome,
            Role = AgentSessionRole.Planner,
            ObservedContextRevision = observedRevision,
            Status = WorkProposalStatus.Pending,
            Revision = 1,
            ConcurrencyToken = Guid.NewGuid(),
            CreatedUtc = nowUtc,
            UpdatedUtc = nowUtc
        };

        dbContext.Conversations.Add(conversation);
        dbContext.WorkProposals.Add(proposal);
        dbContext.ConversationMessages.AddRange(
            new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Author = ConversationMessageAuthor.Human,
                Sequence = 1,
                Content = requestedOutcome,
                CreatedUtc = nowUtc
            },
            new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Author = ConversationMessageAuthor.Familiar,
                Sequence = 2,
                Content = "Seeded Familiar response.",
                CreatedUtc = nowUtc
            });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return (conversation.Id, proposal);
    }

    internal static Task<WorkProposal> ReadProposalAsync(FamiliarDbContext dbContext, Guid conversationId) =>
        dbContext.WorkProposals
            .AsNoTracking()
            .SingleAsync(proposal => proposal.ConversationId == conversationId);

    internal static Task<List<ConversationMessage>> ReadMessagesAsync(
        FamiliarDbContext dbContext,
        Guid conversationId) =>
        dbContext.ConversationMessages
            .AsNoTracking()
            .Where(message => message.ConversationId == conversationId)
            .OrderBy(message => message.Sequence)
            .ToListAsync();

    internal static Task<int> CurrentRevisionAsync(FamiliarDbContext dbContext, Guid projectId) =>
        dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId)
            .Select(project => project.ContextRevision)
            .SingleAsync();
}
