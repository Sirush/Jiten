using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWordFormRedundancies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WordFormRedundancies",
                schema: "jmdict",
                columns: table => new
                {
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    SourceReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    TargetReadingIndex = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordFormRedundancies", x => new { x.WordId, x.SourceReadingIndex, x.TargetReadingIndex });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WordFormRedundancies",
                schema: "jmdict");
        }
    }
}
