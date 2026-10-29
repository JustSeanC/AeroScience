using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationAccountSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AuthenticatorLastStep",
                schema: "organization",
                table: "AspNetUsers",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RecoveryAcknowledgedUtc",
                schema: "organization",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthenticatorLastStep",
                schema: "organization",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RecoveryAcknowledgedUtc",
                schema: "organization",
                table: "AspNetUsers");
        }
    }
}
