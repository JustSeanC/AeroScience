using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class CampaignApprovedProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProgressIndexVersion",
                schema: "organization",
                table: "Opportunities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PublishedName",
                schema: "organization",
                table: "Opportunities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PublishedRequiredPilots",
                schema: "organization",
                table: "Opportunities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PublishedSearchName",
                schema: "organization",
                table: "Opportunities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublishedSha256",
                schema: "organization",
                table: "Opportunities",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ProgressIndexVersion",
                schema: "organization",
                table: "Campaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PublishedName",
                schema: "organization",
                table: "Campaigns",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublishedObjective",
                schema: "organization",
                table: "Campaigns",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PublishedSearchName",
                schema: "organization",
                table: "Campaigns",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CampaignContributions",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationRevision = table.Column<int>(type: "integer", nullable: false),
                    PublicationSha256 = table.Column<string>(type: "text", nullable: false),
                    DefinitionSha256 = table.Column<string>(type: "text", nullable: false),
                    AcceptancePolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    ReviewRevision = table.Column<int>(type: "integer", nullable: false),
                    SourceHeadSha256 = table.Column<string>(type: "text", nullable: false),
                    Valid = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    RequiredFindings = table.Column<int>(type: "integer", nullable: false),
                    ApprovedFindings = table.Column<int>(type: "integer", nullable: false),
                    AcceptedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SummaryJson = table.Column<string>(type: "text", nullable: false),
                    ApprovalSignature = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignContributions", x => new { x.RootId, x.CardId });
                    table.ForeignKey(
                        name: "FK_CampaignContributions_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CampaignProjections",
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
                    table.PrimaryKey("PK_CampaignProjections", x => x.RootId);
                    table.ForeignKey(
                        name: "FK_CampaignProjections_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CampaignMeasurements",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<string>(type: "text", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CampaignMeasurements", x => new { x.RootId, x.CardId, x.GroupId });
                    table.ForeignKey(
                        name: "FK_CampaignMeasurements_CampaignContributions_RootId_CardId",
                        columns: x => new { x.RootId, x.CardId },
                        principalSchema: "organization",
                        principalTable: "CampaignContributions",
                        principalColumns: new[] { "RootId", "CardId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_CampaignId_PublishedName_Id",
                schema: "organization",
                table: "Opportunities",
                columns: new[] { "CampaignId", "PublishedName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_PublishedSearchName",
                schema: "organization",
                table: "Opportunities",
                column: "PublishedSearchName")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_ProgramId_PublishedName_Id",
                schema: "organization",
                table: "Campaigns",
                columns: new[] { "ProgramId", "PublishedName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Campaigns_PublishedSearchName",
                schema: "organization",
                table: "Campaigns",
                column: "PublishedSearchName")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignContributions_CampaignId_State_AcceptedUtc_RootId_C~",
                schema: "organization",
                table: "CampaignContributions",
                columns: new[] { "CampaignId", "State", "AcceptedUtc", "RootId", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignContributions_OpportunityId_PublicationRevision_Pub~",
                schema: "organization",
                table: "CampaignContributions",
                columns: new[] { "OpportunityId", "PublicationRevision", "PublicationSha256", "State", "PilotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CampaignContributions_PilotId_CampaignId_OpportunityId",
                schema: "organization",
                table: "CampaignContributions",
                columns: new[] { "PilotId", "CampaignId", "OpportunityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CampaignMeasurements",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "CampaignProjections",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "CampaignContributions",
                schema: "organization");

            migrationBuilder.DropIndex(
                name: "IX_Opportunities_CampaignId_PublishedName_Id",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Opportunities_PublishedSearchName",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropIndex(
                name: "IX_Campaigns_ProgramId_PublishedName_Id",
                schema: "organization",
                table: "Campaigns");

            migrationBuilder.DropIndex(
                name: "IX_Campaigns_PublishedSearchName",
                schema: "organization",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "ProgressIndexVersion",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "PublishedName",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "PublishedRequiredPilots",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "PublishedSearchName",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "PublishedSha256",
                schema: "organization",
                table: "Opportunities");

            migrationBuilder.DropColumn(
                name: "ProgressIndexVersion",
                schema: "organization",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "PublishedName",
                schema: "organization",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "PublishedObjective",
                schema: "organization",
                table: "Campaigns");

            migrationBuilder.DropColumn(
                name: "PublishedSearchName",
                schema: "organization",
                table: "Campaigns");
        }
    }
}
