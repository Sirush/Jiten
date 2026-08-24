using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaRequestUpvoteUserIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestUpvote_UserId_RequestId",
                schema: "jiten",
                table: "MediaRequestUpvotes",
                columns: new[] { "UserId", "MediaRequestId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaRequestUpvote_UserId_RequestId",
                schema: "jiten",
                table: "MediaRequestUpvotes");
        }
    }
}
