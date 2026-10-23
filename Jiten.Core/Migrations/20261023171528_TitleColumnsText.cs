using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class TitleColumnsText : Migration
    {
        private const string VarcharFullTextOpclass = " pgroonga_varchar_full_text_search_ops_v2";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            DropTitlePgroongaIndexes(migrationBuilder);
            migrationBuilder.Sql("""ALTER TABLE jiten."DeckTitles" DROP COLUMN "TitleNoSpaces";""");

            AlterTitle(migrationBuilder, "MediaRequestTitles", "text");
            AlterTitle(migrationBuilder, "MediaRequests", "text");
            AlterTitle(migrationBuilder, "DeckTitles", "text");

            AddTitleNoSpaces(migrationBuilder);
            CreateTitlePgroongaIndexes(migrationBuilder, "");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaRequestTitles_Title_Length",
                schema: "jiten",
                table: "MediaRequestTitles",
                sql: "length(\"Title\") <= 500");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MediaRequests_Title_Length",
                schema: "jiten",
                table: "MediaRequests",
                sql: "length(\"Title\") <= 300");

            migrationBuilder.AddCheckConstraint(
                name: "CK_DeckTitles_Title_Length",
                schema: "jiten",
                table: "DeckTitles",
                sql: "length(\"Title\") <= 200");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaRequestTitles_Title_Length",
                schema: "jiten",
                table: "MediaRequestTitles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MediaRequests_Title_Length",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_DeckTitles_Title_Length",
                schema: "jiten",
                table: "DeckTitles");

            DropTitlePgroongaIndexes(migrationBuilder);
            migrationBuilder.Sql("""ALTER TABLE jiten."DeckTitles" DROP COLUMN "TitleNoSpaces";""");

            AlterTitle(migrationBuilder, "MediaRequestTitles", "character varying(500)");
            AlterTitle(migrationBuilder, "MediaRequests", "character varying(300)");
            AlterTitle(migrationBuilder, "DeckTitles", "character varying(200)");

            AddTitleNoSpaces(migrationBuilder);
            CreateTitlePgroongaIndexes(migrationBuilder, VarcharFullTextOpclass);
        }

        private static void AlterTitle(MigrationBuilder migrationBuilder, string table, string type)
        {
            migrationBuilder.Sql($"""ALTER TABLE jiten."{table}" ALTER COLUMN "Title" TYPE {type};""");
        }

        private static void DropTitlePgroongaIndexes(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS jiten."IX_DeckTitles_Title_pgroonga";
                DROP INDEX IF EXISTS jiten."IX_MediaRequests_Title_pgroonga";
                DROP INDEX IF EXISTS jiten."IX_MediaRequestTitles_Title_pgroonga";
            """);
        }

        private static void CreateTitlePgroongaIndexes(MigrationBuilder migrationBuilder, string opclass)
        {
            migrationBuilder.Sql($"""
                CREATE INDEX "IX_DeckTitles_Title_pgroonga" ON jiten."DeckTitles" USING pgroonga ("Title"{opclass});
                CREATE INDEX "IX_MediaRequests_Title_pgroonga" ON jiten."MediaRequests" USING pgroonga ("Title"{opclass});
                CREATE INDEX "IX_MediaRequestTitles_Title_pgroonga" ON jiten."MediaRequestTitles" USING pgroonga ("Title"{opclass});
            """);
        }

        private static void AddTitleNoSpaces(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE jiten."DeckTitles"
                ADD COLUMN "TitleNoSpaces" text GENERATED ALWAYS AS (REPLACE("Title", ' ', '')) STORED;

                CREATE INDEX "IX_DeckTitles_TitleNoSpaces_pgroonga" ON jiten."DeckTitles" USING pgroonga ("TitleNoSpaces");
                CREATE INDEX "IX_DeckTitles_TitleNoSpaces_lower" ON jiten."DeckTitles" (LOWER("TitleNoSpaces"));
            """);
        }
    }
}
