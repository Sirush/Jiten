using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDeckSentenceProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeckSentenceProfiles",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Profile = table.Column<byte[]>(type: "bytea", nullable: true),
                    SentenceCount = table.Column<int>(type: "integer", nullable: false),
                    Sample = table.Column<byte[]>(type: "bytea", nullable: true),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<short>(type: "smallint", nullable: false),
                    BuiltAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckSentenceProfiles", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_DeckSentenceProfiles_Decks_DeckId",
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
                name: "DeckSentenceProfiles",
                schema: "jiten");
        }
    }
}
