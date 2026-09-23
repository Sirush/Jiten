using Jiten.Core;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    [DbContext(typeof(JitenDbContext))]
    [Migration("20260923120000_AddMediaRequestTitlePgroongaIndex")]
    public partial class AddMediaRequestTitlePgroongaIndex : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_MediaRequests_Title_pgroonga"
                ON jiten."MediaRequests"
                USING pgroonga ("Title");
            """, suppressTransaction: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX CONCURRENTLY IF EXISTS jiten."IX_MediaRequests_Title_pgroonga";
            """, suppressTransaction: true);
        }
    }
}
