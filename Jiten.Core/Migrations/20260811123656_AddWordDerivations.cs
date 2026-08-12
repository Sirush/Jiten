using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWordDerivations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WordDerivations",
                schema: "jmdict",
                columns: table => new
                {
                    DerivationId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BaseWordId = table.Column<int>(type: "integer", nullable: false),
                    BaseReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    DerivedWordId = table.Column<int>(type: "integer", nullable: false),
                    DerivedReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    Category = table.Column<short>(type: "smallint", nullable: false),
                    Source = table.Column<short>(type: "smallint", nullable: false),
                    Direction = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordDerivations", x => x.DerivationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WordDerivations_Base",
                schema: "jmdict",
                table: "WordDerivations",
                columns: new[] { "BaseWordId", "BaseReadingIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_WordDerivations_Derived",
                schema: "jmdict",
                table: "WordDerivations",
                columns: new[] { "DerivedWordId", "DerivedReadingIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_WordDerivations_Pair",
                schema: "jmdict",
                table: "WordDerivations",
                columns: new[] { "BaseWordId", "BaseReadingIndex", "DerivedWordId", "DerivedReadingIndex", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WordDerivations",
                schema: "jmdict");
        }
    }
}
