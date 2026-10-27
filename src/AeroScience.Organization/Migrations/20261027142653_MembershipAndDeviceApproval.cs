using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class MembershipAndDeviceApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceGrants",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientName = table.Column<string>(type: "text", nullable: false),
                    DeviceHash = table.Column<string>(type: "text", nullable: false),
                    Challenge = table.Column<string>(type: "text", nullable: false),
                    UserCode = table.Column<string>(type: "text", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextPollUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    CredentialHash = table.Column<string>(type: "text", nullable: true),
                    CredentialExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EnrollmentPolicies",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    RequireApproval = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnrollmentPolicies", x => x.Id);
                    table.CheckConstraint("single_enrollment_policy", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretHash = table.Column<string>(type: "text", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RedeemedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Memberships",
                schema: "organization",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Statement = table.Column<string>(type: "text", nullable: false),
                    EnrollmentRevision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Memberships", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_Memberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "organization",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Additive P13 upgrade: preserve existing authorized accounts, bootstrap identity and history.
            migrationBuilder.Sql("""
                INSERT INTO organization."EnrollmentPolicies" ("Id","Revision","Mode","RequireApproval") VALUES (1,1,0,true);
                INSERT INTO organization."Revisions" ("ResourceId","Revision","Kind","SnapshotJson","Sha256","TimestampUtc","AuthorId")
                SELECT "PublicId",1,'enrollment-policy','{"version":1,"revision":1,"mode":"invitationOnly","requireApproval":true}',
                encode(sha256(convert_to('{"version":1,"revision":1,"mode":"invitationOnly","requireApproval":true}','UTF8')),'hex'),CURRENT_TIMESTAMP,"AdministratorId"
                FROM organization."Organizations";
                INSERT INTO organization."Memberships" ("UserId","Role","State","Revision","UpdatedUtc","Statement","EnrollmentRevision")
                SELECT DISTINCT ON (u."Id") u."Id",r."Name",1,1,CURRENT_TIMESTAMP,'',1
                FROM organization."AspNetUsers" u
                JOIN organization."AspNetUserRoles" ur ON ur."UserId"=u."Id"
                JOIN organization."AspNetRoles" r ON r."Id"=ur."RoleId"
                WHERE r."Name" IN ('Administrator','Manager','Reviewer','Pilot')
                ORDER BY u."Id",CASE r."Name" WHEN 'Administrator' THEN 0 WHEN 'Manager' THEN 1 WHEN 'Reviewer' THEN 2 ELSE 3 END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGrants_CredentialHash",
                schema: "organization",
                table: "DeviceGrants",
                column: "CredentialHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGrants_UserId",
                schema: "organization",
                table: "DeviceGrants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_CreatedUtc_Id",
                schema: "organization",
                table: "Invitations",
                columns: new[] { "CreatedUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Memberships_UpdatedUtc_UserId",
                schema: "organization",
                table: "Memberships",
                columns: new[] { "UpdatedUtc", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceGrants",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "EnrollmentPolicies",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Invitations",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "Memberships",
                schema: "organization");
        }
    }
}
