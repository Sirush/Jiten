using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeWatchData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MedianChildRuntimeSeconds",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeckSubtitleTracks",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    CuesJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckSubtitleTracks", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_DeckSubtitleTracks_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckSubtitleTracks",
                schema: "jiten");

            migrationBuilder.DropColumn(
                name: "MedianChildRuntimeSeconds",
                schema: "jiten",
                table: "Decks");
        }
    }
}
