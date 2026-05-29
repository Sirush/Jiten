using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddKanjiReadingWords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KanjiReadingWords",
                schema: "jmdict",
                columns: table => new
                {
                    KanjiCharacter = table.Column<string>(type: "text", nullable: false),
                    Reading = table.Column<string>(type: "text", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KanjiReadingWords", x => new { x.KanjiCharacter, x.Reading, x.WordId, x.ReadingIndex });
                });

            migrationBuilder.CreateIndex(
                name: "IX_KanjiReadingWords_KanjiCharacter_Reading",
                schema: "jmdict",
                table: "KanjiReadingWords",
                columns: new[] { "KanjiCharacter", "Reading" });

            migrationBuilder.CreateIndex(
                name: "IX_KanjiReadingWords_WordId",
                schema: "jmdict",
                table: "KanjiReadingWords",
                column: "WordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KanjiReadingWords",
                schema: "jmdict");
        }
    }
}
