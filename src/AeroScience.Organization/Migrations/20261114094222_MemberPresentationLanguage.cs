using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class MemberPresentationLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PresentationLocale",
                schema: "organization",
                table: "AspNetUsers",
                type: "character varying(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PresentationRevision",
                schema: "organization",
                table: "AspNetUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PresentationLocale",
                schema: "organization",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PresentationRevision",
                schema: "organization",
                table: "AspNetUsers");
        }
    }
}
