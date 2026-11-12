using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class FsrsCardSourceBatchIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_FsrsCard_UserId_SourceBatchId"
                                 ON "user"."FsrsCards" ("UserId", "SourceBatchId")
                                 WHERE "SourceBatchId" IS NOT NULL;
                                 """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 DROP INDEX CONCURRENTLY IF EXISTS "user"."IX_FsrsCard_UserId_SourceBatchId";
                                 """, suppressTransaction: true);
        }
    }
}
