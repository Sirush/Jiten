using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class UserFrequencyLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserFrequencyLists",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSaved = table.Column<bool>(type: "boolean", nullable: false),
                    AutoUpdate = table.Column<bool>(type: "boolean", nullable: false),
                    PublicSlug = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ZipUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CsvUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    WordCount = table.Column<int>(type: "integer", nullable: false),
                    DeckCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    GeneratedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserFrequencyLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserFrequencyLists_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserFrequencyList_PublicSlug",
                schema: "user",
                table: "UserFrequencyLists",
                column: "PublicSlug",
                unique: true,
                filter: "\"PublicSlug\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserFrequencyList_UserId",
                schema: "user",
                table: "UserFrequencyLists",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserFrequencyLists",
                schema: "user");
        }
    }
}
