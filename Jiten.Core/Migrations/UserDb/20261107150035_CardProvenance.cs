using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class CardProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte>(
                name: "Source",
                schema: "user",
                table: "FsrsCards",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<byte>(
                name: "Source",
                schema: "user",
                table: "FsrsCardArchives",
                type: "smallint",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceBatchId",
                schema: "user",
                table: "FsrsCards",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CardBatches",
                schema: "user",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<byte>(type: "smallint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CardCount = table.Column<int>(type: "integer", nullable: false),
                    Details = table.Column<string>(type: "jsonb", nullable: false),
                    CardState = table.Column<int>(type: "integer", nullable: true),
                    Undone = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardBatches", x => x.BatchId);
                    table.ForeignKey(
                        name: "FK_CardBatches_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CardBatch_UserId_CreatedAt",
                schema: "user",
                table: "CardBatches",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CardBatches",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "user",
                table: "FsrsCards");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "user",
                table: "FsrsCardArchives");

            migrationBuilder.DropColumn(
                name: "SourceBatchId",
                schema: "user",
                table: "FsrsCards");
        }
    }
}
