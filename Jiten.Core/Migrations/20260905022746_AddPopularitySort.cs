using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPopularitySort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTrending",
                schema: "jiten",
                table: "Decks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PopularityFavouriteCount",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PopularityGlobalRank",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PopularityListCount",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PopularityRank",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PopularityScore",
                schema: "jiten",
                table: "Decks",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "PopularityStudyDeckCount",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DeckActivityDaily",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Views = table.Column<int>(type: "integer", nullable: false),
                    GuestDownloads = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckActivityDaily", x => new { x.DeckId, x.Date });
                    table.ForeignKey(
                        name: "FK_DeckActivityDaily_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_PopularityScore",
                schema: "jiten",
                table: "Decks",
                columns: new[] { "PopularityScore", "ExternalRating", "ReleaseDate" },
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckActivityDaily",
                schema: "jiten");

            migrationBuilder.DropIndex(
                name: "IX_Decks_PopularityScore",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "IsTrending",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "PopularityFavouriteCount",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "PopularityGlobalRank",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "PopularityListCount",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "PopularityRank",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "PopularityScore",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "PopularityStudyDeckCount",
                schema: "jiten",
                table: "Decks");
        }
    }
}
