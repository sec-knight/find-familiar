using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Joins the integration collection so the real host — and with it the SQLitePCL provider
/// selection in Program.cs — is initialized before any file-backed database is opened.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ConversationIntakeServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Blank_request_is_rejected_and_persists_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.CreateAsync(new ConversationIntakeRequest("   \n\t  "));

        Assert.Equal(ConversationIntakeStatus.ValidationFailed, outcome.Status);
        Assert.True(outcome.ValidationErrors!.ContainsKey(ConversationIntakeService.RequestField));
        Assert.Equal(0, await dbContext.Conversations.CountAsync());
        Assert.Equal(0, await dbContext.WorkProposals.CountAsync());
        Assert.Equal(0, await dbContext.ConversationMessages.CountAsync());
    }

    [Fact]
    public async Task Oversized_request_is_rejected_and_persists_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.CreateAsync(new ConversationIntakeRequest(new string('a', 4_001)));

        Assert.Equal(ConversationIntakeStatus.ValidationFailed, outcome.Status);
        Assert.Equal(0, await dbContext.Conversations.CountAsync());
    }

    [Fact]
    public async Task A_request_of_exactly_the_maximum_length_is_accepted()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        await SeedProjectAsync(dbContext, "Find Familiar");
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.CreateAsync(new ConversationIntakeRequest(new string('a', 4_000)));

        Assert.Equal(ConversationIntakeStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task Intake_creates_one_conversation_one_proposal_and_two_messages()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext, "Find Familiar");
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        var outcome = await service.CreateAsync(new ConversationIntakeRequest(
            "  Review the Find Familiar intake slice\nThen list the smallest follow-up work.  "));

        Assert.Equal(ConversationIntakeStatus.Success, outcome.Status);

        var conversation = await dbContext.Conversations.AsNoTracking().SingleAsync();
        Assert.Equal(ConversationStatus.AwaitingApproval, conversation.Status);
        Assert.Null(conversation.ApprovedTaskId);
        Assert.Null(conversation.ApprovedSessionId);

        var proposal = await dbContext.WorkProposals.AsNoTracking().SingleAsync();
        Assert.Equal(conversation.Id, proposal.ConversationId);
        Assert.Equal(project.Id, proposal.ProjectId);
        Assert.Equal("Review the Find Familiar intake slice", proposal.Title);
        Assert.Equal(
            "Review the Find Familiar intake slice\nThen list the smallest follow-up work.",
            proposal.RequestedOutcome);
        Assert.Equal(AgentSessionRole.Planner, proposal.Role);
        Assert.Equal(WorkProposalStatus.Pending, proposal.Status);
        Assert.Equal(1, proposal.Revision);
        Assert.NotEqual(Guid.Empty, proposal.ConcurrencyToken);
        Assert.Equal(project.ContextRevision, proposal.ObservedContextRevision);
        Assert.Null(proposal.CreatedTaskId);
        Assert.Null(proposal.CreatedSessionId);

        var messages = await dbContext.ConversationMessages
            .AsNoTracking()
            .OrderBy(message => message.Sequence)
            .ToListAsync();

        Assert.Equal(2, messages.Count);
        Assert.Equal(ConversationMessageAuthor.Human, messages[0].Author);
        Assert.Equal(1, messages[0].Sequence);
        Assert.StartsWith("Review the Find Familiar intake slice", messages[0].Content, StringComparison.Ordinal);
        Assert.Equal(ConversationMessageAuthor.Familiar, messages[1].Author);
        Assert.Equal(2, messages[1].Sequence);
        Assert.Contains(ProposalMessageComposer.NothingStartedNotice, messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("Planner", messages[1].Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Sprint 08 safety boundary, asserted directly against the database: intake writes only
    /// conversation tables. Not one task, session or context entry appears, and the project's
    /// context revision does not move.
    /// </summary>
    [Fact]
    public async Task Intake_creates_no_task_session_or_context_and_does_not_move_the_context_revision()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var project = await SeedProjectAsync(dbContext, "Find Familiar");
        var revisionBefore = project.ContextRevision;

        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));
        await service.CreateAsync(new ConversationIntakeRequest("Plan the Find Familiar follow-up work."));

        Assert.Equal(0, await dbContext.Tasks.CountAsync());
        Assert.Equal(0, await dbContext.AgentSessions.CountAsync());
        Assert.Equal(0, await dbContext.ContextEntries.CountAsync());
        Assert.Equal(0, await dbContext.Workers.CountAsync());

        var revisionAfter = await dbContext.Projects
            .AsNoTracking()
            .Where(candidate => candidate.Id == project.Id)
            .Select(candidate => candidate.ContextRevision)
            .SingleAsync();

        Assert.Equal(revisionBefore, revisionAfter);
    }

    [Fact]
    public async Task An_archived_project_is_never_proposed()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        await SeedProjectAsync(dbContext, "Retired Project", ProjectStatus.Archived);
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        await service.CreateAsync(new ConversationIntakeRequest("Please work on Retired Project."));

        var proposal = await dbContext.WorkProposals.AsNoTracking().SingleAsync();
        Assert.Null(proposal.ProjectId);
        Assert.Null(proposal.ObservedContextRevision);
    }

    [Fact]
    public async Task An_archived_project_does_not_count_as_the_only_active_project()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        var active = await SeedProjectAsync(dbContext, "Active Project");
        await SeedProjectAsync(dbContext, "Retired Project", ProjectStatus.Archived);
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        // Two rows exist, but only one is a candidate, so the single-active-project rule applies.
        await service.CreateAsync(new ConversationIntakeRequest("Something with no project name in it."));

        var proposal = await dbContext.WorkProposals.AsNoTracking().SingleAsync();
        Assert.Equal(active.Id, proposal.ProjectId);
    }

    [Fact]
    public async Task An_ambiguous_request_leaves_the_project_unresolved_and_says_so()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();
        await SeedProjectAsync(dbContext, "Find Familiar");
        await SeedProjectAsync(dbContext, "Ledger Sync");
        var service = new ConversationIntakeService(dbContext, new TestTimeProvider(FixedNow));

        await service.CreateAsync(new ConversationIntakeRequest("Reconcile Find Familiar with Ledger Sync."));

        var proposal = await dbContext.WorkProposals.AsNoTracking().SingleAsync();
        Assert.Null(proposal.ProjectId);
        Assert.Null(proposal.ObservedContextRevision);

        var familiarMessage = await dbContext.ConversationMessages
            .AsNoTracking()
            .Where(message => message.Author == ConversationMessageAuthor.Familiar)
            .Select(message => message.Content)
            .SingleAsync();

        Assert.Contains("choose a project before approving", familiarMessage, StringComparison.Ordinal);
        Assert.Contains(ProposalMessageComposer.NothingStartedNotice, familiarMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidate_projects_are_bounded_and_deterministically_ordered()
    {
        using var database = new TemporarySqliteDatabase();
        var dbContext = await database.CreateContextAsync();

        for (var index = 0; index < DeterministicProposalGenerator.MaxCandidateProjects + 25; index++)
        {
            await SeedProjectAsync(dbContext, $"Project {index:D4}");
        }

        var candidates = await ConversationIntakeService.LoadActiveProjectCandidatesAsync(
            dbContext,
            CancellationToken.None);

        Assert.Equal(DeterministicProposalGenerator.MaxCandidateProjects, candidates.Count);
        Assert.Equal("Project 0000", candidates[0].Name);
        Assert.Equal(
            candidates.Select(candidate => candidate.Name).OrderBy(name => name, StringComparer.Ordinal).ToList(),
            candidates.Select(candidate => candidate.Name).ToList());
    }

    internal static async Task<FamiliarProject> SeedProjectAsync(
        FamiliarDbContext dbContext,
        string name,
        ProjectStatus status = ProjectStatus.Active)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = $"Seeded for {nameof(ConversationIntakeServiceTests)}.",
            Status = status,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        return project;
    }
}
