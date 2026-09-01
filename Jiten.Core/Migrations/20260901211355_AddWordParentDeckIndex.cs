using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWordParentDeckIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WordParentDeckIndex",
                schema: "jiten",
                columns: table => new
                {
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    DeckIds = table.Column<int[]>(type: "integer[]", nullable: false),
                    Occurrences = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordParentDeckIndex", x => new { x.WordId, x.ReadingIndex });
                });

            migrationBuilder.CreateTable(
                name: "WordParentDeckIndexBuild",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeckIds = table.Column<int[]>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordParentDeckIndexBuild", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WordParentDeckIndex",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "WordParentDeckIndexBuild",
                schema: "jiten");
        }
    }
}
