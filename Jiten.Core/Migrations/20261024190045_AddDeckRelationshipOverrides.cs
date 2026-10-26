using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckRelationshipOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeckRelationshipOverrides",
                schema: "jiten",
                columns: table => new
                {
                    LowDeckId = table.Column<int>(type: "integer", nullable: false),
                    HighDeckId = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckRelationshipOverrides", x => new { x.LowDeckId, x.HighDeckId });
                    table.ForeignKey(
                        name: "FK_DeckRelationshipOverrides_Decks_HighDeckId",
                        column: x => x.HighDeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeckRelationshipOverrides_Decks_LowDeckId",
                        column: x => x.LowDeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeckRelationshipOverrides_HighDeckId",
                schema: "jiten",
                table: "DeckRelationshipOverrides",
                column: "HighDeckId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckRelationshipOverrides",
                schema: "jiten");
        }
    }
}
