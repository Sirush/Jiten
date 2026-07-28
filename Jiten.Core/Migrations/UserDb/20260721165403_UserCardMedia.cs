using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class UserCardMedia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserCardMedia",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    StoragePath = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserCardMedia", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserCardMedia_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserCardMedia_UserId",
                schema: "user",
                table: "UserCardMedia",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserCardMedia_UserId_WordId_ReadingIndex_Kind",
                schema: "user",
                table: "UserCardMedia",
                columns: new[] { "UserId", "WordId", "ReadingIndex", "Kind" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserCardMedia",
                schema: "user");
        }
    }
}
