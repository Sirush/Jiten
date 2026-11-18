using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <summary>VNDB imports before 2025-12-23 stored fandiscs as original -> fandisc plus an Adaptation twin; rewrites them as fandisc -> original, leaving admin-curated pairs alone.</summary>
    public partial class RepairLegacyVndbFandiscLinks : Migration
    {
        private const string NotCurated = """
            NOT EXISTS (SELECT 1 FROM jiten."DeckRelationshipOverrides" o
                        WHERE o."LowDeckId" = LEAST(r."SourceDeckId", r."TargetDeckId")
                          AND o."HighDeckId" = GREATEST(r."SourceDeckId", r."TargetDeckId"))
            """;

        private const string SourceReleasedFirst = """
            s."DeckId" = r."SourceDeckId" AND t."DeckId" = r."TargetDeckId"
            AND s."ReleaseDate" >= DATE '1900-01-01' AND s."ReleaseDate" < t."ReleaseDate"
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                DELETE FROM jiten."DeckRelationships" r
                USING jiten."DeckRelationships" f
                WHERE r."RelationshipType" = 5 AND f."RelationshipType" = 2
                  AND r."SourceDeckId" = f."SourceDeckId" AND r."TargetDeckId" = f."TargetDeckId"
                  AND {NotCurated};

                DELETE FROM jiten."DeckRelationships" r
                USING jiten."Decks" s, jiten."Decks" t, jiten."DeckRelationships" f
                WHERE r."RelationshipType" = 5 AND s."MediaType" = 7 AND t."MediaType" = 7 AND {SourceReleasedFirst}
                  AND f."RelationshipType" = 2 AND f."SourceDeckId" = r."TargetDeckId" AND f."TargetDeckId" = r."SourceDeckId"
                  AND {NotCurated};

                UPDATE jiten."DeckRelationships" r
                SET "SourceDeckId" = r."TargetDeckId", "TargetDeckId" = r."SourceDeckId", "RelationshipType" = 2
                FROM jiten."Decks" s, jiten."Decks" t
                WHERE r."RelationshipType" = 5 AND s."MediaType" = 7 AND t."MediaType" = 7 AND {SourceReleasedFirst}
                  AND {NotCurated};

                DELETE FROM jiten."DeckRelationships" r
                USING jiten."Decks" s, jiten."Decks" t, jiten."DeckRelationships" f
                WHERE r."RelationshipType" = 2 AND {SourceReleasedFirst}
                  AND f."RelationshipType" = 2 AND f."SourceDeckId" = r."TargetDeckId" AND f."TargetDeckId" = r."SourceDeckId"
                  AND {NotCurated};

                UPDATE jiten."DeckRelationships" r
                SET "SourceDeckId" = r."TargetDeckId", "TargetDeckId" = r."SourceDeckId"
                FROM jiten."Decks" s, jiten."Decks" t
                WHERE r."RelationshipType" = 2 AND {SourceReleasedFirst}
                  AND {NotCurated};
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
