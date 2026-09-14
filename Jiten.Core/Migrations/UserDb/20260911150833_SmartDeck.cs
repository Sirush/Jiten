using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class SmartDeck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SmartDeckJson",
                schema: "user",
                table: "UserSettings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateIndex(
                name: "IX_UserStudyDeck_UserId_Smart",
                schema: "user",
                table: "UserStudyDecks",
                column: "UserId",
                unique: true,
                filter: "\"DeckType\" = 3");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserStudyDeck_UserId_Smart",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "SmartDeckJson",
                schema: "user",
                table: "UserSettings");
        }
    }
}
