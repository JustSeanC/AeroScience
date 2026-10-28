using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class ResearchReviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReviewGrants",
                schema: "organization",
                columns: table => new
                {
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReviewerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Allowed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewGrants", x => new { x.CampaignId, x.ReviewerId });
                    table.ForeignKey(
                        name: "FK_ReviewGrants_AspNetUsers_ReviewerId",
                        column: x => x.ReviewerId,
                        principalSchema: "organization",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewGrants_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "organization",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Reviews",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProgramId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotName = table.Column<string>(type: "text", nullable: false),
                    ProgramName = table.Column<string>(type: "text", nullable: false),
                    CampaignName = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: false),
                    CardNames = table.Column<string>(type: "text", nullable: false),
                    SearchName = table.Column<string>(type: "text", nullable: false),
                    SearchPilot = table.Column<string>(type: "text", nullable: false),
                    SearchCampaign = table.Column<string>(type: "text", nullable: false),
                    SearchProgram = table.Column<string>(type: "text", nullable: false),
                    RootPayloadSha256 = table.Column<string>(type: "text", nullable: false),
                    Completeness = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    HeadSha256 = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PackageCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Reviews_Submissions_PilotId_SubmissionId",
                        columns: x => new { x.PilotId, x.SubmissionId },
                        principalSchema: "organization",
                        principalTable: "Submissions",
                        principalColumns: new[] { "PilotId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewEvents",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewEvents", x => new { x.RootId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_ReviewEvents_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewPackages",
                schema: "organization",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "text", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewPackages", x => x.ReceiptId);
                    table.ForeignKey(
                        name: "FK_ReviewPackages_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewPackages_Submissions_PilotId_SubmissionId",
                        columns: x => new { x.PilotId, x.SubmissionId },
                        principalSchema: "organization",
                        principalTable: "Submissions",
                        principalColumns: new[] { "PilotId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewRequests",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementKey = table.Column<string>(type: "text", nullable: false),
                    TargetJson = table.Column<string>(type: "text", nullable: false),
                    Note = table.Column<string>(type: "text", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorName = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReviewRequests_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReviewRequirements",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    DefinitionJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    FindingJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewRequirements", x => new { x.RootId, x.CardId, x.Key });
                    table.ForeignKey(
                        name: "FK_ReviewRequirements_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewEvents_Id",
                schema: "organization",
                table: "ReviewEvents",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewGrants_ReviewerId_Allowed_CampaignId",
                schema: "organization",
                table: "ReviewGrants",
                columns: new[] { "ReviewerId", "Allowed", "CampaignId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewPackages_PilotId_SubmissionId",
                schema: "organization",
                table: "ReviewPackages",
                columns: new[] { "PilotId", "SubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewPackages_RootId_ReceivedUtc_ReceiptId",
                schema: "organization",
                table: "ReviewPackages",
                columns: new[] { "RootId", "ReceivedUtc", "ReceiptId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReviewRequests_RootId_State_CreatedUtc_Id",
                schema: "organization",
                table: "ReviewRequests",
                columns: new[] { "RootId", "State", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_CampaignId_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "CampaignId", "UpdatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_PilotId_SubmissionId",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "PilotId", "SubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_PilotId_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "PilotId", "UpdatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SearchName_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "SearchName", "UpdatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_State_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "State", "UpdatedUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReviewEvents",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ReviewGrants",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ReviewPackages",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ReviewRequests",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ReviewRequirements",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Reviews",
                schema: "organization");
        }
    }
}
