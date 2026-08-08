using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class LegalAcceptanceAndBillingNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "StripeCancelAtPeriodEnd",
                schema: "user",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BillingEmailLogs",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    SubscriptionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RenewalDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingEmailLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserLegalDocumentStates",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Document = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NoticeShownAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Source = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLegalDocumentStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingEmailLog_UserId_Kind",
                schema: "user",
                table: "BillingEmailLogs",
                columns: new[] { "UserId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_UserLegalDocumentState_UserId_Document_Version",
                schema: "user",
                table: "UserLegalDocumentStates",
                columns: new[] { "UserId", "Document", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingEmailLogs",
                schema: "user");

            migrationBuilder.DropTable(
                name: "UserLegalDocumentStates",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "StripeCancelAtPeriodEnd",
                schema: "user",
                table: "AspNetUsers");
        }
    }
}
