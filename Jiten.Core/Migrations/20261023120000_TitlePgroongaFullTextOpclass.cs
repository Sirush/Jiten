using Jiten.Core;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <summary>PGroonga's default varchar opclass only serves term and prefix search, so &amp;@ and &amp;@~ on these varchar titles fell back to sequential scans.</summary>
    [DbContext(typeof(JitenDbContext))]
    [Migration("20261023120000_TitlePgroongaFullTextOpclass")]
    public partial class TitlePgroongaFullTextOpclass : Migration
    {
        private static readonly (string Table, string Index)[] TitleIndexes =
        [
            ("DeckTitles", "IX_DeckTitles_Title_pgroonga"),
            ("MediaRequests", "IX_MediaRequests_Title_pgroonga"),
            ("MediaRequestTitles", "IX_MediaRequestTitles_Title_pgroonga")
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, index) in TitleIndexes)
                Rebuild(migrationBuilder, table, index, " pgroonga_varchar_full_text_search_ops_v2");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, index) in TitleIndexes)
                Rebuild(migrationBuilder, table, index, "");
        }

        private static void Rebuild(MigrationBuilder migrationBuilder, string table, string index, string opclass)
        {
            migrationBuilder.Sql($"""
                DROP INDEX CONCURRENTLY IF EXISTS jiten."{index}";
            """, suppressTransaction: true);

            migrationBuilder.Sql($"""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "{index}"
                ON jiten."{table}"
                USING pgroonga ("Title"{opclass});
            """, suppressTransaction: true);
        }
    }
}
