using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class MediaRequestBoosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BoostCount",
                schema: "jiten",
                table: "MediaRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MediaRequestBoosts",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaRequestId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequestBoosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaRequestBoosts_MediaRequests_MediaRequestId",
                        column: x => x.MediaRequestId,
                        principalSchema: "jiten",
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestBoost_RequestId_UserId",
                schema: "jiten",
                table: "MediaRequestBoosts",
                columns: new[] { "MediaRequestId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestBoost_UserId_CreatedAt",
                schema: "jiten",
                table: "MediaRequestBoosts",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaRequestBoosts",
                schema: "jiten");

            migrationBuilder.DropColumn(
                name: "BoostCount",
                schema: "jiten",
                table: "MediaRequests");
        }
    }
}
