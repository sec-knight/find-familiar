using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Familiar.Chat;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// Citations, and the check that makes them worth anything.
///
/// A plan is an argument about what to do next, and slice 3 has the Familiar drafting them. An
/// argument built on invented evidence ids is worse than no argument at all, because it is
/// persuasive: a reader who sees sources assumes someone could follow them. So a reference is only
/// shown as a source when it was in the pack the answer was actually given, and one that was not is
/// marked rather than removed — a reply naming something it was never shown is the most diagnostic
/// thing it can do, and hiding it would throw that signal away.
/// </summary>
public sealed class FamiliarChatCitationTests
{
    private static readonly Guid Offered = Guid.Parse("5e3458cc-e9c2-469d-b19f-e60874b0e073");
    private static readonly Guid NeverOffered = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void An_id_from_the_pack_is_a_supported_citation()
    {
        var segments = FamiliarChatCitations.Segment($"The lanes are separate ({Offered}).", [Offered]);

        var citation = Assert.Single(segments, segment => segment.IsCitation);

        Assert.Equal(Offered, citation.EntryId);
        Assert.True(citation.IsSupported);
    }

    /// <summary>
    /// The load-bearing one. An id the model was never shown is not evidence, however well-formed it
    /// looks, and it must not render as a source.
    /// </summary>
    [Fact]
    public void An_id_that_was_never_offered_is_unsupported()
    {
        var segments = FamiliarChatCitations.Segment($"As decided in {NeverOffered}.", [Offered]);

        var citation = Assert.Single(segments, segment => segment.IsCitation);

        Assert.Equal(NeverOffered, citation.EntryId);
        Assert.False(citation.IsSupported);
    }

    /// <summary>
    /// Kept, not deleted. A reader is entitled to see that the reply cited something imaginary, and so
    /// is anyone reading the transcript months later.
    /// </summary>
    [Fact]
    public void An_unsupported_citation_is_not_silently_dropped()
    {
        var segments = FamiliarChatCitations.Segment($"Because of {NeverOffered}, we deferred it.", []);

        Assert.Contains(segments, segment => segment.IsCitation && !segment.IsSupported);
        Assert.Equal(
            "Because of , we deferred it.",
            string.Concat(segments.Where(segment => !segment.IsCitation).Select(segment => segment.Text)));
    }

    /// <summary>
    /// Text either side of a citation survives exactly, including the spacing. Reassembling the
    /// non-citation segments must give back the reply with only the ids removed.
    /// </summary>
    [Fact]
    public void Surrounding_text_is_preserved_exactly()
    {
        var output = $"First point.\n\nSecond, see {Offered} — which settles it.";
        var segments = FamiliarChatCitations.Segment(output, [Offered]);

        Assert.Equal(
            output.Replace(Offered.ToString(), string.Empty, StringComparison.Ordinal),
            string.Concat(segments.Where(segment => !segment.IsCitation).Select(segment => segment.Text)));
    }

    [Fact]
    public void Several_citations_are_found_in_order()
    {
        var second = Guid.Parse("99999999-8888-7777-6666-555555555555");

        var segments = FamiliarChatCitations.Segment($"{Offered} and then {second}.", [Offered, second]);

        Assert.Equal(
            [Offered, second],
            segments.Where(segment => segment.IsCitation).Select(segment => segment.EntryId));
    }

    /// <summary>
    /// A reply with no ids allocates one segment and comes back unchanged. This is the common case and
    /// the segmenter must not disturb it.
    /// </summary>
    [Fact]
    public void A_reply_with_no_citations_is_one_untouched_segment()
    {
        var segment = Assert.Single(FamiliarChatCitations.Segment("Nothing is recorded about that.", [Offered]));

        Assert.False(segment.IsCitation);
        Assert.Equal("Nothing is recorded about that.", segment.Text);
    }

    /// <summary>
    /// Without a boundary check the tail of a longer token parses as an id, and a citation gets
    /// invented out of something that was never one.
    /// </summary>
    [Theory]
    [InlineData("prefix-{0}")]
    [InlineData("{0}-suffix")]
    [InlineData("x{0}")]
    public void An_id_inside_a_longer_token_is_not_a_citation(string template)
    {
        var output = string.Format(template, Offered);

        Assert.DoesNotContain(FamiliarChatCitations.Segment(output, [Offered]), segment => segment.IsCitation);
    }

    [Fact]
    public void An_id_in_brackets_or_parentheses_is_still_found() =>
        Assert.All(
            new[] { $"[{Offered}]", $"({Offered})", $"see: {Offered}.", $"{Offered}" },
            output => Assert.Contains(
                FamiliarChatCitations.Segment(output, [Offered]),
                segment => segment.IsCitation && segment.IsSupported));

    // ---------------------------------------------------------------- what the row stores

    [Fact]
    public void Evidence_round_trips_through_the_column()
    {
        var ids = new[] { Offered, NeverOffered };

        Assert.Equal(ids, FamiliarChatCitations.ParseEvidence(FamiliarChatCitations.SerialiseEvidence(ids)));
    }

    [Fact]
    public void No_evidence_stores_null_rather_than_an_empty_string() =>
        Assert.Null(FamiliarChatCitations.SerialiseEvidence([]));

    /// <summary>
    /// Turns written before retrieval existed hold null, which reads correctly as "nothing was
    /// offered" — so every id in them is unsupported, which is exactly what they are.
    /// </summary>
    [Fact]
    public void A_turn_with_no_recorded_evidence_supports_nothing()
    {
        var offered = FamiliarChatCitations.ParseEvidence(null);

        Assert.Empty(offered);
        Assert.DoesNotContain(
            FamiliarChatCitations.Segment($"See {Offered}.", offered.ToHashSet()),
            segment => segment.IsCitation && segment.IsSupported);
    }

    /// <summary>
    /// The stored form must fit its column even if a future pack is larger than today's cap, and it
    /// must be trimmed at a whole id rather than mid-token — a half id is not an id.
    /// </summary>
    [Fact]
    public void The_stored_form_is_bounded_by_its_column_and_trimmed_at_a_whole_id()
    {
        var many = Enumerable.Range(0, 40).Select(_ => Guid.NewGuid()).ToList();

        var stored = FamiliarChatCitations.SerialiseEvidence(many)!;

        Assert.True(stored.Length <= FamiliarChatTurn.MaxEvidenceLength);
        Assert.All(
            stored.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            part => Assert.True(Guid.TryParse(part, out _)));
    }

    /// <summary>An unreadable fragment must not fail the read of an otherwise good transcript.</summary>
    [Fact]
    public void An_unreadable_stored_fragment_is_skipped_rather_than_fatal() =>
        Assert.Equal([Offered], FamiliarChatCitations.ParseEvidence($"not-an-id {Offered:N}"));
}
