using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class GroupTitles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "jiten",
                table: "Series",
                newName: "OriginalTitle");

            migrationBuilder.RenameColumn(
                name: "Name",
                schema: "jiten",
                table: "Franchises",
                newName: "OriginalTitle");

            migrationBuilder.AddColumn<string>(
                name: "EnglishTitle",
                schema: "jiten",
                table: "Series",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RomajiTitle",
                schema: "jiten",
                table: "Series",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnglishTitle",
                schema: "jiten",
                table: "Franchises",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RomajiTitle",
                schema: "jiten",
                table: "Franchises",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // A name copied from a member deck's original title takes that deck's other titles; any other name keeps only its original.
            migrationBuilder.Sql("""
                UPDATE jiten."Franchises" f
                SET "RomajiTitle" = LEFT(NULLIF(BTRIM(d."RomajiTitle"), ''), 200),
                    "EnglishTitle" = LEFT(NULLIF(BTRIM(d."EnglishTitle"), ''), 200)
                FROM (SELECT DISTINCT ON (dd."FranchiseId") dd."FranchiseId", dd."RomajiTitle", dd."EnglishTitle"
                      FROM jiten."Decks" dd
                      JOIN jiten."Franchises" ff ON ff."FranchiseId" = dd."FranchiseId" AND ff."OriginalTitle" = dd."OriginalTitle"
                      ORDER BY dd."FranchiseId", dd."DeckId") d
                WHERE d."FranchiseId" = f."FranchiseId";
                """);

            migrationBuilder.Sql("""
                UPDATE jiten."Series" s
                SET "RomajiTitle" = LEFT(NULLIF(BTRIM(d."RomajiTitle"), ''), 200),
                    "EnglishTitle" = LEFT(NULLIF(BTRIM(d."EnglishTitle"), ''), 200)
                FROM (SELECT DISTINCT ON (m."SeriesId") m."SeriesId", dd."RomajiTitle", dd."EnglishTitle"
                      FROM jiten."SeriesMembers" m
                      JOIN jiten."Decks" dd ON dd."DeckId" = m."DeckId"
                      JOIN jiten."Series" ss ON ss."SeriesId" = m."SeriesId" AND ss."OriginalTitle" = dd."OriginalTitle"
                      ORDER BY m."SeriesId", dd."DeckId") d
                WHERE d."SeriesId" = s."SeriesId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnglishTitle",
                schema: "jiten",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "RomajiTitle",
                schema: "jiten",
                table: "Series");

            migrationBuilder.DropColumn(
                name: "EnglishTitle",
                schema: "jiten",
                table: "Franchises");

            migrationBuilder.DropColumn(
                name: "RomajiTitle",
                schema: "jiten",
                table: "Franchises");

            migrationBuilder.RenameColumn(
                name: "OriginalTitle",
                schema: "jiten",
                table: "Series",
                newName: "Name");

            migrationBuilder.RenameColumn(
                name: "OriginalTitle",
                schema: "jiten",
                table: "Franchises",
                newName: "Name");
        }
    }
}
