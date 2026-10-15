using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class MediaRequestMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DuplicateFlagDismissed",
                schema: "jiten",
                table: "MediaRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EnglishTitle",
                schema: "jiten",
                table: "MediaRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MetadataFetchedAt",
                schema: "jiten",
                table: "MediaRequests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MetadataStatus",
                schema: "jiten",
                table: "MediaRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OriginalTitle",
                schema: "jiten",
                table: "MediaRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PossibleDuplicateDeckId",
                schema: "jiten",
                table: "MediaRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PossibleDuplicateOfRequestId",
                schema: "jiten",
                table: "MediaRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReleaseDate",
                schema: "jiten",
                table: "MediaRequests",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RomajiTitle",
                schema: "jiten",
                table: "MediaRequests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MediaRequestGenres",
                schema: "jiten",
                columns: table => new
                {
                    MediaRequestId = table.Column<int>(type: "integer", nullable: false),
                    Genre = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequestGenres", x => new { x.MediaRequestId, x.Genre });
                    table.ForeignKey(
                        name: "FK_MediaRequestGenres_MediaRequests_MediaRequestId",
                        column: x => x.MediaRequestId,
                        principalSchema: "jiten",
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaRequestLinks",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaRequestId = table.Column<int>(type: "integer", nullable: false),
                    LinkType = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequestLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaRequestLinks_MediaRequests_MediaRequestId",
                        column: x => x.MediaRequestId,
                        principalSchema: "jiten",
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaRequestTags",
                schema: "jiten",
                columns: table => new
                {
                    MediaRequestId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Percentage = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequestTags", x => new { x.MediaRequestId, x.TagId });
                    table.ForeignKey(
                        name: "FK_MediaRequestTags_MediaRequests_MediaRequestId",
                        column: x => x.MediaRequestId,
                        principalSchema: "jiten",
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MediaRequestTags_Tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "jiten",
                        principalTable: "Tags",
                        principalColumn: "TagId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MediaRequestTitles",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaRequestId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TitleType = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaRequestTitles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MediaRequestTitles_MediaRequests_MediaRequestId",
                        column: x => x.MediaRequestId,
                        principalSchema: "jiten",
                        principalTable: "MediaRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_PossibleDuplicateDeckId",
                schema: "jiten",
                table: "MediaRequests",
                column: "PossibleDuplicateDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequests_PossibleDuplicateOfRequestId",
                schema: "jiten",
                table: "MediaRequests",
                column: "PossibleDuplicateOfRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestGenres_Genre",
                schema: "jiten",
                table: "MediaRequestGenres",
                column: "Genre");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestLinks_LinkType",
                schema: "jiten",
                table: "MediaRequestLinks",
                column: "LinkType");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestLinks_MediaRequestId",
                schema: "jiten",
                table: "MediaRequestLinks",
                column: "MediaRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestTags_TagId",
                schema: "jiten",
                table: "MediaRequestTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestTitles_MediaRequestId",
                schema: "jiten",
                table: "MediaRequestTitles",
                column: "MediaRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaRequests_Decks_PossibleDuplicateDeckId",
                schema: "jiten",
                table: "MediaRequests",
                column: "PossibleDuplicateDeckId",
                principalSchema: "jiten",
                principalTable: "Decks",
                principalColumn: "DeckId",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_MediaRequests_MediaRequests_PossibleDuplicateOfRequestId",
                schema: "jiten",
                table: "MediaRequests",
                column: "PossibleDuplicateOfRequestId",
                principalSchema: "jiten",
                principalTable: "MediaRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Request duplicate checks search provider names with &@, as they do request titles
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_MediaRequestTitles_Title_pgroonga"
                ON jiten."MediaRequestTitles"
                USING pgroonga ("Title");
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS jiten."IX_MediaRequestTitles_Title_pgroonga";""");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaRequests_Decks_PossibleDuplicateDeckId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_MediaRequests_MediaRequests_PossibleDuplicateOfRequestId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropTable(
                name: "MediaRequestGenres",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MediaRequestLinks",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MediaRequestTags",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MediaRequestTitles",
                schema: "jiten");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_PossibleDuplicateDeckId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequests_PossibleDuplicateOfRequestId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "DuplicateFlagDismissed",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "EnglishTitle",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MetadataFetchedAt",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "MetadataStatus",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "OriginalTitle",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "PossibleDuplicateDeckId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "PossibleDuplicateOfRequestId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "ReleaseDate",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "RomajiTitle",
                schema: "jiten",
                table: "MediaRequests");
        }
    }
}
