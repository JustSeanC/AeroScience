using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class ResearchTemplateGovernance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicationReviews",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DraftRevision = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicationReviews", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PublishingPolicies",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublishingPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResearchTemplates",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Proposal = table.Column<bool>(type: "boolean", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PublishedRevision = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResearchTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationReviews_ResourceId_DraftRevision_Kind",
                schema: "organization",
                table: "PublicationReviews",
                columns: new[] { "ResourceId", "DraftRevision", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_PublicationReviews_Status_CreatedUtc_Id",
                schema: "organization",
                table: "PublicationReviews",
                columns: new[] { "Status", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchTemplates_OwnerId_Proposal_CreatedUtc_Id",
                schema: "organization",
                table: "ResearchTemplates",
                columns: new[] { "OwnerId", "Proposal", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchTemplates_Proposal_CreatedUtc_Id",
                schema: "organization",
                table: "ResearchTemplates",
                columns: new[] { "Proposal", "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ResearchTemplates_Proposal_PublishedRevision",
                schema: "organization",
                table: "ResearchTemplates",
                columns: new[] { "Proposal", "PublishedRevision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicationReviews",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "PublishingPolicies",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ResearchTemplates",
                schema: "organization");
        }
    }
}
