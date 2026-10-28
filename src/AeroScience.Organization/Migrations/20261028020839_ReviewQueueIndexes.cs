using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class ReviewQueueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reviews_SearchName_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SearchCampaign",
                schema: "organization",
                table: "Reviews",
                column: "SearchCampaign")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SearchName",
                schema: "organization",
                table: "Reviews",
                column: "SearchName")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SearchPilot",
                schema: "organization",
                table: "Reviews",
                column: "SearchPilot")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SearchProgram",
                schema: "organization",
                table: "Reviews",
                column: "SearchProgram")
                .Annotation("Npgsql:IndexOperators", new[] { "text_pattern_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "UpdatedUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reviews_SearchCampaign",
                schema: "organization",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_SearchName",
                schema: "organization",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_SearchPilot",
                schema: "organization",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_SearchProgram",
                schema: "organization",
                table: "Reviews");

            migrationBuilder.DropIndex(
                name: "IX_Reviews_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews");

            migrationBuilder.CreateIndex(
                name: "IX_Reviews_SearchName_UpdatedUtc_Id",
                schema: "organization",
                table: "Reviews",
                columns: new[] { "SearchName", "UpdatedUtc", "Id" });
        }
    }
}
