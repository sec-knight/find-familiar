using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat.Planning;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The boundary where model text stops being text.
///
/// Everything past this reader is written to a database, so the rules are deliberately harsh: an
/// unparseable reply produces no plan rather than a partial one, an unknown role produces no role
/// rather than a guessed one, and an item citing evidence that was never in the pack loses the
/// citation. A parser that repairs what it reads will one day repair something into meaning work
/// nobody proposed.
/// </summary>
public sealed class FamiliarPlanDraftTests
{
    private static readonly Guid Offered = Guid.Parse("5e3458cc-e9c2-469d-b19f-e60874b0e073");
    private static readonly Guid NeverOffered = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void A_well_formed_plan_is_read()
    {
        var plan = FamiliarPlanDraftReader.Read(
            $$"""
              {"summary": "Close the loop.",
               "items": [
                 {"title": "Re-specify the anchor task",
                  "requestedOutcome": "The constraint reflects that the app now ships JavaScript.",
                  "role": "Planner",
                  "evidence": ["{{Offered}}"]}
               ]}
              """,
            [Offered]);

        Assert.NotNull(plan);
        Assert.Equal("Close the loop.", plan!.Summary);

        var item = Assert.Single(plan.Items);
        Assert.Equal("Re-specify the anchor task", item.Title);
        Assert.Equal(AgentSessionRole.Planner, item.Role);
        Assert.Equal([Offered], item.EvidenceEntryIds);
    }

    /// <summary>
    /// The same model returns bare JSON for a short answer and fenced JSON once the prompt reaches
    /// full size — the failure commit 41b35e1 recorded. A reader handling only one spelling works in
    /// testing and fails in use.
    /// </summary>
    [Fact]
    public void A_fenced_reply_is_read_the_same_as_a_bare_one()
    {
        const string body = """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}""";

        var fenced = FamiliarPlanDraftReader.Read("```json\n" + body + "\n```", []);
        var bare = FamiliarPlanDraftReader.Read(body, []);

        Assert.NotNull(fenced);
        Assert.Equal(bare!.Items[0].Title, fenced!.Items[0].Title);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ broken")]
    [InlineData("")]
    [InlineData(null)]
    public void Unreadable_output_produces_no_plan(string? output) =>
        Assert.Null(FamiliarPlanDraftReader.Read(output, []));

    /// <summary>
    /// Nothing to propose is a real answer. It must not become an empty plan sitting in a
    /// conversation waiting to be approved.
    /// </summary>
    [Fact]
    public void An_empty_item_list_produces_no_plan() =>
        Assert.Null(FamiliarPlanDraftReader.Read("""{"summary": "Nothing follows from the records.", "items": []}""", []));

    /// <summary>
    /// An item without a title or an outcome is not something a person could evaluate. Dropped rather
    /// than filled with a placeholder, which would read as intent nobody expressed.
    /// </summary>
    [Fact]
    public void An_item_missing_a_title_or_outcome_is_dropped()
    {
        var plan = FamiliarPlanDraftReader.Read(
            """
            {"summary": "s", "items": [
              {"title": "", "requestedOutcome": "o"},
              {"title": "t", "requestedOutcome": "   "},
              {"title": "kept", "requestedOutcome": "and this"}
            ]}
            """,
            []);

        Assert.Equal("kept", Assert.Single(plan!.Items).Title);
    }

    /// <summary>
    /// An unrecognised role yields none rather than a default. Defaulting would silently turn "start
    /// something I did not understand" into "start a Planner".
    /// </summary>
    [Theory]
    [InlineData("Architect")]
    [InlineData("")]
    [InlineData(null)]
    public void An_unknown_role_is_no_role(string? role)
    {
        var json = $$"""{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o", "role": {{(role is null ? "null" : $"\"{role}\"")}}}]}""";

        Assert.Null(FamiliarPlanDraftReader.Read(json, [])!.Items[0].Role);
    }

    [Fact]
    public void A_role_is_read_regardless_of_case() =>
        Assert.Equal(
            AgentSessionRole.Implementer,
            FamiliarPlanDraftReader.Read(
                """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o", "role": "implementer"}]}""",
                [])!.Items[0].Role);

    /// <summary>
    /// The load-bearing one. A plan is an argument about what to do next, and an argument built on
    /// invented sources is worse than one with none, because it is persuasive.
    /// </summary>
    [Fact]
    public void Evidence_that_was_never_offered_is_dropped()
    {
        var plan = FamiliarPlanDraftReader.Read(
            $$"""
              {"summary": "s", "items": [
                {"title": "t", "requestedOutcome": "o", "evidence": ["{{NeverOffered}}", "{{Offered}}", "nonsense"]}
              ]}
              """,
            [Offered]);

        Assert.Equal([Offered], plan!.Items[0].EvidenceEntryIds);
    }

    [Fact]
    public void An_item_may_cite_nothing() =>
        Assert.Empty(
            FamiliarPlanDraftReader.Read(
                """{"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]}""",
                [Offered])!.Items[0].EvidenceEntryIds);

    /// <summary>
    /// The cap exists so an approval stays an act of reading. Truncated rather than refused: a plan of
    /// nine is not nine times as wrong as a plan of eight.
    /// </summary>
    [Fact]
    public void More_items_than_the_cap_are_truncated()
    {
        var items = string.Join(
            ',',
            Enumerable.Range(0, FamiliarPlanProposal.MaxItems + 5)
                .Select(index => $$"""{"title": "t{{index}}", "requestedOutcome": "o"}"""));

        var plan = FamiliarPlanDraftReader.Read($$"""{"summary": "s", "items": [{{items}}]}""", []);

        Assert.Equal(FamiliarPlanProposal.MaxItems, plan!.Items.Count);
    }

    /// <summary>A title with a newline in it breaks every list it is ever rendered into.</summary>
    [Fact]
    public void A_title_is_collapsed_to_one_line_and_bounded()
    {
        var plan = FamiliarPlanDraftReader.Read(
            $$"""{"summary": "s", "items": [{"title": "one\ntwo   three", "requestedOutcome": "{{new string('x', 6_000)}}"}]}""",
            []);

        Assert.Equal("one two three", plan!.Items[0].Title);
        Assert.Equal(FamiliarPlanItem.MaxRequestedOutcomeLength, plan.Items[0].RequestedOutcome.Length);
    }

    /// <summary>
    /// Prose wrapped around the JSON is tolerated. A model that opens with "Here is the plan:" has not
    /// failed, and refusing over it would throw away a good plan for a cosmetic reason.
    /// </summary>
    [Fact]
    public void Surrounding_prose_does_not_prevent_a_read() =>
        Assert.NotNull(FamiliarPlanDraftReader.Read(
            """Here is the plan: {"summary": "s", "items": [{"title": "t", "requestedOutcome": "o"}]} Let me know.""",
            []));
}
