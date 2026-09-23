using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddKanjiVgData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KanjiComponents",
                schema: "jmdict",
                columns: table => new
                {
                    KanjiCharacter = table.Column<string>(type: "text", nullable: false),
                    NodeIndex = table.Column<short>(type: "smallint", nullable: false),
                    ParentIndex = table.Column<short>(type: "smallint", nullable: true),
                    Component = table.Column<string>(type: "text", nullable: false),
                    Original = table.Column<string>(type: "text", nullable: true),
                    IsRadical = table.Column<bool>(type: "boolean", nullable: false),
                    IsPhonetic = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KanjiComponents", x => new { x.KanjiCharacter, x.NodeIndex });
                });

            migrationBuilder.CreateTable(
                name: "KanjiStrokes",
                schema: "jmdict",
                columns: table => new
                {
                    Character = table.Column<string>(type: "text", nullable: false),
                    Paths = table.Column<List<string>>(type: "text[]", nullable: false),
                    NumberPositions = table.Column<List<float>>(type: "real[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KanjiStrokes", x => x.Character);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KanjiComponents_Component",
                schema: "jmdict",
                table: "KanjiComponents",
                column: "Component");

            migrationBuilder.CreateIndex(
                name: "IX_KanjiComponents_Original",
                schema: "jmdict",
                table: "KanjiComponents",
                column: "Original");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KanjiComponents",
                schema: "jmdict");

            migrationBuilder.DropTable(
                name: "KanjiStrokes",
                schema: "jmdict");
        }
    }
}
