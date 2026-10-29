using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationDeploymentOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeploymentState",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CertificateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BootstrapSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BootstrapIssuedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BootstrapExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    BootstrapConsumedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentState", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentState",
                schema: "organization");
        }
    }
}
