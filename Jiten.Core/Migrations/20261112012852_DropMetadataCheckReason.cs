using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class DropMetadataCheckReason : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Note",
                schema: "jiten",
                table: "MetadataEditChecks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(300)",
                oldMaxLength: 300,
                oldNullable: true);

            migrationBuilder.Sql("""
                UPDATE jiten."MetadataEditChecks"
                SET "Note" = left(concat_ws('. ', CASE "Reason" WHEN 1 THEN 'Source doesn''t match' WHEN 2 THEN 'Wrong value' WHEN 3 THEN 'No source' END, "Note"), 500)
                WHERE "Reason" IN (1, 2, 3);
                """);

            migrationBuilder.DropColumn(
                name: "Reason",
                schema: "jiten",
                table: "MetadataEditChecks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE jiten."MetadataEditChecks" SET "Note" = left("Note", 300) WHERE length("Note") > 300;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "Note",
                schema: "jiten",
                table: "MetadataEditChecks",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Reason",
                schema: "jiten",
                table: "MetadataEditChecks",
                type: "integer",
                nullable: true);
        }
    }
}
