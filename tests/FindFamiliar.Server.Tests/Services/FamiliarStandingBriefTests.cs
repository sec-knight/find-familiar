using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar.Chat.Brief;
using FindFamiliar.Server.Services.Providers;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using TaskStatus = FindFamiliar.Server.Domain.TaskStatus;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The standing brief: what the Familiar is shown, and — more importantly — what it is not.
///
/// The load-bearing tests here are the sensitivity ones. This brief is the first thing in the system
/// that sends project state off the machine, so the sensitivity flag is not a feature with a happy
/// path to check; it is a boundary, and a boundary is only worth anything if crossing it fails a test.
/// Each of those tests seeds a distinctive string and asserts it appears nowhere in the serialised
/// output, because "the query filters it" is a claim about code and "the string is absent" is a claim
/// about what leaves.
///
/// The rest protect the brief's honesty about its own edges: a capped list that did not say it was
/// capped would let a model conclude a task does not exist from its absence.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarStandingBriefTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- the boundary

    [Fact]
    public async Task A_sensitive_project_appears_nowhere_in_the_brief()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "Ordinary Project", "An ordinary purpose.");
        await SeedProjectAsync(
            dbContext,
            "Chimera Acquisition",
            "Confidential purpose that must never leave this machine.",
            sensitive: true);

        var brief = await NewService(dbContext).GetBriefAsync();
        var text = FamiliarStandingBriefWriter.Write(brief);

        Assert.DoesNotContain("Chimera", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Confidential purpose", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Ordinary Project", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sensitive project's tasks go with it. The flag is on the project, and a task list that
    /// leaked through would defeat the flag entirely while leaving it looking like it worked.
    /// </summary>
    [Fact]
    public async Task A_sensitive_project_takes_its_tasks_with_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var projectId = await SeedProjectAsync(
            dbContext, "Chimera Acquisition", "Confidential.", sensitive: true);

        await SeedTaskAsync(dbContext, projectId, "Negotiate the Bellweather clause");

        var text = FamiliarStandingBriefWriter.Write(await NewService(dbContext).GetBriefAsync());

        Assert.DoesNotContain("Bellweather", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(projectId.ToString(), text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Withholding is counted and stated. A brief that dropped projects silently would let the
    /// Familiar answer "nothing is blocked" about a world it was only shown part of.
    /// </summary>
    [Fact]
    public async Task Withheld_projects_are_counted_and_declared()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "Visible", "Visible purpose.");
        await SeedProjectAsync(dbContext, "Hidden One", "Hidden.", sensitive: true);
        await SeedProjectAsync(dbContext, "Hidden Two", "Hidden.", sensitive: true);

        var brief = await NewService(dbContext).GetBriefAsync();

        Assert.Equal(2, brief.SensitiveProjectsWithheld);
        Assert.Equal(3, brief.TotalProjects);

        var text = FamiliarStandingBriefWriter.Write(brief);

        // The count is disclosed; the names are the thing being protected.
        Assert.Contains("2 project(s) are marked sensitive", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden One", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden Two", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Focus orders; it never filters. A conversation focused on one project must still be able to
    /// answer about another, because cross-project questions are the point of a system-wide Familiar.
    /// </summary>
    [Fact]
    public async Task A_focus_orders_the_brief_but_hides_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "Alpha Project", "First.");
        var focusId = await SeedProjectAsync(dbContext, "Beta Project", "Second.");

        var brief = await NewService(dbContext).GetBriefAsync(focusId);

        Assert.Equal(2, brief.Projects.Count);
        Assert.Equal(focusId, brief.Projects[0].ProjectId);

        var text = FamiliarStandingBriefWriter.Write(brief);
        Assert.Contains("Alpha Project", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- honesty about edges

    [Fact]
    public async Task An_empty_system_says_so_rather_than_rendering_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var text = FamiliarStandingBriefWriter.Write(await NewService(dbContext).GetBriefAsync());

        Assert.Contains("no projects yet", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Everything visible is withheld, which is a different fact from there being nothing. Saying
    /// "there are no projects" here would be a lie the model would then repeat.
    /// </summary>
    [Fact]
    public async Task A_system_of_only_sensitive_projects_says_nothing_is_visible()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "Hidden", "Hidden.", sensitive: true);

        var text = FamiliarStandingBriefWriter.Write(await NewService(dbContext).GetBriefAsync());

        Assert.Contains("No project is visible to you", text, StringComparison.Ordinal);
        Assert.DoesNotContain("no projects yet", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_brief_always_states_what_it_cannot_see()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "Visible", "Visible purpose.");

        var brief = await NewService(dbContext).GetBriefAsync();
        var text = FamiliarStandingBriefWriter.Write(brief);

        Assert.NotEmpty(brief.Limitations);
        Assert.Contains("<limits>", text, StringComparison.Ordinal);
        Assert.Contains("repository contents", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A capped task list says it is capped, per project. A model reading one project's block must
    /// know that block is partial without having to remember a caveat from elsewhere.
    /// </summary>
    [Fact]
    public async Task A_capped_task_list_declares_what_it_omitted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var projectId = await SeedProjectAsync(dbContext, "Busy Project", "Lots going on.");

        var total = FamiliarStandingBrief.MaxTasksPerProject + 4;

        for (var index = 0; index < total; index++)
        {
            await SeedTaskAsync(dbContext, projectId, $"Task number {index}");
        }

        var brief = await NewService(dbContext).GetBriefAsync();
        var project = Assert.Single(brief.Projects);

        // The count covers every task; only the carried list is capped. A truncated list must never
        // make a project look smaller than it is.
        Assert.Equal(total, project.TotalTasks);
        Assert.Equal(FamiliarStandingBrief.MaxTasksPerProject, project.Tasks.Count);
        Assert.Equal(4, project.TasksOmitted);

        var text = FamiliarStandingBriefWriter.Write(brief);
        Assert.Contains("4 more task(s) in this project are not listed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_brief_stays_within_its_character_bound()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        for (var project = 0; project < 4; project++)
        {
            var projectId = await SeedProjectAsync(
                dbContext,
                $"Project {project} " + new string('n', 300),
                new string('p', 2_000));

            for (var task = 0; task < 6; task++)
            {
                await SeedTaskAsync(dbContext, projectId, new string('t', 500));
            }
        }

        var text = FamiliarStandingBriefWriter.Write(await NewService(dbContext).GetBriefAsync());

        Assert.True(
            text.Length <= FamiliarStandingBrief.MaxCharacters + 200,
            $"The brief was {text.Length} characters, past its bound.");
    }

    // ---------------------------------------------------------------- prompt caching

    /// <summary>
    /// Identical state must serialise identically. The brief sits in the prompt's stable head, and a
    /// writer that reordered equal elements or stamped a clock into the text would break the
    /// provider's prefix cache on every turn and cost several times more for no benefit.
    /// </summary>
    [Fact]
    public async Task Identical_state_serialises_byte_for_byte_identically()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var projectId = await SeedProjectAsync(dbContext, "Stable Project", "Stable purpose.");
        await SeedTaskAsync(dbContext, projectId, "A task");

        var service = NewService(dbContext);

        var first = FamiliarStandingBriefWriter.Write(await service.GetBriefAsync());
        var second = FamiliarStandingBriefWriter.Write(await service.GetBriefAsync());

        Assert.Equal(first, second);
    }

    /// <summary>The observation time is deliberately not written, for the reason above.</summary>
    [Fact]
    public async Task The_observation_time_is_not_serialised()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext, "Stable Project", "Stable purpose.");

        var brief = await NewService(dbContext).GetBriefAsync();
        var text = FamiliarStandingBriefWriter.Write(brief);

        Assert.DoesNotContain(brief.ObservedAt.Year.ToString(), text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- helpers

    private static FamiliarStandingBriefService NewService(FamiliarDbContext dbContext)
    {
        var clock = new TestTimeProvider(Now);

        // The real Demiplane projection, not a stand-in: the brief's whole design is that it does not
        // classify task state itself, and a faked projection would let this suite pass while the
        // Familiar and the Demiplane disagreed about the same task.
        return new FamiliarStandingBriefService(
            dbContext,
            new DemiplaneProjectionService(dbContext, new NoProviderCapacityService(), clock),
            clock);
    }

    /// <summary>Provider readiness is not part of the brief, so it contributes nothing here.</summary>
    private sealed class NoProviderCapacityService : IProviderCapacityService
    {
        public Task<IReadOnlyList<ProviderCapacitySnapshot>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderCapacitySnapshot>>([]);
    }

    private static async Task<Guid> SeedProjectAsync(
        FamiliarDbContext dbContext,
        string name,
        string purpose,
        bool sensitive = false)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = purpose,
            Status = ProjectStatus.Active,
            IsSensitive = sensitive,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project.Id;
    }

    private static async Task SeedTaskAsync(FamiliarDbContext dbContext, Guid projectId, string title)
    {
        dbContext.Tasks.Add(new FamiliarTask
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Title = title,
            RequestedOutcome = "Seeded for FamiliarStandingBriefTests.",
            Status = TaskStatus.Ready,
            CreatedUtc = Now.UtcDateTime,
            UpdatedUtc = Now.UtcDateTime
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }
}
