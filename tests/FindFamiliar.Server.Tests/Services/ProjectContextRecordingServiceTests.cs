using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The supported way to record durable context, tested against the failure that made it necessary.
///
/// During Sprint 14/15 work an agent wrote context by opening the SQLite file directly. A project id
/// in the wrong case matched nothing, foreign keys were not enforcing, and three rows inserted
/// belonging to no project while the context revision never moved. Every assertion here is one of
/// those failures made impossible: identifier handling, orphan prevention, and the entry and the
/// revision moving together or not at all.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class ProjectContextRecordingServiceTests(FindFamiliarWebApplicationFactory factory)
{
    // ---------------------------------------------------------------- the happy path

    [Fact]
    public async Task Valid_context_is_recorded_with_its_provenance()
    {
        var project = await SeedProjectAsync();

        var outcome = await RecordAsync(new RecordProjectContextRequest(
            project.Id,
            ContextEntryKind.Decision,
            "  A recorded decision  ",
            "  The body of the decision.  ",
            ContextProvenance.RepositoryVerified,
            RecordedBy: "  claude-session  "));

        Assert.Equal(RecordProjectContextStatus.Recorded, outcome.Status);
        Assert.NotNull(outcome.ContextEntryId);

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();
        var entry = await dbContext.ContextEntries.SingleAsync(candidate => candidate.Id == outcome.ContextEntryId);

        Assert.Equal(project.Id, entry.ProjectId);
        Assert.Equal(ContextEntryKind.Decision, entry.Kind);
        Assert.Equal(ContextEntryState.Active, entry.State);

        // Provenance and reporter are retained, and both are trimmed rather than stored as typed.
        Assert.Equal(ContextProvenance.RepositoryVerified, entry.Provenance);
        Assert.Equal("claude-session", entry.RecordedBy);
        Assert.Equal("A recorded decision", entry.Title);
        Assert.Equal("The body of the decision.", entry.Content);
    }

    /// <summary>
    /// The bug, directly. An external caller writes a Guid however its language renders one — lower
    /// case, upper case, braces — and the database stores them uppercase. Lookup goes through EF
    /// against the typed column, so the textual form cannot decide whether a project is found.
    /// </summary>
    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    [InlineData("B")]
    public async Task Project_lookup_is_robust_to_external_identifier_representation(string format)
    {
        var project = await SeedProjectAsync();

        foreach (var rendered in new[] { project.Id.ToString(format).ToLowerInvariant(), project.Id.ToString(format).ToUpperInvariant() })
        {
            var outcome = await RecordAsync(new RecordProjectContextRequest(
                Guid.Parse(rendered),
                ContextEntryKind.Summary,
                $"Casing probe {rendered}",
                "Body.",
                ContextProvenance.ExternalReported));

            Assert.Equal(RecordProjectContextStatus.Recorded, outcome.Status);
        }
    }

    // ---------------------------------------------------------------- revision and atomicity

    [Fact]
    public async Task The_context_revision_increments_exactly_once()
    {
        var project = await SeedProjectAsync();
        var before = await RevisionAsync(project.Id);

        var outcome = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Implementation, "One bump", "Body.", ContextProvenance.RepositoryVerified));

        Assert.Equal(RecordProjectContextStatus.Recorded, outcome.Status);
        Assert.Equal(before + 1, await RevisionAsync(project.Id));
        Assert.Equal(before + 1, outcome.ContextRevision);
    }

    /// <summary>
    /// Entry and revision move together. The orphan incident was exactly this pair coming apart: rows
    /// existed and the revision announcing them did not.
    /// </summary>
    [Fact]
    public async Task The_entry_and_the_revision_move_together()
    {
        var project = await SeedProjectAsync();
        var revisionBefore = await RevisionAsync(project.Id);
        var countBefore = await EntryCountAsync(project.Id);

        await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Review, "Atomic", "Body.", ContextProvenance.SessionReported));

        Assert.Equal(countBefore + 1, await EntryCountAsync(project.Id));
        Assert.Equal(revisionBefore + 1, await RevisionAsync(project.Id));
    }

    // ---------------------------------------------------------------- refusals leave nothing behind

    /// <summary>
    /// The orphan case. An id matching no project must write nothing at all — not a row, not a bump.
    /// Asserted on the whole table, because an orphan by definition belongs to no project and would be
    /// invisible to a project-scoped count.
    /// </summary>
    [Fact]
    public async Task An_unknown_project_is_rejected_without_creating_an_orphan()
    {
        var totalBefore = await TotalEntryCountAsync();

        var outcome = await RecordAsync(new RecordProjectContextRequest(
            Guid.NewGuid(), ContextEntryKind.Decision, "Orphan probe", "Body.", ContextProvenance.ExternalReported));

        Assert.Equal(RecordProjectContextStatus.ProjectNotFound, outcome.Status);
        Assert.Equal(totalBefore, await TotalEntryCountAsync());
        Assert.Equal(0, await OrphanCountAsync());
    }

    [Theory]
    [InlineData("", "body", "title")]
    [InlineData("   ", "body", "title")]
    [InlineData("title", "", "content")]
    [InlineData("title", "   ", "content")]
    public async Task A_missing_field_is_refused_without_partial_state(string title, string content, string _)
    {
        var project = await SeedProjectAsync();
        var revisionBefore = await RevisionAsync(project.Id);
        var totalBefore = await TotalEntryCountAsync();

        var outcome = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, title, content, ContextProvenance.RepositoryVerified));

        Assert.Equal(RecordProjectContextStatus.ValidationFailed, outcome.Status);
        Assert.NotNull(outcome.ValidationMessage);
        Assert.Equal(revisionBefore, await RevisionAsync(project.Id));
        Assert.Equal(totalBefore, await TotalEntryCountAsync());
    }

    [Fact]
    public async Task An_oversized_field_is_refused_rather_than_truncated()
    {
        var project = await SeedProjectAsync();

        var longTitle = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, new string('t', 201), "Body.", ContextProvenance.RepositoryVerified));
        Assert.Equal(RecordProjectContextStatus.ValidationFailed, longTitle.Status);

        var longBody = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, "Title", new string('c', 12_001), ContextProvenance.RepositoryVerified));
        Assert.Equal(RecordProjectContextStatus.ValidationFailed, longBody.Status);

        var longReporter = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, "Title", "Body.", ContextProvenance.RepositoryVerified,
            RecordedBy: new string('r', 101)));
        Assert.Equal(RecordProjectContextStatus.ValidationFailed, longReporter.Status);
    }

    /// <summary>
    /// Provenance must be stated. Unspecified exists for rows written before the column did, and a new
    /// entry that claimed it would be asserting the one thing this field is meant to prevent: a fact
    /// whose weight nobody can judge.
    /// </summary>
    [Fact]
    public async Task Provenance_must_be_stated_and_cannot_be_unspecified()
    {
        var project = await SeedProjectAsync();

        var outcome = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, "No provenance", "Body.", ContextProvenance.Unspecified));

        Assert.Equal(RecordProjectContextStatus.ValidationFailed, outcome.Status);
        Assert.Contains("provenance", outcome.ValidationMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_undefined_category_or_provenance_is_refused()
    {
        var project = await SeedProjectAsync();

        var badKind = await RecordAsync(new RecordProjectContextRequest(
            project.Id, (ContextEntryKind)999, "Title", "Body.", ContextProvenance.RepositoryVerified));
        Assert.Equal(RecordProjectContextStatus.ValidationFailed, badKind.Status);

        var badProvenance = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, "Title", "Body.", (ContextProvenance)999));
        Assert.Equal(RecordProjectContextStatus.ValidationFailed, badProvenance.Status);
    }

    /// <summary>The optional fence, for a caller whose entry only makes sense against the view it read.</summary>
    [Fact]
    public async Task A_stale_expected_revision_is_refused_and_a_matching_one_succeeds()
    {
        var project = await SeedProjectAsync();
        var revision = await RevisionAsync(project.Id);

        var stale = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, "Stale", "Body.", ContextProvenance.RepositoryVerified,
            ExpectedContextRevision: revision - 1));

        Assert.Equal(RecordProjectContextStatus.ContextMoved, stale.Status);
        Assert.Equal(revision, await RevisionAsync(project.Id));

        var current = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, "Current", "Body.", ContextProvenance.RepositoryVerified,
            ExpectedContextRevision: revision));

        Assert.Equal(RecordProjectContextStatus.Recorded, current.Status);
    }

    // ---------------------------------------------------------------- integrity and readback

    /// <summary>
    /// Foreign keys hold for everything the service writes. This is the check that would have caught
    /// the orphan rows at the time they were created rather than on a later inspection.
    /// </summary>
    [Fact]
    public async Task Recording_preserves_foreign_key_integrity()
    {
        var project = await SeedProjectAsync();

        await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Goal, "Integrity", "Body.", ContextProvenance.RepositoryVerified));

        Assert.Equal(0, await OrphanCountAsync());
    }

    /// <summary>
    /// A recorded entry must be reachable by the ordinary read path — a record nothing can retrieve is
    /// not durable context, it is a row.
    /// </summary>
    [Fact]
    public async Task A_recorded_entry_is_retrievable_by_the_existing_read_path()
    {
        var project = await SeedProjectAsync();
        var title = $"Retrieval probe {Guid.NewGuid():N}";

        var outcome = await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Decision, title,
            "A distinctive body about the retrieval probe.", ContextProvenance.RepositoryVerified));

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var found = await dbContext.ContextEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == project.Id && entry.State == ContextEntryState.Active)
            .SingleOrDefaultAsync(entry => entry.Id == outcome.ContextEntryId);

        Assert.NotNull(found);
        Assert.Equal(title, found!.Title);
    }

    /// <summary>
    /// The scope boundary. This service records context and does nothing else — it must not have become
    /// a way to create work, and a count over everything the human-gated paths produce is the check
    /// that keeps holding when somebody adds a parameter later.
    /// </summary>
    [Fact]
    public async Task Recording_context_creates_no_tasks_sessions_or_proposals()
    {
        var project = await SeedProjectAsync();
        var before = await WorkCountsAsync();

        await RecordAsync(new RecordProjectContextRequest(
            project.Id, ContextEntryKind.Plan, "Start a session and create a task now",
            "Create a task, start an implementer session, approve the plan.", ContextProvenance.ExternalReported));

        Assert.Equal(before, await WorkCountsAsync());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<RecordProjectContextOutcome> RecordAsync(RecordProjectContextRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IProjectContextRecordingService>();

        return await service.RecordAsync(request);
    }

    private async Task<FamiliarProject> SeedProjectAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = $"Context recording project {Guid.NewGuid():N}",
            Purpose = "Seeded for ProjectContextRecordingServiceTests.",
            Status = ProjectStatus.Active,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();

        return project;
    }

    private async Task<int> RevisionAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.Projects.Where(p => p.Id == projectId).Select(p => p.ContextRevision).SingleAsync();
    }

    private async Task<int> EntryCountAsync(Guid projectId)
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.ContextEntries.CountAsync(entry => entry.ProjectId == projectId);
    }

    private async Task<int> TotalEntryCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.ContextEntries.CountAsync();
    }

    /// <summary>Entries whose project does not exist — the exact shape of the incident.</summary>
    private async Task<int> OrphanCountAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return await dbContext.ContextEntries
            .CountAsync(entry => !dbContext.Projects.Any(project => project.Id == entry.ProjectId));
    }

    private async Task<(int Tasks, int Sessions, int Proposals)> WorkCountsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FamiliarDbContext>();

        return (
            await dbContext.Tasks.CountAsync(),
            await dbContext.AgentSessions.CountAsync(),
            await dbContext.FamiliarPlanProposals.CountAsync());
    }
}
