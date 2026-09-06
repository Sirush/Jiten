using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RuntimeSeconds",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "YouTubeSources",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    SourceKind = table.Column<int>(type: "integer", nullable: false),
                    SourceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChannelName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChannelId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TitleFilterInclude = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TitleFilterExclude = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastSourceUpdate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SyncEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YouTubeSources", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_YouTubeSources_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "YouTubeVideos",
                schema: "jiten",
                columns: table => new
                {
                    SourceDeckId = table.Column<int>(type: "integer", nullable: false),
                    VideoId = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChildDeckId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RuntimeSeconds = table.Column<int>(type: "integer", nullable: true),
                    PlayableInEmbed = table.Column<bool>(type: "boolean", nullable: false),
                    SkipReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YouTubeVideos", x => new { x.SourceDeckId, x.VideoId });
                    table.ForeignKey(
                        name: "FK_YouTubeVideos_YouTubeSources_SourceDeckId",
                        column: x => x.SourceDeckId,
                        principalSchema: "jiten",
                        principalTable: "YouTubeSources",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeSources_SourceKind_SourceId",
                schema: "jiten",
                table: "YouTubeSources",
                columns: new[] { "SourceKind", "SourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeSources_SyncEnabled_NextCheckAt",
                schema: "jiten",
                table: "YouTubeSources",
                columns: new[] { "SyncEnabled", "NextCheckAt" });

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeVideos_ChildDeckId",
                schema: "jiten",
                table: "YouTubeVideos",
                column: "ChildDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeVideos_Status_LastCheckedAt",
                schema: "jiten",
                table: "YouTubeVideos",
                columns: new[] { "Status", "LastCheckedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YouTubeVideos",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "YouTubeSources",
                schema: "jiten");

            migrationBuilder.DropColumn(
                name: "RuntimeSeconds",
                schema: "jiten",
                table: "Decks");
        }
    }
}
