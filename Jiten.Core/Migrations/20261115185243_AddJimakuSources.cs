using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddJimakuSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JimakuSources",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    JimakuEntryId = table.Column<int>(type: "integer", nullable: false),
                    FilePattern = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AnilistId = table.Column<int>(type: "integer", nullable: true),
                    TmdbId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TmdbSeason = table.Column<int>(type: "integer", nullable: true),
                    ExpectedEpisodes = table.Column<int>(type: "integer", nullable: true),
                    ExpectedEpisodesOverride = table.Column<int>(type: "integer", nullable: true),
                    AiredEpisodes = table.Column<int>(type: "integer", nullable: true),
                    FinishedAiring = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JimakuSources", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_JimakuSources_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "JimakuTrackedFiles",
                schema: "jiten",
                columns: table => new
                {
                    SourceDeckId = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    SourceModifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EpisodeNumber = table.Column<double>(type: "double precision", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    HoldReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChildDeckId = table.Column<int>(type: "integer", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JimakuTrackedFiles", x => new { x.SourceDeckId, x.FileName });
                    table.ForeignKey(
                        name: "FK_JimakuTrackedFiles_JimakuSources_SourceDeckId",
                        column: x => x.SourceDeckId,
                        principalSchema: "jiten",
                        principalTable: "JimakuSources",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JimakuSources_JimakuEntryId",
                schema: "jiten",
                table: "JimakuSources",
                column: "JimakuEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JimakuSources_SyncEnabled_NextCheckAt",
                schema: "jiten",
                table: "JimakuSources",
                columns: new[] { "SyncEnabled", "NextCheckAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JimakuTrackedFiles",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "JimakuSources",
                schema: "jiten");
        }
    }
}
