using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeRegistrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "YouTubeRegistrations",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OriginalTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RomajiTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EnglishTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReleaseDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CoverPath = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TitleFilterInclude = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TitleFilterExclude = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MinRuntimeSeconds = table.Column<int>(type: "integer", nullable: true),
                    MaxRuntimeSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeckId = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_YouTubeRegistrations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_YouTubeRegistrations_CompletedAt",
                schema: "jiten",
                table: "YouTubeRegistrations",
                column: "CompletedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "YouTubeRegistrations",
                schema: "jiten");
        }
    }
}
