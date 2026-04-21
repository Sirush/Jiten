using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddConjugatedForms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConjugatedForms",
                schema: "jmdict",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Surface = table.Column<string>(type: "text", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ConjugationChain = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'"),
                    FormIndex = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConjugatedForms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConjugatedForms_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConjugatedForms",
                schema: "jmdict");
        }
    }
}
