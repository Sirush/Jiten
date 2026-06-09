using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaRequestCommentParent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentCommentId",
                schema: "jiten",
                table: "MediaRequestComments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaRequestComment_ParentCommentId",
                schema: "jiten",
                table: "MediaRequestComments",
                column: "ParentCommentId");

            migrationBuilder.AddForeignKey(
                name: "FK_MediaRequestComments_MediaRequestComments_ParentCommentId",
                schema: "jiten",
                table: "MediaRequestComments",
                column: "ParentCommentId",
                principalSchema: "jiten",
                principalTable: "MediaRequestComments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MediaRequestComments_MediaRequestComments_ParentCommentId",
                schema: "jiten",
                table: "MediaRequestComments");

            migrationBuilder.DropIndex(
                name: "IX_MediaRequestComment_ParentCommentId",
                schema: "jiten",
                table: "MediaRequestComments");

            migrationBuilder.DropColumn(
                name: "ParentCommentId",
                schema: "jiten",
                table: "MediaRequestComments");
        }
    }
}
