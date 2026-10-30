using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class ProfileCustomisation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarColour",
                schema: "user",
                table: "UserProfiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarKanji",
                schema: "user",
                table: "UserProfiles",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AvatarStyle",
                schema: "user",
                table: "UserProfiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bio",
                schema: "user",
                table: "UserProfiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HeaderColour",
                schema: "user",
                table: "UserProfiles",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LinksJson",
                schema: "user",
                table: "UserProfiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "PrivacyJson",
                schema: "user",
                table: "UserProfiles",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<bool>(
                name: "IsPrivate",
                schema: "user",
                table: "UserDeckPreferences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                schema: "user",
                table: "UserDeckPreferences",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "Score",
                schema: "user",
                table: "UserDeckPreferences",
                type: "smallint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserFavouriteWords",
                schema: "user",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    Position = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFavouriteWords", x => new { x.UserId, x.WordId, x.ReadingIndex });
                    table.ForeignKey(
                        name: "FK_UserFavouriteWords_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPinnedDecks",
                schema: "user",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPinnedDecks", x => new { x.UserId, x.DeckId });
                    table.ForeignKey(
                        name: "FK_UserPinnedDecks_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserFavouriteWords",
                schema: "user");

            migrationBuilder.DropTable(
                name: "UserPinnedDecks",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "AvatarColour",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AvatarKanji",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "AvatarStyle",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "Bio",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "HeaderColour",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "LinksJson",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "PrivacyJson",
                schema: "user",
                table: "UserProfiles");

            migrationBuilder.DropColumn(
                name: "IsPrivate",
                schema: "user",
                table: "UserDeckPreferences");

            migrationBuilder.DropColumn(
                name: "Note",
                schema: "user",
                table: "UserDeckPreferences");

            migrationBuilder.DropColumn(
                name: "Score",
                schema: "user",
                table: "UserDeckPreferences");
        }
    }
}
