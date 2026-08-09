using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class CardMediaRetainedOriginal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousContentType",
                schema: "user",
                table: "UserCardMedia",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PreviousFileSizeBytes",
                schema: "user",
                table: "UserCardMedia",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousStoragePath",
                schema: "user",
                table: "UserCardMedia",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreviousContentType",
                schema: "user",
                table: "UserCardMedia");

            migrationBuilder.DropColumn(
                name: "PreviousFileSizeBytes",
                schema: "user",
                table: "UserCardMedia");

            migrationBuilder.DropColumn(
                name: "PreviousStoragePath",
                schema: "user",
                table: "UserCardMedia");
        }
    }
}
