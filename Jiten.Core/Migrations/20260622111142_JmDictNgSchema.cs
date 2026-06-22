using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class JmDictNgSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DutchMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "FrenchMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "GermanMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "HungarianMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "RussianMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "SlovenianMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "SpanishMeanings",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.AddColumn<string>(
                name: "EntryInfoJson",
                schema: "jmdict",
                table: "Words",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LanguageSourcesJson",
                schema: "jmdict",
                table: "Words",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "GlossTypes",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<List<string>>(
                name: "SenseInfo",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.CreateTable(
                name: "CrossReferences",
                schema: "jmdict",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FromWordId = table.Column<int>(type: "integer", nullable: false),
                    FromSenseIndex = table.Column<int>(type: "integer", nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    TargetWordId = table.Column<int>(type: "integer", nullable: true),
                    TargetDict = table.Column<int>(type: "int", nullable: false),
                    TargetSenseIndex = table.Column<short>(type: "smallint", nullable: true),
                    TargetKanji = table.Column<string>(type: "text", nullable: true),
                    TargetReading = table.Column<string>(type: "text", nullable: true),
                    RawText = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrossReferences", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CrossReferences_FromWordId_Type",
                schema: "jmdict",
                table: "CrossReferences",
                columns: new[] { "FromWordId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_CrossReferences_TargetWordId",
                schema: "jmdict",
                table: "CrossReferences",
                column: "TargetWordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CrossReferences",
                schema: "jmdict");

            migrationBuilder.DropColumn(
                name: "EntryInfoJson",
                schema: "jmdict",
                table: "Words");

            migrationBuilder.DropColumn(
                name: "LanguageSourcesJson",
                schema: "jmdict",
                table: "Words");

            migrationBuilder.DropColumn(
                name: "GlossTypes",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.DropColumn(
                name: "SenseInfo",
                schema: "jmdict",
                table: "Definitions");

            migrationBuilder.AddColumn<List<string>>(
                name: "DutchMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "FrenchMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "GermanMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "HungarianMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "RussianMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "SlovenianMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);

            migrationBuilder.AddColumn<List<string>>(
                name: "SpanishMeanings",
                schema: "jmdict",
                table: "Definitions",
                type: "text[]",
                nullable: false);
        }
    }
}
