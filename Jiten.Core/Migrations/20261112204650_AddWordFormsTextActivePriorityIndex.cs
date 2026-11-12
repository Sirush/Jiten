using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWordFormsTextActivePriorityIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_WordForms_Text_ActivePriority"
                ON jmdict."WordForms" ("Text")
                WHERE "IsActiveInLatestSource" AND "Priorities" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WordForms_Text_ActivePriority",
                schema: "jmdict",
                table: "WordForms");
        }
    }
}
