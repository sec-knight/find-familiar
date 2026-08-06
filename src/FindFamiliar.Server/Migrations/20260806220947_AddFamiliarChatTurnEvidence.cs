using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// What a reply was actually shown, so its citations can be checked (Sprint 13, slice 2).
    ///
    /// One nullable column, no table rebuilt, no existing row read or written. Turns written before
    /// this migration keep a null, which reads correctly as "nothing was offered": they were answered
    /// before retrieval existed, so any id in them was never in a pack and is not a source.
    ///
    /// Ids only, space separated. Not the titles, not the content, not the query — those are rows in
    /// this same database, read back through a query that re-checks sensitivity every time a
    /// transcript is displayed. Storing the text here would have frozen a copy of context that could
    /// then drift from the entry, and would have put content that might later be marked sensitive into
    /// a row nothing would ever revisit.
    ///
    /// Still no column for a prompt, a standing brief, a raw payload or a provider exception.
    /// </summary>
    public partial class AddFamiliarChatTurnEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceEntryIds",
                table: "FamiliarChatTurns",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        /// <summary>
        /// Raw ALTER TABLE rather than <c>migrationBuilder.DropColumn</c>, for the reason recorded on
        /// <c>SensitivityAndCachedTokens</c>: EF's SQLite provider implements DropColumn by rebuilding
        /// the table, which reorders columns and leaves a schema that no longer matches the one a
        /// forward migration produces. SQLite has supported DROP COLUMN natively since 3.35.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"FamiliarChatTurns\" DROP COLUMN \"EvidenceEntryIds\";");
        }
    }
}
