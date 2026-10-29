using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class IndependentRecoveryAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "RepositorySha256", schema: "organization", table: "ServerBackups", type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(name: "IndependentRepositorySha256", schema: "organization", table: "ServerBackups", type: "text", nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "AutomaticRetention",
                schema: "organization",
                table: "ServerOperationsPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedUtc",
                schema: "organization",
                table: "ServerBackups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IndependentSnapshotId",
                schema: "organization",
                table: "ServerBackups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IndependentVerifiedUtc",
                schema: "organization",
                table: "ServerBackups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetentionStartedUtc",
                schema: "organization",
                table: "ServerBackups",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RepositorySha256", schema: "organization", table: "ServerBackups");
            migrationBuilder.DropColumn(name: "IndependentRepositorySha256", schema: "organization", table: "ServerBackups");
            migrationBuilder.DropColumn(
                name: "AutomaticRetention",
                schema: "organization",
                table: "ServerOperationsPolicies");

            migrationBuilder.DropColumn(
                name: "DeletedUtc",
                schema: "organization",
                table: "ServerBackups");

            migrationBuilder.DropColumn(
                name: "IndependentSnapshotId",
                schema: "organization",
                table: "ServerBackups");

            migrationBuilder.DropColumn(
                name: "IndependentVerifiedUtc",
                schema: "organization",
                table: "ServerBackups");

            migrationBuilder.DropColumn(
                name: "RetentionStartedUtc",
                schema: "organization",
                table: "ServerBackups");
        }
    }
}
