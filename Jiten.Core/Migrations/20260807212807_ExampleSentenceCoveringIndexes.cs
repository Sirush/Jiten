using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class ExampleSentenceCoveringIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Built concurrently and only then swapped: ExampleSentenceWords is ~44M rows, and dropping the
            // old index first would leave every word lookup unindexed for the length of the rebuild.
            migrationBuilder.Sql("""
                                 CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ExampleSentenceWord_WordIdReadingIndex_IncSentenceId"
                                 ON "jiten"."ExampleSentenceWords" ("WordId", "ReadingIndex")
                                 INCLUDE ("ExampleSentenceId");
                                 """, suppressTransaction: true);

            migrationBuilder.Sql("""
                                 DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentenceWord_WordIdReadingIndex";
                                 """, suppressTransaction: true);

            migrationBuilder.Sql("""
                                 CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ExampleSentence_SentenceId_IncDeckId"
                                 ON "jiten"."ExampleSentences" ("SentenceId")
                                 INCLUDE ("DeckId");
                                 """, suppressTransaction: true);

            // Index-only scans read the visibility map, which a bulk-loaded table leaves unset — without this
            // both indexes above still take a heap fetch per row and buy nothing.
            migrationBuilder.Sql("""VACUUM (ANALYZE) "jiten"."ExampleSentenceWords";""", suppressTransaction: true);
            migrationBuilder.Sql("""VACUUM (ANALYZE) "jiten"."ExampleSentences";""", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ExampleSentenceWord_WordIdReadingIndex"
                                 ON "jiten"."ExampleSentenceWords" ("WordId", "ReadingIndex");
                                 """, suppressTransaction: true);

            migrationBuilder.Sql("""
                                 DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentenceWord_WordIdReadingIndex_IncSentenceId";
                                 """, suppressTransaction: true);

            migrationBuilder.Sql("""
                                 DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentence_SentenceId_IncDeckId";
                                 """, suppressTransaction: true);
        }
    }
}
