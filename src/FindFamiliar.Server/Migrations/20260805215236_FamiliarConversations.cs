using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FindFamiliar.Server.Migrations
{
    /// <summary>
    /// Adds the four tables the conversational Familiar needs: one conversation per project, its
    /// append-only messages, server-composed evidence for those messages, and the action proposals a
    /// human may confirm.
    ///
    /// Additive only. No existing table is altered and no existing row is read or written — a project
    /// with no conversation has no conversation, which is the truth, so nothing is backfilled and no
    /// default conversation row is created. <c>Down</c> is therefore a clean reversal: dropping the
    /// four tables leaves the Sprint 10 schema exactly as it was.
    ///
    /// No column here holds a prompt, a behaviour contract, a thinking block, a raw provider payload,
    /// an exception, or a credential. That absence is the schema-level half of the rule that model
    /// output is inert data: there is nowhere for hidden reasoning to be written, and
    /// <see cref="Domain.FamiliarActionKind"/> is a closed two-member enum, so no column lets provider
    /// text select executable behaviour.
    /// </summary>
    public partial class FamiliarConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FamiliarConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarConversations_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FamiliarMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", maxLength: 8000, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ProviderName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ProviderModel = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    LatencyMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Delivery = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarMessages_FamiliarConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "FamiliarConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FamiliarActionProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConcurrencyToken = table.Column<Guid>(type: "TEXT", nullable: false),
                    ObservedContextRevision = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RequestedOutcome = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    TargetTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedSessionId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarActionProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarActionProposals_AgentSessions_CreatedSessionId",
                        column: x => x.CreatedSessionId,
                        principalTable: "AgentSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FamiliarActionProposals_FamiliarConversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "FamiliarConversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FamiliarActionProposals_FamiliarMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "FamiliarMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FamiliarActionProposals_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FamiliarActionProposals_Tasks_CreatedTaskId",
                        column: x => x.CreatedTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FamiliarActionProposals_Tasks_TargetTaskId",
                        column: x => x.TargetTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FamiliarEvidence",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FamiliarEvidence", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FamiliarEvidence_FamiliarMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "FamiliarMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarActionProposals_ConversationId_Pending",
                table: "FamiliarActionProposals",
                column: "ConversationId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarActionProposals_CreatedSessionId",
                table: "FamiliarActionProposals",
                column: "CreatedSessionId",
                unique: true,
                filter: "\"CreatedSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarActionProposals_CreatedTaskId",
                table: "FamiliarActionProposals",
                column: "CreatedTaskId",
                unique: true,
                filter: "\"CreatedTaskId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarActionProposals_MessageId",
                table: "FamiliarActionProposals",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarActionProposals_ProjectId",
                table: "FamiliarActionProposals",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarActionProposals_TargetTaskId",
                table: "FamiliarActionProposals",
                column: "TargetTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarConversations_ProjectId",
                table: "FamiliarConversations",
                column: "ProjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarEvidence_MessageId",
                table: "FamiliarEvidence",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_FamiliarMessages_ConversationId_Sequence",
                table: "FamiliarMessages",
                columns: new[] { "ConversationId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FamiliarActionProposals");

            migrationBuilder.DropTable(
                name: "FamiliarEvidence");

            migrationBuilder.DropTable(
                name: "FamiliarMessages");

            migrationBuilder.DropTable(
                name: "FamiliarConversations");
        }
    }
}
