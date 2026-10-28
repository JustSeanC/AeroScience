using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class VerifiedPhaseRequirementApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkflowRequirementApprovals",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementKey = table.Column<string>(type: "text", nullable: false),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingSha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowRequirementApprovals", x => new { x.RootId, x.CardId, x.RequirementKey });
                    table.ForeignKey(
                        name: "FK_WorkflowRequirementApprovals_CampaignContributions_RootId_C~",
                        columns: x => new { x.RootId, x.CardId },
                        principalSchema: "organization",
                        principalTable: "CampaignContributions",
                        principalColumns: new[] { "RootId", "CardId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowReviewIndexes",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ReviewRevision = table.Column<int>(type: "integer", nullable: false),
                    HeadSha256 = table.Column<string>(type: "text", nullable: false),
                    Valid = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowReviewIndexes", x => x.RootId);
                    table.ForeignKey(
                        name: "FK_WorkflowReviewIndexes_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowRequirementApprovals",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "WorkflowReviewIndexes",
                schema: "organization");
        }
    }
}
