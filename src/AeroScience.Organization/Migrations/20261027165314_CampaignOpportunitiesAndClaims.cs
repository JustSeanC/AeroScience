using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class CampaignOpportunitiesAndClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Permission",
                schema: "organization",
                table: "DeviceGrants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Opportunities",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PublishedRevision = table.Column<int>(type: "integer", nullable: true),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    Suspended = table.Column<bool>(type: "boolean", nullable: false),
                    AvailabilityRevision = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Opportunities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Opportunities_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "organization",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Claims",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IssuedRevision = table.Column<int>(type: "integer", nullable: false),
                    IssuedSha256 = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Slot = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    AssignedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                    table.CheckConstraint("claim_lifecycle", "(\"State\" = 2) = (\"EndedUtc\" IS NOT NULL)");
                    table.CheckConstraint("claim_slot", "\"Slot\" > 0");
                    table.CheckConstraint("claim_state", "\"State\" IN (0,1,2)");
                    table.ForeignKey(
                        name: "FK_Claims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "organization",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Claims_Opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "organization",
                        principalTable: "Opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Claims_Revisions_OpportunityId_IssuedRevision",
                        columns: x => new { x.OpportunityId, x.IssuedRevision },
                        principalSchema: "organization",
                        principalTable: "Revisions",
                        principalColumns: new[] { "ResourceId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClaimEvents",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimRevision = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClaimEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClaimEvents_Claims_ClaimId",
                        column: x => x.ClaimId,
                        principalSchema: "organization",
                        principalTable: "Claims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClaimEvents_ClaimId_Id",
                schema: "organization",
                table: "ClaimEvents",
                columns: new[] { "ClaimId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_OpportunityId_AssignedUtc_Id",
                schema: "organization",
                table: "Claims",
                columns: new[] { "OpportunityId", "AssignedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_OpportunityId_IssuedRevision",
                schema: "organization",
                table: "Claims",
                columns: new[] { "OpportunityId", "IssuedRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_OpportunityId_Slot",
                schema: "organization",
                table: "Claims",
                columns: new[] { "OpportunityId", "Slot" },
                unique: true,
                filter: "\"State\" IN (0,1)");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_OpportunityId_UserId",
                schema: "organization",
                table: "Claims",
                columns: new[] { "OpportunityId", "UserId" },
                unique: true,
                filter: "\"State\" IN (0,1)");

            migrationBuilder.CreateIndex(
                name: "IX_Claims_UserId_AssignedUtc_Id",
                schema: "organization",
                table: "Claims",
                columns: new[] { "UserId", "AssignedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_CampaignId_CreatedUtc_Id",
                schema: "organization",
                table: "Opportunities",
                columns: new[] { "CampaignId", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_CreatedUtc_Id",
                schema: "organization",
                table: "Opportunities",
                columns: new[] { "CreatedUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClaimEvents",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Claims",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Opportunities",
                schema: "organization");

            migrationBuilder.DropColumn(
                name: "Permission",
                schema: "organization",
                table: "DeviceGrants");
        }
    }
}
