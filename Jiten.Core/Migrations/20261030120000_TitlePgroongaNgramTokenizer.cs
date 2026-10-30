using Jiten.Core;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <summary>TokenBigram keeps Latin runs as whole words, so "schwarz" missed "Schwarzesmarken"; bigrams on letters and digits restore substring matching.</summary>
    [DbContext(typeof(JitenDbContext))]
    [Migration("20261030120000_TitlePgroongaNgramTokenizer")]
    public partial class TitlePgroongaNgramTokenizer : Migration
    {
        private const string NgramTokenizer = """ WITH (tokenizer = 'TokenNgram("unify_alphabet", false, "unify_digit", false)')""";

        private static readonly (string Table, string Column, string Index)[] TitleIndexes =
        [
            ("DeckTitles", "Title", "IX_DeckTitles_Title_pgroonga"),
            ("DeckTitles", "TitleNoSpaces", "IX_DeckTitles_TitleNoSpaces_pgroonga"),
            ("MediaRequests", "Title", "IX_MediaRequests_Title_pgroonga"),
            ("MediaRequestTitles", "Title", "IX_MediaRequestTitles_Title_pgroonga")
        ];

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, index) in TitleIndexes)
                Swap(migrationBuilder, table, column, index, NgramTokenizer);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column, index) in TitleIndexes)
                Swap(migrationBuilder, table, column, index, "");
        }

        /// <summary>Builds the replacement before dropping the original so searches never lose the index mid-rebuild.</summary>
        private static void Swap(MigrationBuilder migrationBuilder, string table, string column, string index, string withClause)
        {
            var temp = index + "_new";

            migrationBuilder.Sql($"""DROP INDEX CONCURRENTLY IF EXISTS jiten."{temp}";""", suppressTransaction: true);
            migrationBuilder.Sql($"""CREATE INDEX CONCURRENTLY "{temp}" ON jiten."{table}" USING pgroonga ("{column}"){withClause};""",
                suppressTransaction: true);
            migrationBuilder.Sql($"""DROP INDEX CONCURRENTLY IF EXISTS jiten."{index}";""", suppressTransaction: true);
            migrationBuilder.Sql($"""ALTER INDEX jiten."{temp}" RENAME TO "{index}";""", suppressTransaction: true);
        }
    }
}
