using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropDeckSimilarityShareWordColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SharedWordCount",
                schema: "jiten",
                table: "DeckSimilarities");

            migrationBuilder.DropColumn(
                name: "TopSharedWordIds",
                schema: "jiten",
                table: "DeckSimilarities");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SharedWordCount",
                schema: "jiten",
                table: "DeckSimilarities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int[]>(
                name: "TopSharedWordIds",
                schema: "jiten",
                table: "DeckSimilarities",
                type: "int[]",
                nullable: false,
                defaultValueSql: "'{}'");
        }
    }
}
