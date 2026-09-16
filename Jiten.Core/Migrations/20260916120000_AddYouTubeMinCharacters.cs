using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeMinCharacters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MinCharacters",
                schema: "jiten",
                table: "YouTubeSources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinCharacters",
                schema: "jiten",
                table: "YouTubeRegistrations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MinCharacters",
                schema: "jiten",
                table: "YouTubeSources");

            migrationBuilder.DropColumn(
                name: "MinCharacters",
                schema: "jiten",
                table: "YouTubeRegistrations");
        }
    }
}
