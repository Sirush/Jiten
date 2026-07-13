using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class WebNovelDecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebNovelSources",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastEpisodeCount = table.Column<int>(type: "integer", nullable: false),
                    LastSourceUpdate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAtSource = table.Column<bool>(type: "boolean", nullable: false),
                    OnHiatusAtSource = table.Column<bool>(type: "boolean", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChunkCharBudget = table.Column<int>(type: "integer", nullable: true),
                    PendingRevisionCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebNovelSources", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_WebNovelSources_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebNovelChapters",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "integer", nullable: false),
                    ChildDeckId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CharCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebNovelChapters", x => new { x.DeckId, x.EpisodeNumber });
                    table.ForeignKey(
                        name: "FK_WebNovelChapters_WebNovelSources_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "WebNovelSources",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WebNovelChapters_ChildDeckId",
                schema: "jiten",
                table: "WebNovelChapters",
                column: "ChildDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_WebNovelSources_Provider_SourceId",
                schema: "jiten",
                table: "WebNovelSources",
                columns: new[] { "Provider", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebNovelSources_SyncEnabled_NextCheckAt",
                schema: "jiten",
                table: "WebNovelSources",
                columns: new[] { "SyncEnabled", "NextCheckAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebNovelChapters",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "WebNovelSources",
                schema: "jiten");
        }
    }
}
