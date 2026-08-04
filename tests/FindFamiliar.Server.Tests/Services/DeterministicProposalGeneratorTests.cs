using System.Globalization;
using FindFamiliar.Server.Services;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The proposal engine is pure, so these are ordinary unit tests with no database and no host.
/// That is the point: a proposal shown before approval must be reproducible and explainable, and
/// nothing here can reach a provider.
/// </summary>
public sealed class DeterministicProposalGeneratorTests
{
    private static ProposalProjectCandidate Candidate(string name) =>
        new(Guid.NewGuid(), name);

    [Fact]
    public void Title_is_the_first_non_empty_line_of_the_request()
    {
        var title = DeterministicProposalGenerator.BuildTitle(
            "\n\n   \nReview the intake implementation\nThen list the follow-up work.\n");

        Assert.Equal("Review the intake implementation", title);
    }

    [Fact]
    public void Title_handles_carriage_returns_from_browser_submitted_text()
    {
        var title = DeterministicProposalGenerator.BuildTitle("First line\r\nSecond line");

        Assert.Equal("First line", title);
    }

    [Fact]
    public void Title_is_bounded_to_two_hundred_characters()
    {
        var title = DeterministicProposalGenerator.BuildTitle(new string('a', 500));

        Assert.Equal(200, title.Length);
    }

    [Fact]
    public void Title_never_splits_a_surrogate_pair()
    {
        // 199 ASCII characters then an astral-plane emoji: cutting at 200 would leave half a
        // surrogate pair and produce an unrenderable string.
        var request = new string('a', 199) + "\U0001F600";

        var title = DeterministicProposalGenerator.BuildTitle(request);

        Assert.Equal(199, title.Length);
        Assert.DoesNotContain(title, character => char.IsSurrogate(character));
        Assert.Equal(new string('a', 199), title);
    }

    [Fact]
    public void Title_never_splits_a_combining_sequence()
    {
        // "e" + combining acute accent renders as one grapheme; truncation must keep them together.
        var request = new string('a', 199) + "éextra";

        var title = DeterministicProposalGenerator.BuildTitle(request);

        Assert.Equal(199, title.Length);
        Assert.EndsWith("a", title, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_keeps_a_whole_emoji_that_fits()
    {
        var request = new string('a', 100) + "\U0001F600";

        var title = DeterministicProposalGenerator.BuildTitle(request);

        Assert.Equal(request, title);
        Assert.Equal(
            101,
            new StringInfo(title).LengthInTextElements);
    }

    [Fact]
    public void Requested_outcome_is_the_full_trimmed_request()
    {
        var outcome = DeterministicProposalGenerator.BuildRequestedOutcome(
            "  Review the intake implementation\nThen list follow-up work.  ");

        Assert.Equal("Review the intake implementation\nThen list follow-up work.", outcome);
    }

    [Fact]
    public void Exactly_one_complete_name_match_is_proposed()
    {
        var target = Candidate("Find Familiar");
        var other = Candidate("Ledger Sync");

        var resolution = DeterministicProposalGenerator.ResolveProject(
            "Please review the Find Familiar intake slice.",
            [target, other]);

        Assert.Equal(ProposalProjectResolution.MatchedByName, resolution.Resolution);
        Assert.Equal(target.Id, resolution.ProjectId);
    }

    [Fact]
    public void Name_matching_is_culture_invariant_and_case_insensitive()
    {
        var target = Candidate("FIND FAMILIAR");

        var resolution = DeterministicProposalGenerator.ResolveProject(
            "look at find familiar please",
            [target, Candidate("Something Else")]);

        Assert.Equal(target.Id, resolution.ProjectId);
    }

    [Fact]
    public void Name_matching_ignores_differences_in_whitespace_runs()
    {
        var target = Candidate("Find   Familiar");

        var resolution = DeterministicProposalGenerator.ResolveProject(
            "work on Find Familiar",
            [target, Candidate("Second Project")]);

        Assert.Equal(target.Id, resolution.ProjectId);
    }

    [Fact]
    public void A_partial_word_is_not_a_complete_name_match()
    {
        // "Find" must not claim a request about a "Finder": a prefix is not a complete name.
        var resolution = DeterministicProposalGenerator.ResolveProject(
            "improve the Finder integration",
            [Candidate("Find"), Candidate("Ledger Sync")]);

        Assert.Equal(ProposalProjectResolution.NoMatch, resolution.Resolution);
        Assert.Null(resolution.ProjectId);
    }

    [Fact]
    public void Multiple_name_matches_stay_unresolved()
    {
        var resolution = DeterministicProposalGenerator.ResolveProject(
            "reconcile Find Familiar with Ledger Sync",
            [Candidate("Find Familiar"), Candidate("Ledger Sync")]);

        Assert.Equal(ProposalProjectResolution.AmbiguousNameMatch, resolution.Resolution);
        Assert.Null(resolution.ProjectId);
    }

    [Fact]
    public void The_only_active_project_is_proposed_when_no_name_matches()
    {
        var only = Candidate("Find Familiar");

        var resolution = DeterministicProposalGenerator.ResolveProject(
            "tidy up the deployment story",
            [only]);

        Assert.Equal(ProposalProjectResolution.OnlyActiveProject, resolution.Resolution);
        Assert.Equal(only.Id, resolution.ProjectId);
    }

    [Fact]
    public void Several_active_projects_with_no_name_match_stay_unresolved()
    {
        var resolution = DeterministicProposalGenerator.ResolveProject(
            "tidy up the deployment story",
            [Candidate("Find Familiar"), Candidate("Ledger Sync")]);

        Assert.Equal(ProposalProjectResolution.NoMatch, resolution.Resolution);
        Assert.Null(resolution.ProjectId);
    }

    [Fact]
    public void No_candidates_means_nothing_can_be_proposed()
    {
        var resolution = DeterministicProposalGenerator.ResolveProject("anything at all", []);

        Assert.Equal(ProposalProjectResolution.NoActiveProjects, resolution.Resolution);
        Assert.Null(resolution.ProjectId);
    }

    [Fact]
    public void Resolution_is_deterministic_across_repeated_calls()
    {
        var candidates = new List<ProposalProjectCandidate>
        {
            Candidate("Find Familiar"),
            Candidate("Ledger Sync"),
            Candidate("Atlas")
        };

        var first = DeterministicProposalGenerator.ResolveProject("plan the Atlas rollout", candidates);
        var second = DeterministicProposalGenerator.ResolveProject("plan the Atlas rollout", candidates);

        Assert.Equal(first, second);
        Assert.Equal(candidates[2].Id, first.ProjectId);
    }
}
