using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class CampaignScientificCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CoverageGoals",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SearchName = table.Column<string>(type: "text", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverageGoals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CoverageGoals_Campaigns_CampaignId",
                        column: x => x.CampaignId,
                        principalSchema: "organization",
                        principalTable: "Campaigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CoverageIndexes",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ReviewRevision = table.Column<int>(type: "integer", nullable: false),
                    HeadSha256 = table.Column<string>(type: "text", nullable: false),
                    ApprovalSignature = table.Column<string>(type: "text", nullable: false),
                    Valid = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverageIndexes", x => x.RootId);
                    table.ForeignKey(
                        name: "FK_CoverageIndexes_Reviews_RootId",
                        column: x => x.RootId,
                        principalSchema: "organization",
                        principalTable: "Reviews",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CoverageObservations",
                schema: "organization",
                columns: table => new
                {
                    RootId = table.Column<Guid>(type: "uuid", nullable: false),
                    CardId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequirementKey = table.Column<string>(type: "text", nullable: false),
                    IdentitySha256 = table.Column<string>(type: "text", nullable: false),
                    ContentSha256 = table.Column<string>(type: "text", nullable: false),
                    CampaignId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicationRevision = table.Column<int>(type: "integer", nullable: false),
                    PublicationSha256 = table.Column<string>(type: "text", nullable: false),
                    ReferenceId = table.Column<string>(type: "text", nullable: false),
                    CompatibilityId = table.Column<string>(type: "text", nullable: false),
                    FlightId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlightReference = table.Column<string>(type: "text", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Contributor = table.Column<string>(type: "text", nullable: false),
                    Variable = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Instrument = table.Column<string>(type: "text", nullable: false),
                    Unit = table.Column<string>(type: "text", nullable: false),
                    Provenance = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    SourceSemanticsSha256 = table.Column<string>(type: "text", nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AltitudeFeetMsl = table.Column<double>(type: "double precision", nullable: true),
                    Latitude = table.Column<double>(type: "double precision", nullable: true),
                    Longitude = table.Column<double>(type: "double precision", nullable: true),
                    OriginalReferenceJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoverageObservations", x => new { x.RootId, x.CardId, x.RequirementKey, x.IdentitySha256 });
                    table.ForeignKey(
                        name: "FK_CoverageObservations_CampaignContributions_RootId_CardId",
                        columns: x => new { x.RootId, x.CardId },
                        principalSchema: "organization",
                        principalTable: "CampaignContributions",
                        principalColumns: new[] { "RootId", "CardId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageGoals_CampaignId_Name_Id",
                schema: "organization",
                table: "CoverageGoals",
                columns: new[] { "CampaignId", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageGoals_SearchName",
                schema: "organization",
                table: "CoverageGoals",
                column: "SearchName")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageObservations_CampaignId_CompatibilityId_IdentitySha~",
                schema: "organization",
                table: "CoverageObservations",
                columns: new[] { "CampaignId", "CompatibilityId", "IdentitySha256" });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageObservations_CampaignId_Date_FlightId",
                schema: "organization",
                table: "CoverageObservations",
                columns: new[] { "CampaignId", "Date", "FlightId" });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageObservations_CampaignId_OpportunityId_PublicationRe~",
                schema: "organization",
                table: "CoverageObservations",
                columns: new[] { "CampaignId", "OpportunityId", "PublicationRevision", "Variable", "Category", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageObservations_CompatibilityId_AltitudeFeetMsl",
                schema: "organization",
                table: "CoverageObservations",
                columns: new[] { "CompatibilityId", "AltitudeFeetMsl" });

            migrationBuilder.CreateIndex(
                name: "IX_CoverageObservations_CompatibilityId_Latitude_Longitude",
                schema: "organization",
                table: "CoverageObservations",
                columns: new[] { "CompatibilityId", "Latitude", "Longitude" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoverageGoals",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "CoverageIndexes",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "CoverageObservations",
                schema: "organization");
        }
    }
}
