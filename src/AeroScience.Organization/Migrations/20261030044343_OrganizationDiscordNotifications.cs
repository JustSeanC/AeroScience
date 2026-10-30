using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AeroScience.Organization.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationDiscordNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscordConsents",
                schema: "organization",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PilotId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationRevision = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Allowed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordConsents", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "DiscordDeliveries",
                schema: "organization",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationRevision = table.Column<int>(type: "integer", nullable: false),
                    ConsentSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConsentRevision = table.Column<int>(type: "integer", nullable: true),
                    Test = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SenderInstance = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordDeliveries", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "DiscordDestinations",
                schema: "organization",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    MaskedIdentifier = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ProtectedCredential = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false),
                    Categories = table.Column<int>(type: "integer", nullable: false),
                    AuthorizerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizerStamp = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    NextSendUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EventCursor = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordDestinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscordDisclosures",
                schema: "organization",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationRevision = table.Column<int>(type: "integer", nullable: false),
                    MessageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AuthorizerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorizerStamp = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ApprovedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscordDisclosures", x => new { x.EventId, x.DestinationId, x.DestinationRevision });
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_SourceId_Type_Sequence",
                schema: "organization",
                table: "Events",
                columns: new[] { "SourceId", "Type", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_Type_Sequence",
                schema: "organization",
                table: "Events",
                columns: new[] { "Type", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordConsents_DestinationId_DestinationRevision_Allowed",
                schema: "organization",
                table: "DiscordConsents",
                columns: new[] { "DestinationId", "DestinationRevision", "Allowed" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordDeliveries_DestinationId_Sequence",
                schema: "organization",
                table: "DiscordDeliveries",
                columns: new[] { "DestinationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordDeliveries_EventId_DestinationId_DestinationRevision",
                schema: "organization",
                table: "DiscordDeliveries",
                columns: new[] { "EventId", "DestinationId", "DestinationRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordDeliveries_Id",
                schema: "organization",
                table: "DiscordDeliveries",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscordDeliveries_State_DueUtc_Sequence",
                schema: "organization",
                table: "DiscordDeliveries",
                columns: new[] { "State", "DueUtc", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscordDestinations_PublicId",
                schema: "organization",
                table: "DiscordDestinations",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscordConsents",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "DiscordDeliveries",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "DiscordDestinations",
                schema: "organization");

            migrationBuilder.DropTable(
                name: "DiscordDisclosures",
                schema: "organization");

            migrationBuilder.DropIndex(
                name: "IX_Events_SourceId_Type_Sequence",
                schema: "organization",
                table: "Events");

            migrationBuilder.DropIndex(
                name: "IX_Events_Type_Sequence",
                schema: "organization",
                table: "Events");
        }
    }
}
