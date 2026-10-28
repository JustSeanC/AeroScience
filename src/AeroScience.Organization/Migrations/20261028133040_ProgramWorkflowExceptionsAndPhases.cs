using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class ProgramWorkflowExceptionsAndPhases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TestPhases",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    DefinitionRevision = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Held = table.Column<bool>(type: "boolean", nullable: false),
                    Released = table.Column<bool>(type: "boolean", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TestPhases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TestPhases_Programs_ProgramId",
                        column: x => x.ProgramId,
                        principalSchema: "organization",
                        principalTable: "Programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowExceptions",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationRevision = table.Column<int>(type: "integer", nullable: false),
                    PublicationSha256 = table.Column<string>(type: "text", nullable: false),
                    RequirementKey = table.Column<string>(type: "text", nullable: false),
                    PhaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    PhaseDefinitionRevision = table.Column<int>(type: "integer", nullable: true),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    RulesRevision = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    CountAsPartialContributor = table.Column<bool>(type: "boolean", nullable: false),
                    RootReceiptId = table.Column<Guid>(type: "uuid", nullable: true),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceReviewRevision = table.Column<int>(type: "integer", nullable: true),
                    SourceReviewHeadSha256 = table.Column<string>(type: "text", nullable: true),
                    SourceSignature = table.Column<string>(type: "text", nullable: false),
                    SourceState = table.Column<int>(type: "integer", nullable: true),
                    ScopeSha256 = table.Column<string>(type: "text", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowExceptions_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "organization",
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkflowExceptions_Programs_ProgramId",
                        column: x => x.ProgramId,
                        principalSchema: "organization",
                        principalTable: "Programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowPolicies",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    RulesRevision = table.Column<int>(type: "integer", nullable: false),
                    PhaseGatesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AdministrativeHold = table.Column<bool>(type: "boolean", nullable: false),
                    SnapshotSha256 = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowPolicies_Programs_ProgramId",
                        column: x => x.ProgramId,
                        principalSchema: "organization",
                        principalTable: "Programs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PhaseCards",
                schema: "organization",
                columns: table => new
                {
                    PhaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationRevision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhaseCards", x => new { x.PhaseId, x.OpportunityId });
                    table.ForeignKey(
                        name: "FK_PhaseCards_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "organization",
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PhaseCards_TestPhases_PhaseId",
                        column: x => x.PhaseId,
                        principalSchema: "organization",
                        principalTable: "TestPhases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhaseCards_OpportunityId",
                schema: "organization",
                table: "PhaseCards",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_TestPhases_ProgramId_Sequence",
                schema: "organization",
                table: "TestPhases",
                columns: new[] { "ProgramId", "Sequence" },
                unique: true,
                filter: "\"Enabled\"");

            migrationBuilder.CreateIndex(
                name: "IX_TestPhases_ProgramId_Sequence_Id",
                schema: "organization",
                table: "TestPhases",
                columns: new[] { "ProgramId", "Sequence", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExceptions_OpportunityId",
                schema: "organization",
                table: "WorkflowExceptions",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExceptions_ProgramId_PhaseId_OpportunityId_Publicat~",
                schema: "organization",
                table: "WorkflowExceptions",
                columns: new[] { "ProgramId", "PhaseId", "OpportunityId", "PublicationRevision", "RequirementKey", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExceptions_ProgramId_TimestampUtc_Id",
                schema: "organization",
                table: "WorkflowExceptions",
                columns: new[] { "ProgramId", "TimestampUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowExceptions_ScopeSha256",
                schema: "organization",
                table: "WorkflowExceptions",
                column: "ScopeSha256",
                unique: true,
                filter: "\"Active\"");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowPolicies_ProgramId",
                schema: "organization",
                table: "WorkflowPolicies",
                column: "ProgramId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhaseCards",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "WorkflowExceptions",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "WorkflowPolicies",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "TestPhases",
                schema: "organization");
        }
    }
}
