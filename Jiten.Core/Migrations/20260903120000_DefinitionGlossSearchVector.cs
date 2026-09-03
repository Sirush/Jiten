using Jiten.Core;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    [DbContext(typeof(JitenDbContext))]
    [Migration("20260903120000_DefinitionGlossSearchVector")]
    public partial class DefinitionGlossSearchVector : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION jmdict.gloss_search_text(glosses text[])
                RETURNS text
                LANGUAGE sql IMMUTABLE PARALLEL SAFE
                AS $$ SELECT regexp_replace(array_to_string(glosses, ' '), '\([^)]*\)', ' ', 'g') $$;
                """);

            migrationBuilder.Sql("""
                ALTER TABLE jmdict."Definitions"
                ADD COLUMN "SearchVector" tsvector
                GENERATED ALWAYS AS (to_tsvector('english', jmdict.gloss_search_text("EnglishMeanings"))) STORED;
                """);

            migrationBuilder.Sql("""
                CREATE INDEX "IX_Definitions_SearchVector" ON jmdict."Definitions" USING gin ("SearchVector");
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS jmdict."IX_Definitions_SearchVector";""");
            migrationBuilder.Sql("""ALTER TABLE jmdict."Definitions" DROP COLUMN IF EXISTS "SearchVector";""");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS jmdict.gloss_search_text(text[]);");
        }
    }
}
