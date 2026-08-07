using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Retrieval;
using FindFamiliar.Server.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The Familiar reading its own memory.
///
/// Until Sprint 13 it could see projects and tasks and nothing else, so every result its own sessions
/// had harvested since Sprint 9 — and every recorded decision about why this system is shaped the way
/// it is — sat in a store it could not read. These tests are about the store being readable, and about
/// the two ways reading it could do harm: carrying something that was marked sensitive, and finding
/// nothing while appearing to have found something.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarContextRetrievalTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- finding the right thing

    [Fact]
    public async Task A_question_finds_the_decision_that_answers_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision,
            "Separate the conversational provider from the execution runner",
            "The talk lane streams prose and declares no tools. The Runner executes work through an "
            + "adapter. They are different seams with different providers.");

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Implementation,
            "Worker retry backoff",
            "The worker retries a failed dispatch three times with exponential backoff.");

        var result = await Retrieve(dbContext, "why is the talk lane separate from the runner?");

        var entry = Assert.Single(result.Entries);
        Assert.Equal(ContextEntryKind.Decision, entry.Kind);
        Assert.Contains("different seams", entry.Excerpt, StringComparison.Ordinal);
    }

    /// <summary>
    /// An entry matching nothing is not weakly relevant, it is irrelevant. Carrying it would cost
    /// tokens and invite the model to use it.
    /// </summary>
    [Fact]
    public async Task An_entry_matching_nothing_is_not_carried()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Goal, "Kitchen renovation budget",
            "Cabinets, worktops and a new extractor fan.");

        Assert.True((await Retrieve(dbContext, "why is the talk lane separate?")).FoundNothing);
    }

    /// <summary>
    /// Breadth beats depth. An entry touching three of the question's words is more likely to be about
    /// the question than one repeating a single word.
    /// </summary>
    [Fact]
    public async Task Matching_more_of_the_question_outranks_repeating_one_word()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary, "Broad",
            "The retrieval budget bounds the excerpt carried into a prompt.");

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary, "Narrow",
            "retrieval retrieval retrieval retrieval retrieval retrieval");

        var result = await Retrieve(dbContext, "how does the retrieval budget bound an excerpt?");

        Assert.Equal("Broad", result.Entries[0].Title);
    }

    /// <summary>
    /// A term in the title is a statement that the entry is about that thing; the same term in a long
    /// body may be one mention in nine hundred characters.
    /// </summary>
    [Fact]
    public async Task A_title_match_outweighs_a_body_mention()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary, "Handoff approval gate",
            "Unrelated body text about scheduling.");

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary, "Weekly notes",
            "Among other things we discussed handoff once.");

        Assert.Equal("Handoff approval gate", (await Retrieve(dbContext, "handoff")).Entries[0].Title);
    }

    /// <summary>
    /// Hyphenated and numbered identifiers are the most precise query a person can type here, and
    /// splitting them on the punctuation would destroy exactly the signal worth having.
    /// </summary>
    [Fact]
    public async Task An_identifier_survives_as_one_term()
    {
        Assert.Contains("adr-0013", FamiliarQueryTerms.Extract("what does ADR-0013 say?"));
        Assert.Contains("grok-4", FamiliarQueryTerms.Extract("are we on grok-4 still"));
    }

    [Fact]
    public void Words_that_separate_nothing_are_dropped()
    {
        var terms = FamiliarQueryTerms.Extract("What is the state of the work, and how should I run it?");

        Assert.DoesNotContain("what", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("and", terms);

        // Stop lists in general prose discard these three. They are among the most load-bearing nouns
        // in this system, and dropping them would make its own vocabulary unsearchable.
        Assert.Contains("state", terms);
        Assert.Contains("work", terms);
        Assert.Contains("run", terms);
    }

    // ---------------------------------------------------------------- the sensitivity boundary

    [Fact]
    public async Task A_sensitive_entry_is_never_retrieved_and_is_counted()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Retention decision",
            "A distinctive sentence that must never leave this machine.", isSensitive: true);

        var result = await Retrieve(dbContext, "what was the retention decision?");

        Assert.True(result.FoundNothing);
        Assert.Equal(1, result.SensitiveWithheld);

        var written = FamiliarRetrievalWriter.Write(result)!;
        Assert.DoesNotContain("distinctive sentence", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Retention decision", written, StringComparison.OrdinalIgnoreCase);

        // What, not which: the count is the honest disclosure, the content is what is protected.
        Assert.Contains("marked sensitive", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_in_a_sensitive_project_is_never_retrieved()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext, isSensitive: true);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Retention decision",
            "A distinctive sentence that must never leave this machine.");

        var result = await Retrieve(dbContext, "what was the retention decision?");

        Assert.True(result.FoundNothing);
        Assert.Equal(1, result.SensitiveWithheld);
    }

    /// <summary>
    /// A superseded entry is a record of what was believed then, and answering from it would state a
    /// decision that has since been reversed.
    /// </summary>
    [Fact]
    public async Task A_superseded_entry_is_not_retrieved()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Model choice",
            "We use grok-4.1-fast.", state: ContextEntryState.Superseded);

        Assert.True((await Retrieve(dbContext, "which model choice did we make?")).FoundNothing);
    }

    /// <summary>
    /// Prompt and RawOutput are the verbatim input and output of a previous agent run. Feeding those
    /// back teaches a model to imitate them — to write in a session transcript's voice, or to treat an
    /// instruction addressed to a Planner as one addressed to itself. Same rule that keeps failed
    /// turns out of conversation history.
    /// </summary>
    [Theory]
    [InlineData(ContextEntryKind.Prompt)]
    [InlineData(ContextEntryKind.RawOutput)]
    public async Task Machine_text_kinds_are_never_retrieved(ContextEntryKind kind)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project, kind, "Planner assignment",
            "You are a Planner. Produce a plan for the assignment below.");

        Assert.True((await Retrieve(dbContext, "what was the planner assignment?")).FoundNothing);
    }

    // ---------------------------------------------------------------- finding nothing, out loud

    /// <summary>
    /// The load-bearing test of the slice. A model shown no context and no statement that none was
    /// found answers from general knowledge in the same confident register it uses for facts — which
    /// is the failure this whole application exists to prevent. Finding nothing is information.
    /// </summary>
    [Fact]
    public async Task Finding_nothing_is_written_into_the_prompt_as_a_finding()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext);

        var written = FamiliarRetrievalWriter.Write(await Retrieve(dbContext, "what did we decide about caching?"));

        Assert.NotNull(written);
        Assert.Contains("Nothing recorded matches this", written, StringComparison.Ordinal);
        Assert.Contains("do not supply an answer from general", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// A message with nothing selective in it produces no block at all, rather than a block of the
    /// newest entries — which would put unrelated context in front of a model and invite it to connect
    /// the two.
    /// </summary>
    [Fact]
    public async Task A_message_with_no_selective_words_searches_for_nothing()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Something", "Anything.");

        var result = await Retrieve(dbContext, "ok, and then?");

        Assert.Empty(result.Terms);
        Assert.Null(FamiliarRetrievalWriter.Write(result));
    }

    // ---------------------------------------------------------------- the relevance floor

    /// <summary>
    /// The regression test for the observed defect.
    ///
    /// Pressing "Plan this" on a question about repository snapshots returned an unrelated open item,
    /// "DEFECT: plans name absolute paths", worded as though it were a freshly drafted plan. The
    /// planning path was never at fault: the search under it had no floor, so it returned its best
    /// candidate whatever that candidate scored, and its best candidate shared exactly one word with
    /// the question — in a title, which is the heaviest signal this scorer has.
    ///
    /// The entry that actually answers the question is present here too, so this asserts the floor
    /// discriminates rather than merely suppresses.
    /// </summary>
    [Fact]
    public async Task A_question_about_repository_snapshots_does_not_return_the_absolute_paths_defect()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.OpenQuestion,
            "DEFECT: plans name absolute paths",
            "Drafted plans name absolute paths in their files_touched lists, which are wrong on any "
            + "machine but this one.");

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary,
            "Repository state snapshot (current)",
            "Automated snapshot of the repository state: tracked files, recent commits, and a "
            + "two-level view of tracked paths.");

        var result = await Retrieve(dbContext, "what does the automated repository state snapshot record?");

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Repository state snapshot (current)", entry.Title);

        var written = FamiliarRetrievalWriter.Write(result)!;
        Assert.DoesNotContain("absolute paths", written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The shape of the defect, isolated: one term, landing in a title, on a question of five.
    ///
    /// A title hit is worth eight content hits, so this scores well above any absolute floor. Only
    /// breadth separates it from a real answer, which is why the floor is two numbers and not one.
    /// </summary>
    [Fact]
    public async Task One_term_landing_in_a_title_is_not_an_answer()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.OpenQuestion,
            "DEFECT: plans name absolute paths",
            "Drafted plans name absolute paths in their files_touched lists.");

        var result = await Retrieve(dbContext, "can you plan the automated repository state snapshot?");

        Assert.True(result.FoundNothing);
        Assert.True(result.NoMatchAboveFloor);
        Assert.Equal(1, result.BelowThreshold);
    }

    /// <summary>
    /// A near-miss is stated as a near-miss. The count travels into the prompt and the content does
    /// not — the same disclosure rule sensitivity follows, applied to irrelevance.
    ///
    /// Saying nothing at all here would be worse than it sounds: retrieval genuinely did surface
    /// something, and a model told only "nothing matches" while the store visibly contains the words
    /// it asked about has been told something misleading.
    /// </summary>
    [Fact]
    public async Task A_near_miss_is_disclosed_as_a_count_and_never_as_content()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.OpenQuestion,
            "DEFECT: plans name absolute paths",
            "A distinctive sentence that is not an answer to anything asked here.");

        var written = FamiliarRetrievalWriter.Write(
            await Retrieve(dbContext, "can you plan the automated repository state snapshot?"))!;

        Assert.Contains("Nothing recorded matches this", written, StringComparison.Ordinal);
        Assert.Contains("not close enough to be responsive", written, StringComparison.Ordinal);
        Assert.DoesNotContain("distinctive sentence", written, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("absolute paths", written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// On a one-word question breadth can say nothing — every match touches the only term there is —
    /// so the absolute floor is the whole guard, and a passing mention in an unrelated note is exactly
    /// what it rejects.
    /// </summary>
    [Fact]
    public async Task On_a_one_word_question_a_passing_mention_does_not_clear_the_floor()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary, "Weekly notes",
            "Among other things we discussed handoff once.");

        var result = await Retrieve(dbContext, "handoff");

        Assert.True(result.NoMatchAboveFloor);
        Assert.Equal(1, result.BelowThreshold);
    }

    /// <summary>
    /// The clamp. A single identifier is the most precise query a person can type here, and a bar of
    /// two terms applied literally would make it unanswerable.
    /// </summary>
    [Fact]
    public async Task A_one_word_question_can_still_be_answered()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "ADR-0013 provider split",
            "The talk lane and the reasoning lane are configured separately.");

        Assert.Equal("ADR-0013 provider split", (await Retrieve(dbContext, "adr-0013")).Entries[0].Title);
    }

    /// <summary>
    /// An empty store is not a near-miss, and the prompt must not conflate them. "Nothing is written
    /// down about this" and "things were written down and none of them answer you" lead a reader to
    /// different next actions.
    /// </summary>
    [Fact]
    public async Task An_empty_store_reports_no_match_and_no_near_misses()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        await SeedProjectAsync(dbContext);

        var result = await Retrieve(dbContext, "what does the repository state snapshot record?");

        Assert.True(result.FoundNothing);
        Assert.Equal(0, result.BelowThreshold);
        Assert.False(result.NoMatchAboveFloor);

        var written = FamiliarRetrievalWriter.Write(result)!;
        Assert.Contains("Nothing recorded matches this", written, StringComparison.Ordinal);
        Assert.DoesNotContain("not close enough to be responsive", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bar is configuration, not a literal. The corpus this searches is thirty-odd entries today
    /// and will not be, and the right numbers are a property of the corpus rather than the algorithm.
    /// </summary>
    [Fact]
    public async Task Raising_the_bar_in_configuration_raises_it()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary, "Snapshot trimming",
            "The snapshot trims the tree section before the log.");

        var question = "how does the snapshot trim its tree section?";

        Assert.False((await Retrieve(dbContext, question)).FoundNothing);

        // Raised past anything this scorer produces, so the same question that just succeeded now
        // reports an explicit no-match. Nothing about the query changed; only where the bar sits.
        var strict = new FamiliarRetrievalOptions { MinimumScore = 1_000 };
        Assert.True((await Retrieve(dbContext, question, options: strict)).NoMatchAboveFloor);
    }

    // ---------------------------------------------------------------- bounds and determinism

    [Fact]
    public async Task No_more_than_the_cap_is_carried()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        for (var index = 0; index < FamiliarRetrievalResult.MaxEntries + 4; index++)
        {
            await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary,
                $"Retrieval note {index}", "About retrieval and budgets.");
        }

        var result = await Retrieve(dbContext, "retrieval budgets");

        Assert.Equal(FamiliarRetrievalResult.MaxEntries, result.Entries.Count);
    }

    /// <summary>
    /// An entry longer than the budget is excerpted around the match, not truncated from the start.
    /// The useful half of a long decision is routinely in the middle, and a head-truncated entry is
    /// the specific way retrieval fails while appearing to have worked.
    /// </summary>
    [Fact]
    public async Task A_long_entry_is_excerpted_around_the_match()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        var padding = new string('x', FamiliarRetrievalResult.MaxExcerptCharacters * 2);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Long one",
            padding + " the buried finding is here " + padding);

        var entry = Assert.Single((await Retrieve(dbContext, "buried finding")).Entries);

        Assert.True(entry.IsExcerpted);
        Assert.Contains("buried finding", entry.Excerpt, StringComparison.Ordinal);
        Assert.True(entry.Excerpt.Length <= FamiliarRetrievalResult.MaxExcerptCharacters + 2);
    }

    /// <summary>
    /// Content with no whitespace to stop it — a hash, a base64 blob, a minified payload — must not
    /// drag the excerpt's start back to the head of the entry. Losing a word boundary is cosmetic;
    /// losing the match is the whole point of the excerpt, and it fails while looking like it worked.
    /// </summary>
    [Fact]
    public async Task An_unbroken_run_does_not_drag_the_excerpt_off_the_match()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        var blob = new string('x', FamiliarRetrievalResult.MaxExcerptCharacters * 2);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Blob",
            blob + "watermark" + blob);

        Assert.Contains(
            "watermark",
            Assert.Single((await Retrieve(dbContext, "watermark")).Entries).Excerpt,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The same question must produce a byte-identical block, or nothing about an answer is
    /// reproducible and two identical turns cost two uncached prompts.
    /// </summary>
    [Fact]
    public async Task The_same_question_produces_the_same_block()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);

        // Identical scores, so only the tie-break can order them.
        for (var index = 0; index < 4; index++)
        {
            await SeedEntryAsync(dbContext, project, ContextEntryKind.Summary,
                "Retrieval note", "About retrieval.", createdUtc: Now);
        }

        var first = FamiliarRetrievalWriter.Write(await Retrieve(dbContext, "retrieval note"));
        var second = FamiliarRetrievalWriter.Write(await Retrieve(dbContext, "retrieval note"));

        Assert.Equal(first, second);
    }

    /// <summary>
    /// Every carried entry writes its id, because an answer is expected to cite it and slice 2
    /// validates those citations against exactly these ids.
    /// </summary>
    [Fact]
    public async Task Every_carried_entry_writes_its_id()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var project = await SeedProjectAsync(dbContext);
        await SeedEntryAsync(dbContext, project, ContextEntryKind.Decision, "Retrieval shape",
            "Server-side and deterministic.");

        var result = await Retrieve(dbContext, "retrieval shape");
        var written = FamiliarRetrievalWriter.Write(result)!;

        Assert.Contains(result.Entries[0].EntryId.ToString(), written, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not cite an id that does not appear above", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// Focus is a lean, never a filter — the same rule the standing brief follows. A cross-project
    /// question must still reach the project that answers it.
    /// </summary>
    [Fact]
    public async Task Focus_leans_without_excluding_other_projects()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var focused = await SeedProjectAsync(dbContext, name: "Focused");
        var other = await SeedProjectAsync(dbContext, name: "Other");

        await SeedEntryAsync(dbContext, focused, ContextEntryKind.Summary, "Caching note", "About caching.");
        await SeedEntryAsync(dbContext, other, ContextEntryKind.Summary, "Caching note", "About caching.");

        var result = await Retrieve(dbContext, "caching note", focused.Id);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal(focused.Id, result.Entries[0].ProjectId);
    }

    // ---------------------------------------------------------------- helpers

    private static Task<FamiliarRetrievalResult> Retrieve(
        FamiliarDbContext dbContext,
        string message,
        Guid? focusProjectId = null,
        FamiliarRetrievalOptions? options = null) =>
        new FamiliarContextRetrievalService(dbContext, Options.Create(options ?? new FamiliarRetrievalOptions()))
            .RetrieveAsync(message, focusProjectId);

    private static async Task<FamiliarProject> SeedProjectAsync(
        FamiliarDbContext dbContext,
        string name = "Find Familiar",
        bool isSensitive = false)
    {
        var project = new FamiliarProject
        {
            Id = Guid.NewGuid(),
            Name = name,
            Purpose = "Preserve project context across sessions.",
            Status = ProjectStatus.Active,
            IsSensitive = isSensitive,
            CreatedUtc = Now,
            UpdatedUtc = Now
        };

        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        return project;
    }

    private static async Task SeedEntryAsync(
        FamiliarDbContext dbContext,
        FamiliarProject project,
        ContextEntryKind kind,
        string title,
        string content,
        bool isSensitive = false,
        ContextEntryState state = ContextEntryState.Active,
        DateTime? createdUtc = null)
    {
        dbContext.ContextEntries.Add(new ContextEntry
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = kind,
            Title = title,
            Content = content,
            State = state,
            IsSensitive = isSensitive,
            CreatedUtc = createdUtc ?? Now
        });

        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
    }
}
