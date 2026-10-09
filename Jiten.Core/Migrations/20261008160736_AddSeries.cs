using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FranchiseId",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Franchises",
                schema: "jiten",
                columns: table => new
                {
                    FranchiseId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    NameIsManual = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Franchises", x => x.FranchiseId);
                });

            migrationBuilder.CreateTable(
                name: "Series",
                schema: "jiten",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Series", x => x.SeriesId);
                });

            migrationBuilder.CreateTable(
                name: "SeriesMembers",
                schema: "jiten",
                columns: table => new
                {
                    SeriesId = table.Column<int>(type: "integer", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMembers", x => new { x.SeriesId, x.DeckId });
                    table.ForeignKey(
                        name: "FK_SeriesMembers_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SeriesMembers_Series_SeriesId",
                        column: x => x.SeriesId,
                        principalSchema: "jiten",
                        principalTable: "Series",
                        principalColumn: "SeriesId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_FranchiseId",
                schema: "jiten",
                table: "Decks",
                column: "FranchiseId");

            migrationBuilder.CreateIndex(
                name: "IX_SeriesMembers_DeckId",
                schema: "jiten",
                table: "SeriesMembers",
                column: "DeckId");

            migrationBuilder.AddForeignKey(
                name: "FK_Decks_Franchises_FranchiseId",
                schema: "jiten",
                table: "Decks",
                column: "FranchiseId",
                principalSchema: "jiten",
                principalTable: "Franchises",
                principalColumn: "FranchiseId",
                onDelete: ReferentialAction.SetNull);

            // Copy of SeriesRelationConversion.Sql at the time of this migration, kept literal so later edits to the class can't change history.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE series_conv_edges ON COMMIT DROP AS
                SELECT "SourceDeckId" AS a, "TargetDeckId" AS b, "RelationshipType" AS t
                FROM jiten."DeckRelationships"
                WHERE "RelationshipType" IN (7, 8) AND "SourceDeckId" <> "TargetDeckId"
                UNION
                SELECT "TargetDeckId", "SourceDeckId", "RelationshipType"
                FROM jiten."DeckRelationships"
                WHERE "RelationshipType" IN (7, 8) AND "SourceDeckId" <> "TargetDeckId";

                CREATE TEMP TABLE series_conv_components ON COMMIT DROP AS
                WITH RECURSIVE reach(t, root, deck) AS (
                    SELECT DISTINCT t, a, a FROM series_conv_edges
                    UNION
                    SELECT r.t, r.root, e.b
                    FROM reach r
                    JOIN series_conv_edges e ON e.t = r.t AND e.a = r.deck
                )
                SELECT t, deck, MIN(root) AS comp FROM reach GROUP BY t, deck;

                CREATE TEMP TABLE series_conv_groups ON COMMIT DROP AS
                SELECT g.t, g.comp,
                       nextval(pg_get_serial_sequence('jiten."Series"', 'SeriesId'))::int AS series_id,
                       (SELECT d."OriginalTitle"
                        FROM series_conv_components c
                        JOIN jiten."Decks" d ON d."DeckId" = c.deck
                        WHERE c.t = g.t AND c.comp = g.comp
                        ORDER BY d."ReleaseDate" < DATE '1900-01-01', d."ReleaseDate", d."DeckId"
                        LIMIT 1) AS name
                FROM (SELECT DISTINCT t, comp FROM series_conv_components) g;

                INSERT INTO jiten."Series" ("SeriesId", "Name", "Kind", "CreatedAt", "UpdatedAt")
                SELECT series_id, LEFT(COALESCE(name, 'Unnamed'), 200), CASE WHEN t = 7 THEN 1 ELSE 2 END, now(), now()
                FROM series_conv_groups;

                INSERT INTO jiten."SeriesMembers" ("SeriesId", "DeckId")
                SELECT g.series_id, c.deck
                FROM series_conv_components c
                JOIN series_conv_groups g ON g.t = c.t AND g.comp = c.comp;

                DELETE FROM jiten."DeckRelationships" WHERE "RelationshipType" IN (7, 8);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores each group as links from its first deck.
            migrationBuilder.Sql("""
                INSERT INTO jiten."DeckRelationships" ("SourceDeckId", "TargetDeckId", "RelationshipType")
                SELECT f.first_deck, m."DeckId", CASE WHEN s."Kind" = 1 THEN 7 ELSE 8 END
                FROM jiten."SeriesMembers" m
                JOIN jiten."Series" s ON s."SeriesId" = m."SeriesId"
                JOIN (SELECT "SeriesId", MIN("DeckId") AS first_deck FROM jiten."SeriesMembers" GROUP BY "SeriesId") f
                     ON f."SeriesId" = m."SeriesId"
                WHERE m."DeckId" <> f.first_deck
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_Decks_Franchises_FranchiseId",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropTable(
                name: "Franchises",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "SeriesMembers",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "Series",
                schema: "jiten");

            migrationBuilder.DropIndex(
                name: "IX_Decks_FranchiseId",
                schema: "jiten",
                table: "Decks");

            migrationBuilder.DropColumn(
                name: "FranchiseId",
                schema: "jiten",
                table: "Decks");
        }
    }
}
