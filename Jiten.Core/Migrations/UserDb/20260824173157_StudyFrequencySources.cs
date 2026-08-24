using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class StudyFrequencySources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FrequencyListId",
                schema: "user",
                table: "UserStudyDecks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "FrequencyMediaType",
                schema: "user",
                table: "UserStudyDecks",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BlobGeneratedAt",
                schema: "user",
                table: "UserFrequencyLists",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "RankedWordsBlob",
                schema: "user",
                table: "UserFrequencyLists",
                type: "bytea",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserStudyDeck_FrequencyListId",
                schema: "user",
                table: "UserStudyDecks",
                column: "FrequencyListId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserStudyDeck_FrequencyListId",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "FrequencyListId",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "FrequencyMediaType",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "BlobGeneratedAt",
                schema: "user",
                table: "UserFrequencyLists");

            migrationBuilder.DropColumn(
                name: "RankedWordsBlob",
                schema: "user",
                table: "UserFrequencyLists");
        }
    }
}
