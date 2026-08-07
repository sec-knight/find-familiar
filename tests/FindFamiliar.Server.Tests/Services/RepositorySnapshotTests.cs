using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Retrieval;
using FindFamiliar.Server.Services.Familiar.Repository;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The repository, written down without anybody remembering to.
///
/// Two things are being asserted, and they fail in opposite directions. The snapshot must be
/// <i>bounded</i>, because an unbounded one silently becomes the largest thing in every prompt; and it
/// must be <i>honest about being bounded</i>, because a file list that stops at 120 of 366 paths in
/// silence is not a small truth, it is a false claim about the size of the repository.
///
/// The third thing is supersession. Exactly one snapshot row exists at any moment, enforced by
/// deleting on write rather than by a filter every future reader would have to remember.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class RepositorySnapshotTests
{
    private static readonly DateTime Now = new(2026, 8, 7, 9, 30, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- what a snapshot says

    [Fact]
    public void The_header_states_the_date_the_branch_the_head_and_the_supersession_rule()
    {
        var text = RepositorySnapshotComposer.Compose(SmallState(), Now);

        Assert.Contains("date: 2026-08-07", text, StringComparison.Ordinal);
        Assert.Contains("branch: main", text, StringComparison.Ordinal);
        Assert.Contains("head: 81d754a", text, StringComparison.Ordinal);
        Assert.Contains(RepositorySnapshotComposer.SupersedesMarker, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_small_repository_is_carried_whole_with_no_trim_notes()
    {
        var text = RepositorySnapshotComposer.Compose(SmallState(), Now);

        Assert.Contains("src/FindFamiliar.Server/Program.cs", text, StringComparison.Ordinal);
        Assert.Contains("two-level view of tracked paths (2 paths):", text, StringComparison.Ordinal);
        Assert.DoesNotContain("trimmed:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two-level view is derived from the tracked paths rather than from a filesystem walk, which
    /// is the correction that matters: <c>find . -type f</c> reports <c>bin/</c>, <c>obj/</c>,
    /// <c>node_modules</c> and the SQLite database, and calls the build output of one machine the
    /// state of the repository.
    /// </summary>
    [Fact]
    public void The_two_level_view_collapses_deep_paths_and_keeps_shallow_ones_whole()
    {
        var view = RepositoryState.TwoLevelView([
            "src/FindFamiliar.Server/Program.cs",
            "src/FindFamiliar.Server/Data/FamiliarDbContext.cs",
            "src/FindFamiliar.Runner/Program.cs",
            "README.md",
            "docs/sprint-13-plan.md"
        ]);

        Assert.Equal(
            ["README.md", "docs/sprint-13-plan.md", "src/FindFamiliar.Runner", "src/FindFamiliar.Server"],
            view);
    }

    // ---------------------------------------------------------------- the ceiling

    [Fact]
    public void A_repository_larger_than_the_ceiling_is_cut_down_to_it()
    {
        var text = RepositorySnapshotComposer.Compose(LargeState(1_200), Now);

        Assert.True(
            text.Length <= RepositorySnapshotComposer.MaxCharacters,
            $"The snapshot was {text.Length} characters, over the {RepositorySnapshotComposer.MaxCharacters} ceiling.");
    }

    /// <summary>
    /// The whole point of CORRECTION 3: a reader must be able to tell whether they are looking at the
    /// repository or at a corner of it, without counting anything.
    /// </summary>
    [Fact]
    public void A_trimmed_section_states_what_was_cut_and_by_how_much()
    {
        var text = RepositorySnapshotComposer.Compose(LargeState(1_200), Now);

        var note = text
            .Split('\n')
            .Single(line => line.StartsWith("[tracked files trimmed:", StringComparison.Ordinal));

        Assert.EndsWith("of 1,200 paths shown]", note, StringComparison.Ordinal);

        // The count in the note is the count actually rendered, not an estimate of it.
        var shown = int.Parse(note.Split(':')[1].Split(" of ")[0].Trim().Replace(",", ""));
        Assert.Equal(shown, text.Split('\n').Count(line => line.StartsWith("src/generated/file-", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Trim order. The exhaustive list goes first because it is the only section big enough to breach
    /// the ceiling alone and the one whose loss costs least — the two-level view above it already
    /// states what is in the repository. Cutting the summary to preserve the raw list would leave a
    /// reader holding a corner with nothing to tell them it was a corner.
    /// </summary>
    [Fact]
    public void The_shape_of_the_repository_survives_the_trim_that_takes_the_file_list()
    {
        var text = RepositorySnapshotComposer.Compose(LargeState(1_200), Now);

        Assert.Contains("[tracked files trimmed:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[two-level view of tracked paths trimmed:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[recent commits trimmed:", text, StringComparison.Ordinal);

        // Every commit, and the whole shape, still there.
        Assert.Equal(20, text.Split('\n').Count(line => line.StartsWith("abc", StringComparison.Ordinal)));
    }

    /// <summary>
    /// When the file list alone is not enough to get under the ceiling, the two-level view goes next
    /// and the commit log last. The log is kept longest because twenty subject lines are the cheapest
    /// statement of what has recently changed that this snapshot can make.
    /// </summary>
    [Fact]
    public void When_the_file_list_is_not_enough_the_two_level_view_goes_before_the_log()
    {
        // Ten thousand distinct two-level prefixes: the summary itself is now far over the ceiling.
        var text = RepositorySnapshotComposer.Compose(LargeState(10_000, deepPrefixes: true), Now);

        Assert.True(text.Length <= RepositorySnapshotComposer.MaxCharacters);
        Assert.Contains("[two-level view of tracked paths trimmed:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("[recent commits trimmed:", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same repository must compose to the same characters on every run and every machine, or the
    /// snapshot rewrites itself on a platform difference and every prompt built on it stops matching
    /// the provider's prefix cache.
    /// </summary>
    [Fact]
    public void The_same_repository_composes_to_the_same_characters()
    {
        Assert.Equal(
            RepositorySnapshotComposer.Compose(LargeState(1_200), Now),
            RepositorySnapshotComposer.Compose(LargeState(1_200), Now));
    }

    // ---------------------------------------------------------------- exactly one row

    [Fact]
    public async Task A_capture_writes_one_summary_entry_under_the_fixed_title()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        var outcome = await NewService(dbContext, project.Id, SmallState()).CaptureAsync();

        Assert.Equal(RepositorySnapshotStatus.Captured, outcome.Status);

        var entry = Assert.Single(await dbContext.ContextEntries.AsNoTracking().ToListAsync());
        Assert.Equal(RepositorySnapshotService.SnapshotTitle, entry.Title);
        Assert.Equal(ContextEntryKind.Summary, entry.Kind);
        Assert.Equal(ContextEntryState.Active, entry.State);
        Assert.False(entry.IsSensitive);
    }

    /// <summary>
    /// Supersession by delete-on-write. The rejected alternative — keep every snapshot and filter to
    /// the newest at retrieval time — puts a correctness requirement in every reader, including
    /// readers not yet written, and a reader that forgets it answers confidently about a repository as
    /// it stood in March.
    /// </summary>
    [Fact]
    public async Task A_second_capture_replaces_the_first_rather_than_joining_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await NewService(dbContext, project.Id, SmallState()).CaptureAsync();
        await NewService(dbContext, project.Id, SmallState(head: "cafe1234", branch: "familiar/sessions")).CaptureAsync();

        var entry = Assert.Single(await dbContext.ContextEntries.AsNoTracking().ToListAsync());
        Assert.Contains("head: cafe1234", entry.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("head: 81d754a", entry.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A process interrupted between the delete and the insert could leave two behind. The invariant
    /// is restored on the next capture rather than assumed to have held.
    /// </summary>
    [Fact]
    public async Task Duplicates_left_by_an_interrupted_write_are_collapsed_to_one()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        for (var index = 0; index < 3; index++)
        {
            dbContext.ContextEntries.Add(new ContextEntry
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                Kind = ContextEntryKind.Summary,
                Title = RepositorySnapshotService.SnapshotTitle,
                Content = "An older snapshot.",
                CreatedUtc = Now.AddDays(-1)
            });
        }

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        await NewService(dbContext, project.Id, SmallState()).CaptureAsync();

        Assert.Single(await dbContext.ContextEntries.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A snapshot that cannot be read must leave the previous one standing rather than replace it
    /// with an apology. A stale snapshot naming the commit it describes is still true about that
    /// commit; an empty one is true about nothing.
    /// </summary>
    [Fact]
    public async Task An_unreadable_repository_leaves_the_previous_snapshot_untouched()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await NewService(dbContext, project.Id, SmallState()).CaptureAsync();

        var outcome = await NewService(dbContext, project.Id, state: null).CaptureAsync();

        Assert.Equal(RepositorySnapshotStatus.Unreadable, outcome.Status);

        var entry = Assert.Single(await dbContext.ContextEntries.AsNoTracking().ToListAsync());
        Assert.Contains("head: 81d754a", entry.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_written_when_no_repository_is_configured()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext);

        var service = new RepositorySnapshotService(
            dbContext,
            new StubRepositoryStateReader(SmallState()),
            Options.Create(new RepositorySnapshotOptions { Enabled = false }),
            new TestTimeProvider(Now),
            NullLogger<RepositorySnapshotService>.Instance);

        Assert.Equal(RepositorySnapshotStatus.NotConfigured, (await service.CaptureAsync()).Status);
        Assert.Empty(await dbContext.ContextEntries.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A context revision bump says the evidence a human is looking at moved underneath them, and it
    /// invalidates every pending proposal and plan that observed the old one. An automated write every
    /// half hour that did this would make plan approval fail permanently — a far worse outcome than a
    /// plan drafted against a snapshot half an hour old.
    /// </summary>
    [Fact]
    public async Task A_capture_does_not_invalidate_pending_plans()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var before = project.ContextRevision;

        await NewService(dbContext, project.Id, SmallState()).CaptureAsync();

        var after = await dbContext.Projects.AsNoTracking().SingleAsync(candidate => candidate.Id == project.Id);
        Assert.Equal(before, after.ContextRevision);
    }

    /// <summary>
    /// The row keeps its id across captures, and this is what stops citations rotting.
    ///
    /// A context entry id is a citable thing: a reply that cites the snapshot is checked against ids
    /// that still resolve. Re-inserting under a fresh id every half hour meant every such citation
    /// decayed into the words "unsupported reference" within one capture interval. Supersession was
    /// always about there being exactly one snapshot; it never needed the one to be a different row.
    /// </summary>
    [Fact]
    public async Task A_second_capture_keeps_the_same_entry_id()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await NewService(dbContext, project.Id, SmallState(head: "81d754a")).CaptureAsync();
        dbContext.ChangeTracker.Clear();

        var first = await dbContext.ContextEntries.AsNoTracking().SingleAsync();

        await NewService(dbContext, project.Id, SmallState(head: "aaaaaaa")).CaptureAsync();
        dbContext.ChangeTracker.Clear();

        var second = await dbContext.ContextEntries.AsNoTracking().SingleAsync();

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.CreatedUtc, second.CreatedUtc);

        // Same row, new content: the id is stable, not the snapshot.
        Assert.Contains("head: aaaaaaa", second.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("head: 81d754a", second.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// More than one snapshot means a prior process was interrupted part way. The invariant is
    /// restored rather than assumed, and the oldest row — the one anything already cited — is the one
    /// that survives.
    /// </summary>
    [Fact]
    public async Task Duplicate_snapshots_are_reduced_to_the_oldest()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        var oldest = Guid.NewGuid();

        foreach (var (id, created) in new[] { (oldest, Now.AddHours(-2)), (Guid.NewGuid(), Now.AddHours(-1)) })
        {
            dbContext.ContextEntries.Add(new ContextEntry
            {
                Id = id,
                ProjectId = project.Id,
                Kind = ContextEntryKind.Summary,
                Title = RepositorySnapshotService.SnapshotTitle,
                Content = "An earlier snapshot.",
                State = ContextEntryState.Active,
                CreatedUtc = created
            });
        }

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        await NewService(dbContext, project.Id, SmallState()).CaptureAsync();
        dbContext.ChangeTracker.Clear();

        Assert.Equal(oldest, (await dbContext.ContextEntries.AsNoTracking().SingleAsync()).Id);
    }

    // ---------------------------------------------------------------- and the Familiar can read it

    /// <summary>
    /// The reason it is an ordinary context entry and not a new table: it arrives through the search
    /// path everything else arrives through, with no migration and no second retrieval rule.
    ///
    /// It must also clear the relevance floor on the question it exists to answer — a snapshot that
    /// is stored but never retrieved is the same as no snapshot.
    /// </summary>
    [Fact]
    public async Task The_snapshot_is_retrievable_through_the_ordinary_search_path()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await NewService(dbContext, project.Id, SmallState()).CaptureAsync();

        var retrieval = new FamiliarContextRetrievalService(
            dbContext,
            Options.Create(new FamiliarRetrievalOptions()));

        var result = await retrieval.RetrieveAsync("what is in the current repository state snapshot?");

        Assert.Equal(RepositorySnapshotService.SnapshotTitle, Assert.Single(result.Entries).Title);
    }

    // ---------------------------------------------------------------- against a real repository

    /// <summary>
    /// CORRECTION 1, asserted against git rather than argued about.
    ///
    /// The original spec walked the filesystem with <c>find . -type f</c> and an exclusion for
    /// <c>.git</c>. This test builds a repository containing exactly what that would have got wrong —
    /// an ignored build directory and an untracked file — and requires that neither appears. Asking
    /// git means the answer already honours <c>.gitignore</c> and is the same on every checkout.
    /// </summary>
    [Fact]
    public async Task Reading_a_real_repository_reports_tracked_files_and_nothing_else()
    {
        var repository = Path.Combine(Path.GetTempPath(), "FindFamiliar.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.Combine(repository, "src", "App"));
            Directory.CreateDirectory(Path.Combine(repository, "src", "App", "obj"));

            await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "obj/\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "# Test\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "src", "App", "Program.cs"), "// code\n");
            await File.WriteAllTextAsync(Path.Combine(repository, "src", "App", "obj", "App.dll"), "binary");
            await File.WriteAllTextAsync(Path.Combine(repository, "untracked.txt"), "not added");

            await GitAsync(repository, "init", "--initial-branch", "trunk");
            await GitAsync(repository, "add", ".gitignore", "README.md", "src/App/Program.cs");
            await GitAsync(
                repository,
                "-c", "user.email=tests@example.invalid",
                "-c", "user.name=Tests",
                "commit", "-m", "First commit");

            var reader = new GitRepositoryStateReader(
                Options.Create(new RepositorySnapshotOptions { Enabled = true, RepositoryPath = repository }),
                NullLogger<GitRepositoryStateReader>.Instance);

            var state = await reader.ReadAsync();

            Assert.NotNull(state);
            Assert.Equal("trunk", state.Branch);
            Assert.Equal(40, state.HeadSha.Length);
            Assert.Equal([".gitignore", "README.md", "src/App/Program.cs"], state.TrackedPaths);
            Assert.Equal([".gitignore", "README.md", "src/App"], state.TwoLevelPaths);

            // The two things a filesystem walk would have swept in.
            Assert.DoesNotContain("src/App/obj/App.dll", state.TrackedPaths);
            Assert.DoesNotContain("untracked.txt", state.TrackedPaths);

            Assert.Single(state.RecentCommits);
            Assert.EndsWith("First commit", state.RecentCommits[0], StringComparison.Ordinal);
        }
        finally
        {
            TemporaryDirectoryCleanup.Delete(repository);
        }
    }

    private static async Task GitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new System.Diagnostics.Process();

        process.StartInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {await error}");
    }

    // ---------------------------------------------------------------- helpers

    private static RepositorySnapshotService NewService(
        FamiliarDbContext dbContext,
        Guid projectId,
        RepositoryState? state) =>
        new(dbContext,
            new StubRepositoryStateReader(state),
            Options.Create(new RepositorySnapshotOptions
            {
                Enabled = true,
                RepositoryPath = OperatingSystem.IsWindows() ? @"C:\repo" : "/srv/repo",
                ProjectId = projectId
            }),
            new TestTimeProvider(Now),
            NullLogger<RepositorySnapshotService>.Instance);

    private static RepositoryState SmallState(string head = "81d754a", string branch = "main")
    {
        string[] tracked = ["README.md", "src/FindFamiliar.Server/Program.cs"];

        return new RepositoryState(
            branch,
            head,
            tracked,
            RepositoryState.TwoLevelView(tracked),
            ["81d754a Make the plan's include checkbox actually include"]);
    }

    /// <summary>
    /// A repository too big for the ceiling. <paramref name="deepPrefixes"/> gives every file its own
    /// second path segment, so the two-level view is as large as the file list and the trim has to
    /// reach past it.
    /// </summary>
    private static RepositoryState LargeState(int fileCount, bool deepPrefixes = false)
    {
        var tracked = Enumerable
            .Range(0, fileCount)
            .Select(index => deepPrefixes
                ? $"src/module-{index:D5}/generated/file-{index:D5}.cs"
                : $"src/generated/file-{index:D5}.cs")
            .Order(StringComparer.Ordinal)
            .ToList();

        var commits = Enumerable
            .Range(0, RepositoryState.RecentCommitCount)
            .Select(index => $"abc{index:D4} A commit subject line of a fairly ordinary length")
            .ToList();

        return new RepositoryState("main", "81d754a", tracked, RepositoryState.TwoLevelView(tracked), commits);
    }

    private static async Task<FamiliarProject> SeedProjectAsync(FamiliarDbContext dbContext)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = "Find Familiar",
            Purpose = "Preserve project context across sessions.",
            Status = ProjectStatus.Active,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private sealed class StubRepositoryStateReader(RepositoryState? state) : IRepositoryStateReader
    {
        public Task<RepositoryState?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(state);
    }
}
