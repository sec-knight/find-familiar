using FindFamiliar.Server.Domain;
using FindFamiliar.Server.Services.Demiplane;
using FindFamiliar.Server.Services.Familiar;
using FindFamiliar.Server.Services.Familiar.Reasoning;

namespace FindFamiliar.Server.Tests.Services;

/// <summary>
/// The trust table from architecture.md §5.
///
/// Validation is total and silent: every draft either becomes a fully-checked
/// <see cref="ValidatedAction"/> or becomes null. There is no third result and no error surface,
/// because a rejected draft is not an error — reporting one would teach people to read model intent
/// as system state.
/// </summary>
public sealed class ProposedActionValidatorTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly Guid KnownTaskId = Guid.NewGuid();

    [Fact]
    public void No_draft_produces_no_proposal()
    {
        Assert.Null(ProposedActionValidator.Validate([], Snapshot()));
    }

    /// <summary>
    /// Two drafts yield nothing rather than the first. A provider that proposed two actions did not
    /// understand the contract, and silently picking one would be this application choosing an
    /// action on a model's behalf.
    /// </summary>
    [Fact]
    public void More_than_one_draft_produces_no_proposal()
    {
        var drafts = new[]
        {
            new ProposedActionDraft("CreateTask", "First", "An outcome.", null),
            new ProposedActionDraft("CreateTask", "Second", "Another outcome.", null)
        };

        Assert.Null(ProposedActionValidator.Validate(drafts, Snapshot()));
        Assert.Equal(1, ProposedActionValidator.MaxActionsPerReply);
    }

    /// <summary>Model text can never select behaviour this application did not define.</summary>
    [Theory]
    [InlineData("DeleteProject")]
    [InlineData("RunCommand")]
    [InlineData("ApproveHandoff")]
    [InlineData("StartImplementer")]
    [InlineData("")]
    [InlineData("createtask")]
    [InlineData("CreateTask ")]
    public void An_unrecognised_kind_is_rejected_silently(string kind)
    {
        var draft = new ProposedActionDraft(kind, "A title", "An outcome.", KnownTaskId);

        Assert.Null(ProposedActionValidator.Validate([draft], Snapshot()));
    }

    /// <summary>The enum is closed at two. A third member would be a third thing chat can drive.</summary>
    [Fact]
    public void Exactly_two_action_kinds_exist()
    {
        Assert.Equal(
            [FamiliarActionKind.CreateTask, FamiliarActionKind.StartPlanner],
            Enum.GetValues<FamiliarActionKind>());
    }

    // ---------------------------------------------------------------- CreateTask

    [Fact]
    public void A_valid_create_task_draft_is_accepted_and_trimmed()
    {
        var draft = new ProposedActionDraft("CreateTask", "  Wire the tunnel  ", "  It should resolve.  ", null);

        var validated = ProposedActionValidator.Validate([draft], Snapshot());

        Assert.NotNull(validated);
        Assert.Equal(FamiliarActionKind.CreateTask, validated.Kind);
        Assert.Equal("Wire the tunnel", validated.Title);
        Assert.Equal("It should resolve.", validated.RequestedOutcome);
        Assert.Null(validated.TargetTaskId);
    }

    [Theory]
    [InlineData(null, "An outcome.")]
    [InlineData("", "An outcome.")]
    [InlineData("   ", "An outcome.")]
    [InlineData("A title", null)]
    [InlineData("A title", "")]
    [InlineData("A title", "   ")]
    public void A_create_task_draft_missing_a_required_field_is_rejected(string? title, string? outcome)
    {
        var draft = new ProposedActionDraft("CreateTask", title, outcome, null);

        Assert.Null(ProposedActionValidator.Validate([draft], Snapshot()));
    }

    [Fact]
    public void An_over_length_create_task_draft_is_rejected()
    {
        var longTitle = new string('t', FamiliarActionProposal.MaxTitleLength + 1);
        var longOutcome = new string('o', FamiliarActionProposal.MaxRequestedOutcomeLength + 1);

        Assert.Null(ProposedActionValidator.Validate(
            [new ProposedActionDraft("CreateTask", longTitle, "An outcome.", null)], Snapshot()));

        Assert.Null(ProposedActionValidator.Validate(
            [new ProposedActionDraft("CreateTask", "A title", longOutcome, null)], Snapshot()));
    }

    [Fact]
    public void A_create_task_draft_at_exactly_the_bounds_is_accepted()
    {
        var draft = new ProposedActionDraft(
            "CreateTask",
            new string('t', FamiliarActionProposal.MaxTitleLength),
            new string('o', FamiliarActionProposal.MaxRequestedOutcomeLength),
            null);

        Assert.NotNull(ProposedActionValidator.Validate([draft], Snapshot()));
    }

    /// <summary>A task id on a CreateTask is meaningless and is dropped rather than carried.</summary>
    [Fact]
    public void A_create_task_draft_never_carries_a_target_task()
    {
        var draft = new ProposedActionDraft("CreateTask", "A title", "An outcome.", KnownTaskId);

        Assert.Null(ProposedActionValidator.Validate([draft], Snapshot())!.TargetTaskId);
    }

    // ---------------------------------------------------------------- StartPlanner

    [Fact]
    public void A_start_planner_draft_targeting_a_snapshot_task_is_accepted()
    {
        var draft = new ProposedActionDraft("StartPlanner", null, null, KnownTaskId);

        var validated = ProposedActionValidator.Validate([draft], Snapshot());

        Assert.NotNull(validated);
        Assert.Equal(FamiliarActionKind.StartPlanner, validated.Kind);
        Assert.Equal(KnownTaskId, validated.TargetTaskId);
    }

    /// <summary>
    /// The central rule for this kind: the target must be a task the provider was actually shown.
    /// A task from another project is by construction absent from this project's snapshot, so this
    /// covers cross-project targeting and invention with the same check.
    /// </summary>
    [Fact]
    public void A_start_planner_draft_targeting_a_task_outside_the_snapshot_is_rejected()
    {
        var foreign = new ProposedActionDraft("StartPlanner", null, null, Guid.NewGuid());

        Assert.Null(ProposedActionValidator.Validate([foreign], Snapshot()));
    }

    [Fact]
    public void A_start_planner_draft_with_no_target_is_rejected()
    {
        Assert.Null(ProposedActionValidator.Validate(
            [new ProposedActionDraft("StartPlanner", null, null, null)], Snapshot()));

        Assert.Null(ProposedActionValidator.Validate(
            [new ProposedActionDraft("StartPlanner", null, null, Guid.Empty)], Snapshot()));
    }

    /// <summary>Provider prose has no place on a row whose rendering reads the task instead.</summary>
    [Fact]
    public void A_start_planner_draft_never_carries_provider_prose()
    {
        var draft = new ProposedActionDraft("StartPlanner", "Ignore me", "Ignore me too", KnownTaskId);

        var validated = ProposedActionValidator.Validate([draft], Snapshot());

        Assert.Null(validated!.Title);
        Assert.Null(validated.RequestedOutcome);
    }

    // ---------------------------------------------------------------- helpers

    private static ProjectSnapshot Snapshot() => new(
        ProjectId,
        "A project",
        "Purpose.",
        false,
        ProjectStatus.Active,
        7,
        [
            new SnapshotTask(
                KnownTaskId,
                "Cloudflare tunnel",
                TaskDisplayState.Blocked,
                TaskDisplayReasonCode.NoWorkerForRole,
                "No enabled worker declares Implementer.",
                true,
                null,
                null,
                false,
                null)
        ],
        [], [], [],
        new SnapshotHealth(1, [], 1, false),
        [],
        new SnapshotWorkforce(0, [], 0, 0, 0),
        [],
        1_000,
        true,
        DateTimeOffset.UnixEpoch);
}
