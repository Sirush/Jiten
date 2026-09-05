using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddPopularitySignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "user",
                table: "UserDeckPreferences",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Registration is a lower bound for undated rows; decay starts there rather than at the epoch.
            migrationBuilder.Sql(
                "UPDATE \"user\".\"UserDeckPreferences\" p SET \"UpdatedAt\" = u.\"CreatedAt\" " +
                "FROM \"user\".\"AspNetUsers\" u WHERE u.\"Id\" = p.\"UserId\"");

            migrationBuilder.CreateTable(
                name: "DeckDownloads",
                schema: "user",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    FirstDownloadAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckDownloads", x => new { x.UserId, x.DeckId });
                    table.ForeignKey(
                        name: "FK_DeckDownloads_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckDownloads",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "user",
                table: "UserDeckPreferences");
        }
    }
}
