using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class GuidedRecoveryImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProtectedImportJson",
                schema: "organization",
                table: "ServerOperations",
                type: "character varying(65536)",
                maxLength: 65536,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedImportJson",
                schema: "organization",
                table: "ServerOperations");
        }
    }
}
