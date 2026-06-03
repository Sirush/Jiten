using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckSimilarity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeckSimilarities",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    SimilarDeckId = table.Column<int>(type: "integer", nullable: false),
                    Similarity = table.Column<float>(type: "real", nullable: false),
                    SharedWordCount = table.Column<int>(type: "integer", nullable: false),
                    TopSharedWordIds = table.Column<int[]>(type: "int[]", nullable: false, defaultValueSql: "'{}'")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckSimilarities", x => new { x.DeckId, x.SimilarDeckId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeckSimilarities_DeckId_Similarity",
                schema: "jiten",
                table: "DeckSimilarities",
                columns: new[] { "DeckId", "Similarity" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckSimilarities",
                schema: "jiten");
        }
    }
}
