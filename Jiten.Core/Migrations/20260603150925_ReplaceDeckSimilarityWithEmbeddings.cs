using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceDeckSimilarityWithEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckSimilarities",
                schema: "jiten");

            migrationBuilder.CreateTable(
                name: "DeckEmbeddings",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Vector = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckEmbeddings", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_DeckEmbeddings_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeckEmbeddingSpaces",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    Mean = table.Column<byte[]>(type: "bytea", nullable: false),
                    Components = table.Column<byte[]>(type: "bytea", nullable: false),
                    Idf = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckEmbeddingSpaces", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckEmbeddings",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "DeckEmbeddingSpaces",
                schema: "jiten");

            migrationBuilder.CreateTable(
                name: "DeckSimilarities",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    SimilarDeckId = table.Column<int>(type: "integer", nullable: false),
                    Similarity = table.Column<float>(type: "real", nullable: false)
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
    }
}
