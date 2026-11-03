using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class WebNovelChapterSourceEpisodeId : Migration
    {
        /// <summary>Backfills existing ledger rows, all Syosetu, whose episode id is their position.</summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceEpisodeId",
                schema: "jiten",
                table: "WebNovelChapters",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""UPDATE jiten."WebNovelChapters" SET "SourceEpisodeId" = "EpisodeNumber"::text;""");

            migrationBuilder.CreateIndex(
                name: "IX_WebNovelChapters_DeckId_SourceEpisodeId",
                schema: "jiten",
                table: "WebNovelChapters",
                columns: new[] { "DeckId", "SourceEpisodeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WebNovelChapters_DeckId_SourceEpisodeId",
                schema: "jiten",
                table: "WebNovelChapters");

            migrationBuilder.DropColumn(
                name: "SourceEpisodeId",
                schema: "jiten",
                table: "WebNovelChapters");
        }
    }
}
