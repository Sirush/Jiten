using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class MediaRequestKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                schema: "jiten",
                table: "MediaRequests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "TargetDeckId",
                schema: "jiten",
                table: "MediaRequests",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequest_Kind",
                schema: "jiten",
                table: "MediaRequests",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequest_TargetDeckId",
                schema: "jiten",
                table: "MediaRequests",
                column: "TargetDeckId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaRequests_Decks_TargetDeckId",
                schema: "jiten",
                table: "MediaRequests",
                column: "TargetDeckId",
                principalSchema: "jiten",
                principalTable: "Decks",
                principalColumn: "DeckId",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaRequests_Decks_TargetDeckId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequest_Kind",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequest_TargetDeckId",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "jiten",
                table: "MediaRequests");

            migrationBuilder.DropColumn(
                name: "TargetDeckId",
                schema: "jiten",
                table: "MediaRequests");
        }
    }
}
