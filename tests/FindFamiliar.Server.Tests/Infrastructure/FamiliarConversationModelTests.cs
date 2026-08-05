using FindFamiliar.Server.Data;
using FindFamiliar.Server.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FindFamiliar.Server.Tests.Infrastructure;

/// <summary>
/// The configuration the conversation schema's guarantees rest on, read back off the compiled EF model.
///
/// Each of these is load-bearing somewhere else: the string enum conversions are what make the
/// filtered indexes' SQL literals match, the delete behaviours are what stop a proposal's record of
/// created work being deleted out from under it, and the absent columns are what make "no hidden
/// reasoning is persisted" a property of the database rather than a habit of the code.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public sealed class FamiliarConversationModelTests
{
    /// <summary>
    /// Column-name fragments that would each mean this schema had grown a place to store something it
    /// promised never to keep: a prompt, hidden reasoning, a raw provider payload, or a secret.
    ///
    /// Deliberately narrow. "Content", "Title" and "Label" are the user-visible fields this feature
    /// exists to store, and a test that rejected them would be reworded rather than obeyed.
    /// </summary>
    private static readonly string[] ForbiddenColumnFragments =
    [
        "Prompt",
        "SystemPrompt",
        "BehaviorContract",
        "Thinking",
        "Reasoning",
        "ChainOfThought",
        "RawRequest",
        "RawResponse",
        "ProviderPayload",
        "Exception",
        "StackTrace",
        "ApiKey",
        "Credential",
        "Secret"
    ];

    [Fact]
    public async Task One_conversation_per_project_is_a_unique_index()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var index = SingleIndexOn<FamiliarConversation>(dbContext, nameof(FamiliarConversation.ProjectId));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public async Task Message_ordering_is_unique_within_a_conversation()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var index = dbContext.Model
            .FindEntityType(typeof(FamiliarMessage))!
            .GetIndexes()
            .Single(candidate => candidate.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(FamiliarMessage.ConversationId), nameof(FamiliarMessage.Sequence)]));

        Assert.True(index.IsUnique);
    }

    [Theory]
    [InlineData(typeof(FamiliarMessage), nameof(FamiliarMessage.Author))]
    [InlineData(typeof(FamiliarMessage), nameof(FamiliarMessage.Delivery))]
    [InlineData(typeof(FamiliarEvidence), nameof(FamiliarEvidence.Kind))]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.Kind))]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.Status))]
    public async Task Every_enum_is_stored_as_text_capped_at_thirty_two_characters(Type entityType, string propertyName)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var property = dbContext.Model.FindEntityType(entityType)!.FindProperty(propertyName)!;

        // TEXT, not INTEGER: IX_FamiliarActionProposals_ConversationId_Pending filters on the literal
        // 'Pending', and a stored integer would make that filter match nothing at all.
        Assert.Equal("TEXT", property.GetColumnType());
        Assert.Equal(typeof(string), property.GetProviderClrType());
        Assert.Equal(32, property.GetMaxLength());
    }

    [Fact]
    public async Task Text_lengths_match_the_specification()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var message = dbContext.Model.FindEntityType(typeof(FamiliarMessage))!;
        Assert.Equal(8_000, message.FindProperty(nameof(FamiliarMessage.Content))!.GetMaxLength());
        Assert.False(message.FindProperty(nameof(FamiliarMessage.Content))!.IsNullable);
        Assert.Equal(120, message.FindProperty(nameof(FamiliarMessage.ProviderName))!.GetMaxLength());
        Assert.Equal(120, message.FindProperty(nameof(FamiliarMessage.ProviderModel))!.GetMaxLength());
        Assert.Equal(64, message.FindProperty(nameof(FamiliarMessage.FailureCode))!.GetMaxLength());

        // Nullable on purpose. A degraded or failed turn is a real outcome with no provider metadata,
        // and a NOT NULL here would reject the honest row and push the decision into the schema,
        // where the conversation service cannot see it.
        Assert.True(message.FindProperty(nameof(FamiliarMessage.ProviderName))!.IsNullable);
        Assert.True(message.FindProperty(nameof(FamiliarMessage.ProviderModel))!.IsNullable);
        Assert.True(message.FindProperty(nameof(FamiliarMessage.LatencyMs))!.IsNullable);
        Assert.True(message.FindProperty(nameof(FamiliarMessage.FailureCode))!.IsNullable);

        var evidence = dbContext.Model.FindEntityType(typeof(FamiliarEvidence))!;
        Assert.Equal(200, evidence.FindProperty(nameof(FamiliarEvidence.Label))!.GetMaxLength());
        Assert.False(evidence.FindProperty(nameof(FamiliarEvidence.Label))!.IsNullable);

        var proposal = dbContext.Model.FindEntityType(typeof(FamiliarActionProposal))!;
        Assert.Equal(200, proposal.FindProperty(nameof(FamiliarActionProposal.Title))!.GetMaxLength());
        Assert.Equal(4_000, proposal.FindProperty(nameof(FamiliarActionProposal.RequestedOutcome))!.GetMaxLength());
        Assert.True(proposal.FindProperty(nameof(FamiliarActionProposal.Title))!.IsNullable);
        Assert.True(proposal.FindProperty(nameof(FamiliarActionProposal.RequestedOutcome))!.IsNullable);
        Assert.False(proposal.FindProperty(nameof(FamiliarActionProposal.ConcurrencyToken))!.IsNullable);
        Assert.False(proposal.FindProperty(nameof(FamiliarActionProposal.ObservedContextRevision))!.IsNullable);
    }

    [Theory]
    [InlineData(typeof(FamiliarConversation), nameof(FamiliarConversation.ProjectId), DeleteBehavior.Cascade)]
    [InlineData(typeof(FamiliarMessage), nameof(FamiliarMessage.ConversationId), DeleteBehavior.Cascade)]
    [InlineData(typeof(FamiliarEvidence), nameof(FamiliarEvidence.MessageId), DeleteBehavior.Cascade)]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.ConversationId), DeleteBehavior.Cascade)]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.ProjectId), DeleteBehavior.Cascade)]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.MessageId), DeleteBehavior.Cascade)]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.TargetTaskId), DeleteBehavior.Restrict)]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.CreatedTaskId), DeleteBehavior.Restrict)]
    [InlineData(typeof(FamiliarActionProposal), nameof(FamiliarActionProposal.CreatedSessionId), DeleteBehavior.Restrict)]
    public async Task Delete_behaviour_matches_the_specification(
        Type entityType,
        string foreignKeyProperty,
        DeleteBehavior expected)
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var foreignKey = dbContext.Model
            .FindEntityType(entityType)!
            .GetForeignKeys()
            .Single(candidate => candidate.Properties.Single().Name == foreignKeyProperty);

        Assert.Equal(expected, foreignKey.DeleteBehavior);
    }

    [Fact]
    public async Task Evidence_carries_no_foreign_key_for_its_reference()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var entity = dbContext.Model.FindEntityType(typeof(FamiliarEvidence))!;

        // One FK only, to the message. Four nullable FKs — one per evidence kind — would encode the
        // same fact four ways and still not express the guarantee that matters: the id was present in
        // the snapshot that produced the message.
        var foreignKey = Assert.Single(entity.GetForeignKeys());
        Assert.Equal(nameof(FamiliarEvidence.MessageId), foreignKey.Properties.Single().Name);
    }

    [Fact]
    public void There_are_exactly_two_action_kinds()
    {
        // A third member, or an Unknown placeholder, would give an unparseable kind somewhere to be
        // stored instead of being dropped.
        Assert.Equal(
            [FamiliarActionKind.CreateTask, FamiliarActionKind.StartPlanner],
            Enum.GetValues<FamiliarActionKind>());
    }

    [Fact]
    public async Task No_column_in_the_model_can_hold_a_prompt_hidden_reasoning_or_a_secret()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        var columns = dbContext.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetProperties()
                .Select(property => $"{entity.GetTableName()}.{property.GetColumnName()}"))
            .ToList();

        AssertNoForbiddenColumn(columns);
    }

    [Fact]
    public async Task No_column_in_the_migrated_database_can_hold_a_prompt_hidden_reasoning_or_a_secret()
    {
        using var database = new TemporarySqliteDatabase();
        await using var dbContext = await database.CreateContextAsync();

        // The model and the database are asserted separately on purpose: a migration can create a
        // column the model does not describe, and only the file says what actually ships.
        AssertNoForbiddenColumn(SqliteSchemaReader.QualifiedColumnNames(database.ConnectionString));
    }

    private static void AssertNoForbiddenColumn(IReadOnlyList<string> qualifiedColumnNames)
    {
        foreach (var column in qualifiedColumnNames)
        {
            var columnName = column[(column.IndexOf('.') + 1)..];

            foreach (var fragment in ForbiddenColumnFragments)
            {
                Assert.False(
                    columnName.Contains(fragment, StringComparison.OrdinalIgnoreCase),
                    $"{column} suggests storage for {fragment}, which this schema must never hold.");
            }
        }
    }

    private static IIndex SingleIndexOn<TEntity>(FamiliarDbContext dbContext, string propertyName) =>
        dbContext.Model
            .FindEntityType(typeof(TEntity))!
            .GetIndexes()
            .Single(index => index.Properties.Count == 1 && index.Properties[0].Name == propertyName);


}
