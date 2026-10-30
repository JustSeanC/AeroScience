using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationLiveSharingAndEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Events",
                schema: "organization",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Authority = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRevision = table.Column<int>(type: "integer", nullable: true),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: true),
                    CardId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequirementKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Visibility = table.Column<int>(type: "integer", nullable: false),
                    Meaning = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "LiveConsents",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PolicyRevision = table.Column<int>(type: "integer", nullable: false),
                    LastSequence = table.Column<long>(type: "bigint", nullable: false),
                    LastUpdateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayLabel = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ClaimsJson = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    FlightStartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveConsents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveConsents_DeviceGrants_GrantId",
                        column: x => x.GrantId,
                        principalSchema: "organization",
                        principalTable: "DeviceGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LivePolicies",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    RestrictedCampaignsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LivePolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_ActorId_Sequence",
                schema: "organization",
                table: "Events",
                columns: new[] { "ActorId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_CampaignId_Sequence",
                schema: "organization",
                table: "Events",
                columns: new[] { "CampaignId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Id",
                schema: "organization",
                table: "Events",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_IdempotencyKey",
                schema: "organization",
                table: "Events",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Events_SubjectId_Sequence",
                schema: "organization",
                table: "Events",
                columns: new[] { "SubjectId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Visibility_Sequence",
                schema: "organization",
                table: "Events",
                columns: new[] { "Visibility", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveConsents_EndedUtc_StartedUtc",
                schema: "organization",
                table: "LiveConsents",
                columns: new[] { "EndedUtc", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveConsents_GrantId_FlightId_EndedUtc",
                schema: "organization",
                table: "LiveConsents",
                columns: new[] { "GrantId", "FlightId", "EndedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveConsents_InstanceId_EndedUtc_LastSeenUtc",
                schema: "organization",
                table: "LiveConsents",
                columns: new[] { "InstanceId", "EndedUtc", "LastSeenUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveConsents_PilotId_FlightId_InstanceId_EndedUtc",
                schema: "organization",
                table: "LiveConsents",
                columns: new[] { "PilotId", "FlightId", "InstanceId", "EndedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LiveConsents_PilotId_RequestId",
                schema: "organization",
                table: "LiveConsents",
                columns: new[] { "PilotId", "RequestId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Events",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "LiveConsents",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "LivePolicies",
                schema: "organization");
        }
    }
}
