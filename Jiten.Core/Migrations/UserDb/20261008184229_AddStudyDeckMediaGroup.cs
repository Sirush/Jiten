using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddStudyDeckMediaGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GroupExcludedDeckIds",
                schema: "user",
                table: "UserStudyDecks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                schema: "user",
                table: "UserStudyDecks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "GroupKind",
                schema: "user",
                table: "UserStudyDecks",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupMediaTypes",
                schema: "user",
                table: "UserStudyDecks",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GroupExcludedDeckIds",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "GroupKind",
                schema: "user",
                table: "UserStudyDecks");

            migrationBuilder.DropColumn(
                name: "GroupMediaTypes",
                schema: "user",
                table: "UserStudyDecks");
        }
    }
}
