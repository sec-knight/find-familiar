using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// The sensitivity boundary, and the measurement that says whether prompt caching works
    /// (Sprint 12, slice 3).
    ///
    /// Three nullable-or-defaulted columns, no table rebuilt, no existing row read or written.
    ///
    /// <c>IsSensitive</c> on projects and context entries defaults to false, which is the honest
    /// default: nothing that exists today was created under a promise of confidentiality, and
    /// defaulting to true would lock an operator out of their own records to make a migration look
    /// cautious. The flag is a column rather than a convention because the standing brief is the first
    /// thing in this system that sends project state off the machine, and a rule enforced by a schema
    /// survives a contributor who has never read the ADR.
    ///
    /// <c>CachedInputTokens</c> is nullable and stays null when a provider does not report caching —
    /// which is not the same as reporting zero, and the dashboard says "not reported" rather than
    /// implying a cache miss it never observed.
    ///
    /// Still no column for a prompt, a standing brief, a raw payload or a provider exception. This is
    /// the migration that adds the brief to the wire, so it is exactly the one where such a column
    /// would have seemed reasonable: the brief is composed per turn from live records and is not
    /// stored, because storing it would be storing a copy of project state that could then drift from
    /// the project.
    /// </summary>
    public partial class SensitivityAndCachedTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSensitive",
                table: "Projects",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CachedInputTokens",
                table: "FamiliarChatTurns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSensitive",
                table: "ContextEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        /// <summary>
        /// Raw ALTER TABLE rather than <c>migrationBuilder.DropColumn</c>, deliberately.
        ///
        /// EF's SQLite provider implements DropColumn by rebuilding the table: create a new one,
        /// copy the rows, drop the old, rename. That works, but the rebuilt table's DDL is not the
        /// DDL it started with — the surviving columns come back in a different order — so a Down
        /// that used it would leave a schema that is equivalent but not identical, and the migration
        /// tests compare stored DDL byte for byte on purpose.
        ///
        /// SQLite has supported native DROP COLUMN since 3.35 (2021), it preserves the remaining
        /// column order, and it is legal here because none of these three columns is indexed, part of
        /// a key, or referenced by a constraint. A rollback is therefore an exact reversal rather than
        /// an approximate one.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Projects"" DROP COLUMN ""IsSensitive"";");
            migrationBuilder.Sql(@"ALTER TABLE ""FamiliarChatTurns"" DROP COLUMN ""CachedInputTokens"";");
            migrationBuilder.Sql(@"ALTER TABLE ""ContextEntries"" DROP COLUMN ""IsSensitive"";");
        }
    }
}
