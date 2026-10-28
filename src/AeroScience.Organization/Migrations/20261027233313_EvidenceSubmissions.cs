using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class EvidenceSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CancelledSubmissions",
                schema: "organization",
                columns: table => new
                {
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "text", nullable: false),
                    CancelledUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CancelledSubmissions", x => new { x.PilotId, x.Id });
                    table.ForeignKey(
                        name: "FK_CancelledSubmissions_AspNetUsers_PilotId",
                        column: x => x.PilotId,
                        principalSchema: "organization",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionPolicies",
                schema: "organization",
                columns: table => new
                {
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    PublishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionPolicies", x => new { x.OpportunityId, x.Revision });
                    table.ForeignKey(
                        name: "FK_SubmissionPolicies_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "organization",
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Submissions",
                schema: "organization",
                columns: table => new
                {
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SearchName = table.Column<string>(type: "text", nullable: false),
                    ManifestSha256 = table.Column<string>(type: "text", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "text", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    ReceiptJson = table.Column<string>(type: "text", nullable: false),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Submissions", x => new { x.PilotId, x.Id });
                    table.ForeignKey(
                        name: "FK_Submissions_AspNetUsers_PilotId",
                        column: x => x.PilotId,
                        principalSchema: "organization",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Submissions_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "organization",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubmissionUsage",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PayloadBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubmissionUsage", x => x.Id);
                    table.CheckConstraint("single_submission_usage", "\"Id\" = 1 AND \"PayloadBytes\" >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_CampaignId_ReceivedUtc_Id",
                schema: "organization",
                table: "Submissions",
                columns: new[] { "CampaignId", "ReceivedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ReceiptId",
                schema: "organization",
                table: "Submissions",
                column: "ReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_ReceivedUtc_Id",
                schema: "organization",
                table: "Submissions",
                columns: new[] { "ReceivedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Submissions_SearchName_ReceivedUtc_Id",
                schema: "organization",
                table: "Submissions",
                columns: new[] { "SearchName", "ReceivedUtc", "Id" });
            // Safe default is a new submission policy, not a rewrite of issued test-card requirements.
            migrationBuilder.Sql("INSERT INTO organization.\"SubmissionUsage\" (\"Id\",\"PayloadBytes\") VALUES (1,0)");
            migrationBuilder.Sql("""
                INSERT INTO organization."SubmissionPolicies" ("OpportunityId","Revision","Mode","PublishedUtc","AuthorId")
                SELECT o."Id",1,0,CURRENT_TIMESTAMP,g."AdministratorId"
                FROM organization."Opportunities" o CROSS JOIN organization."Organizations" g
                WHERE o."PublishedRevision" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CancelledSubmissions",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "SubmissionPolicies",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Submissions",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "SubmissionUsage",
                schema: "organization");
        }
    }
}
