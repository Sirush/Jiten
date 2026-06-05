using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddStripFuriganaCorpusIndex : Migration
    {
        // Inline furigana annotations ({base'reading}) live in DeckRawTexts.RawText and must stay
        // there (ReparseJob/ruby priors need them). For corpus full-text search we instead index a
        // furigana-stripped projection so search, snippets and occurrence counts ignore the markup.
        // Idempotent (CREATE OR REPLACE / IF [NOT] EXISTS) so it is safe whether or not the
        // equivalent SQL was already applied manually in production.

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jiten.strip_furigana(t text)
                RETURNS text
                LANGUAGE sql
                IMMUTABLE
                PARALLEL SAFE
                AS $$
                    SELECT regexp_replace(t, '\{([^''{}]+)''[^}]+\}', '\1', 'g')
                $$;
            """, suppressTransaction: true);

            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DeckRawTexts_RawTextClean_pgroonga"
                ON jiten."DeckRawTexts"
                USING pgroonga (jiten.strip_furigana("RawText"))
                WITH (tokenizer='TokenBigramSplitSymbolAlphaDigit');
            """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS jiten."IX_DeckRawTexts_RawText_pgroonga";
            """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_DeckRawTexts_RawText_pgroonga"
                ON jiten."DeckRawTexts"
                USING pgroonga ("RawText")
                WITH (tokenizer='TokenBigramSplitSymbolAlphaDigit');
            """, suppressTransaction: true);

            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS jiten."IX_DeckRawTexts_RawTextClean_pgroonga";
            """, suppressTransaction: true);

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jiten.strip_furigana(text);", suppressTransaction: true);
        }
    }
}
