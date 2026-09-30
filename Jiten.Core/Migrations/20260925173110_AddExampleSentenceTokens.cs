using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddExampleSentenceTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 ALTER TABLE "jiten"."ExampleSentences" ADD COLUMN IF NOT EXISTS "Tokens" bytea;
                                 ALTER TABLE "jiten"."ExampleSentences" ADD COLUMN IF NOT EXISTS "WordKeys" integer[];
                                 """);

            migrationBuilder.Sql("""
                                 CREATE INDEX CONCURRENTLY IF NOT EXISTS "IX_ExampleSentence_WordKeys"
                                 ON "jiten"."ExampleSentences" USING gin ("WordKeys");
                                 """, suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                                 DROP INDEX CONCURRENTLY IF EXISTS "jiten"."IX_ExampleSentence_WordKeys";
                                 """, suppressTransaction: true);

            migrationBuilder.DropColumn(
                name: "Tokens",
                schema: "jiten",
                table: "ExampleSentences");

            migrationBuilder.DropColumn(
                name: "WordKeys",
                schema: "jiten",
                table: "ExampleSentences");
        }
    }
}
