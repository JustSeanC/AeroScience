using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationServerOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServerBackups",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SnapshotId = table.Column<string>(type: "text", nullable: false),
                    BundleSha256 = table.Column<string>(type: "text", nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    ManifestJson = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedRestoreUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VerificationJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerBackups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerOperations",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestSha256 = table.Column<string>(type: "text", nullable: false),
                    ActorName = table.Column<string>(type: "text", nullable: false),
                    BackupId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FinishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Message = table.Column<string>(type: "text", nullable: false),
                    DistributionJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerOperations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerOperationsPolicies",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Scheduled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalHours = table.Column<int>(type: "integer", nullable: false),
                    RetainDaily = table.Column<int>(type: "integer", nullable: false),
                    RetainWeekly = table.Column<int>(type: "integer", nullable: false),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecoveryKitSavedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerOperationsPolicies", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServerBackups_CreatedUtc",
                schema: "organization",
                table: "ServerBackups",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServerBackups_SnapshotId",
                schema: "organization",
                table: "ServerBackups",
                column: "SnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerOperations_ActorId_RequestId",
                schema: "organization",
                table: "ServerOperations",
                columns: new[] { "ActorId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServerOperations_State_CreatedUtc",
                schema: "organization",
                table: "ServerOperations",
                columns: new[] { "State", "CreatedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ServerBackups",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ServerOperations",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "ServerOperationsPolicies",
                schema: "organization");
        }
    }
}
