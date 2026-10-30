using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class OwnerPairing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerBindingSha256",
                schema: "organization",
                table: "DeploymentState",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "OwnerSetups",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChallengeJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerSetups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSetups_ConsumedUtc_ExpiresUtc",
                schema: "organization",
                table: "OwnerSetups",
                columns: new[] { "ConsumedUtc", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerSetups",
                schema: "organization");

            migrationBuilder.DropColumn(
                name: "OwnerBindingSha256",
                schema: "organization",
                table: "DeploymentState");
        }
    }
}
