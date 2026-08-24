using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaTypeFrequencies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WordFormFrequenciesByType",
                schema: "jmdict",
                columns: table => new
                {
                    MediaType = table.Column<short>(type: "smallint", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<short>(type: "smallint", nullable: false),
                    FrequencyRank = table.Column<int>(type: "integer", nullable: false),
                    FrequencyPercentage = table.Column<double>(type: "double precision", nullable: false),
                    ObservedFrequency = table.Column<double>(type: "double precision", nullable: false),
                    UsedInMediaAmount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordFormFrequenciesByType", x => new { x.MediaType, x.WordId, x.ReadingIndex });
                });

            migrationBuilder.CreateTable(
                name: "WordFrequenciesByType",
                schema: "jmdict",
                columns: table => new
                {
                    MediaType = table.Column<short>(type: "smallint", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    FrequencyRank = table.Column<int>(type: "integer", nullable: false),
                    UsedInMediaAmount = table.Column<int>(type: "integer", nullable: false),
                    ObservedFrequency = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordFrequenciesByType", x => new { x.MediaType, x.WordId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WordFormFrequenciesByType_MediaType_FrequencyRank",
                schema: "jmdict",
                table: "WordFormFrequenciesByType",
                columns: new[] { "MediaType", "FrequencyRank" });

            migrationBuilder.CreateIndex(
                name: "IX_WordFormFrequenciesByType_WordId",
                schema: "jmdict",
                table: "WordFormFrequenciesByType",
                column: "WordId");

            migrationBuilder.CreateIndex(
                name: "IX_WordFrequenciesByType_MediaType_FrequencyRank",
                schema: "jmdict",
                table: "WordFrequenciesByType",
                columns: new[] { "MediaType", "FrequencyRank" });

            migrationBuilder.CreateIndex(
                name: "IX_WordFrequenciesByType_WordId",
                schema: "jmdict",
                table: "WordFrequenciesByType",
                column: "WordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WordFormFrequenciesByType",
                schema: "jmdict");

            migrationBuilder.DropTable(
                name: "WordFrequenciesByType",
                schema: "jmdict");
        }
    }
}
