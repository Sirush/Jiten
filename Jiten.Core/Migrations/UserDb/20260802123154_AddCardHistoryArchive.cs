using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddCardHistoryArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReviewRollupDirty",
                schema: "user",
                table: "UserMetadatas",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewRollupRebuiltAt",
                schema: "user",
                table: "UserMetadatas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FsrsCardArchives",
                schema: "user",
                columns: table => new
                {
                    ArchiveId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<byte>(type: "smallint", nullable: false),
                    CoveringReadingIndex = table.Column<byte>(type: "smallint", nullable: true),
                    State = table.Column<int>(type: "integer", nullable: false),
                    Step = table.Column<int>(type: "integer", nullable: true),
                    Stability = table.Column<double>(type: "double precision", nullable: true),
                    Difficulty = table.Column<double>(type: "double precision", nullable: true),
                    Due = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastReview = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Lapses = table.Column<int>(type: "integer", nullable: false),
                    CardCreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReviewCount = table.Column<int>(type: "integer", nullable: false),
                    FirstReview = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HistoryMerged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    HistoryTruncated = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Logs = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FsrsCardArchives", x => x.ArchiveId);
                    table.ForeignKey(
                        name: "FK_FsrsCardArchives_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserReviewDailies",
                schema: "user",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ReviewCount = table.Column<int>(type: "integer", nullable: false),
                    CorrectCount = table.Column<int>(type: "integer", nullable: false),
                    NewCardCount = table.Column<int>(type: "integer", nullable: false),
                    TotalDurationMs = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserReviewDailies", x => new { x.UserId, x.LocalDate });
                    table.ForeignKey(
                        name: "FK_UserReviewDailies_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserMetadatas_ReviewRollupDirty",
                schema: "user",
                table: "UserMetadatas",
                column: "ReviewRollupDirty",
                filter: "\"ReviewRollupDirty\"");

            migrationBuilder.CreateIndex(
                name: "IX_FsrsCardArchive_UserId_ArchivedAt",
                schema: "user",
                table: "FsrsCardArchives",
                columns: new[] { "UserId", "ArchivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FsrsCardArchive_UserId_WordId_ReadingIndex",
                schema: "user",
                table: "FsrsCardArchives",
                columns: new[] { "UserId", "WordId", "ReadingIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FsrsCardArchives",
                schema: "user");

            migrationBuilder.DropTable(
                name: "UserReviewDailies",
                schema: "user");

            migrationBuilder.DropIndex(
                name: "IX_UserMetadatas_ReviewRollupDirty",
                schema: "user",
                table: "UserMetadatas");

            migrationBuilder.DropColumn(
                name: "ReviewRollupDirty",
                schema: "user",
                table: "UserMetadatas");

            migrationBuilder.DropColumn(
                name: "ReviewRollupRebuiltAt",
                schema: "user",
                table: "UserMetadatas");
        }
    }
}
