using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceExampleSentenceWordsWithTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentence_WordKeys";""", suppressTransaction: true);

            // Packs each sentence's link rows into a partial Tokens payload (ExampleSentenceTokens layout, header 0x81) plus its
            // WordKeys and a random sampling bucket, committing per id batch so the rewrite never holds long locks.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    lo bigint;
                    batch constant bigint := 100000;
                BEGIN
                    IF to_regclass('jiten."ExampleSentenceWords"') IS NULL THEN RETURN; END IF;
                    SELECT min("SentenceId") INTO lo FROM jiten."ExampleSentences" WHERE "Tokens" IS NULL;
                    WHILE lo IS NOT NULL AND lo <= (SELECT max("SentenceId") FROM jiten."ExampleSentences") LOOP
                        UPDATE jiten."ExampleSentences" es
                        SET "Tokens" = c.tokens,
                            "WordKeys" = ARRAY(SELECT k FROM unnest(c.keys || ARRAY[c.bucket / 64, 64 + c.bucket]) AS k ORDER BY k)
                        FROM (
                            SELECT w."ExampleSentenceId" AS sid,
                                   decode('81' || string_agg(
                                       lpad(to_hex(w."ReadingIndex"::int), 2, '0')
                                       || lpad(to_hex(w."WordId" & 255), 2, '0')
                                       || lpad(to_hex((w."WordId" >> 8) & 255), 2, '0')
                                       || lpad(to_hex((w."WordId" >> 16) & 255), 2, '0')
                                       || lpad(to_hex(w."Position"::int), 2, '0')
                                       || lpad(to_hex(w."Length"::int | 128), 2, '0'),
                                       '' ORDER BY w."Position", w."WordId"), 'hex') AS tokens,
                                   array_agg(DISTINCT ((((w."WordId"::bigint << 8) | w."ReadingIndex")
                                                        - CASE WHEN w."WordId" >= 8388608 THEN 4294967296 ELSE 0 END)::int)) AS keys,
                                   floor(random() * 4096)::int AS bucket
                            FROM jiten."ExampleSentenceWords" w
                            WHERE w."ExampleSentenceId" >= lo AND w."ExampleSentenceId" < lo + batch
                              AND w."WordId" BETWEEN 0 AND 16777215
                              AND w."Position" BETWEEN 0 AND 255
                              AND w."Length" BETWEEN 1 AND 63
                            GROUP BY w."ExampleSentenceId"
                        ) c
                        WHERE es."SentenceId" = c.sid AND es."Tokens" IS NULL;
                        COMMIT;
                        lo := lo + batch;
                    END LOOP;
                END $$;
                """, suppressTransaction: true);

            // A sentence with no convertible link row is unreachable by any word, but still needs a valid payload.
            migrationBuilder.Sql("""
                UPDATE jiten."ExampleSentences" es
                SET "Tokens" = '\x81'::bytea, "WordKeys" = ARRAY[r.bucket / 64, 64 + r.bucket]
                FROM (SELECT "SentenceId", floor(random() * 4096)::int AS bucket FROM jiten."ExampleSentences" WHERE "Tokens" IS NULL) r
                WHERE es."SentenceId" = r."SentenceId";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    expected bigint;
                    converted bigint;
                    skipped bigint;
                BEGIN
                    IF to_regclass('jiten."ExampleSentenceWords"') IS NULL THEN RETURN; END IF;

                    SELECT count(*) FILTER (WHERE w."WordId" BETWEEN 0 AND 16777215 AND w."Position" BETWEEN 0 AND 255 AND w."Length" BETWEEN 1 AND 63),
                           count(*) FILTER (WHERE NOT (w."WordId" BETWEEN 0 AND 16777215 AND w."Position" BETWEEN 0 AND 255 AND w."Length" BETWEEN 1 AND 63))
                    INTO expected, skipped
                    FROM jiten."ExampleSentenceWords" w
                    JOIN jiten."ExampleSentences" es ON es."SentenceId" = w."ExampleSentenceId"
                    WHERE get_byte(es."Tokens", 0) = 129;

                    SELECT coalesce(sum((octet_length("Tokens") - 1) / 6), 0) INTO converted
                    FROM jiten."ExampleSentences" WHERE get_byte("Tokens", 0) = 129;

                    RAISE NOTICE 'Example sentence links converted: %, skipped as unencodable: %', converted, skipped;
                    IF converted <> expected THEN
                        RAISE EXCEPTION 'Converted % tokens but found % convertible links; ExampleSentenceWords kept', converted, expected;
                    END IF;
                END $$;
                """, suppressTransaction: true);

            migrationBuilder.Sql("""DROP TABLE IF EXISTS "jiten"."ExampleSentenceWords";""", suppressTransaction: true);
            migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentence_DeckId";""", suppressTransaction: true);
            migrationBuilder.Sql("""DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentence_SentenceId_IncDeckId";""", suppressTransaction: true);

            // A validated CHECK lets SET NOT NULL skip its full-table scan under an exclusive lock.
            migrationBuilder.Sql("""
                ALTER TABLE "jiten"."ExampleSentences" DROP CONSTRAINT IF EXISTS "CK_ExampleSentences_TokensPresent";
                ALTER TABLE "jiten"."ExampleSentences" ADD CONSTRAINT "CK_ExampleSentences_TokensPresent"
                    CHECK ("Tokens" IS NOT NULL AND "WordKeys" IS NOT NULL) NOT VALID;
                """, suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE "jiten"."ExampleSentences" VALIDATE CONSTRAINT "CK_ExampleSentences_TokensPresent";""",
                                 suppressTransaction: true);
            migrationBuilder.Sql("""
                ALTER TABLE "jiten"."ExampleSentences" ALTER COLUMN "Tokens" SET NOT NULL, ALTER COLUMN "WordKeys" SET NOT NULL;
                ALTER TABLE "jiten"."ExampleSentences" DROP CONSTRAINT "CK_ExampleSentences_TokensPresent";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ExampleSentence_WordKeys"
                ON "jiten"."ExampleSentences" USING gin ("WordKeys");
                """, suppressTransaction: true);
            migrationBuilder.Sql("""VACUUM (ANALYZE) "jiten"."ExampleSentences";""", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restores the schema only: the dropped link rows cannot be rebuilt.
            migrationBuilder.AlterColumn<int[]>(
                name: "WordKeys",
                schema: "jiten",
                table: "ExampleSentences",
                type: "integer[]",
                nullable: true,
                oldClrType: typeof(int[]),
                oldType: "integer[]");

            migrationBuilder.AlterColumn<byte[]>(
                name: "Tokens",
                schema: "jiten",
                table: "ExampleSentences",
                type: "bytea",
                nullable: true,
                oldClrType: typeof(byte[]),
                oldType: "bytea");

            migrationBuilder.CreateTable(
                name: "ExampleSentenceWords",
                schema: "jiten",
                columns: table => new
                {
                    ExampleSentenceId = table.Column<long>(type: "bigint", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<byte>(type: "smallint", nullable: false),
                    Length = table.Column<byte>(type: "smallint", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExampleSentenceWords", x => new { x.ExampleSentenceId, x.WordId, x.Position });
                    table.ForeignKey(
                        name: "FK_ExampleSentenceWords_ExampleSentences_ExampleSentenceId",
                        column: x => x.ExampleSentenceId,
                        principalSchema: "jiten",
                        principalTable: "ExampleSentences",
                        principalColumn: "SentenceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExampleSentenceWords_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_DeckId",
                schema: "jiten",
                table: "ExampleSentences",
                column: "DeckId");

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentence_SentenceId_IncDeckId",
                schema: "jiten",
                table: "ExampleSentences",
                column: "SentenceId")
                .Annotation("Npgsql:IndexInclude", new[] { "DeckId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExampleSentenceWord_WordIdReadingIndex_IncSentenceId",
                schema: "jiten",
                table: "ExampleSentenceWords",
                columns: new[] { "WordId", "ReadingIndex" })
                .Annotation("Npgsql:IndexInclude", new[] { "ExampleSentenceId" });
        }
    }
}
