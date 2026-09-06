using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddYouTubeSourceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CheckIntervalDays",
                schema: "jiten",
                table: "YouTubeSources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRuntimeSeconds",
                schema: "jiten",
                table: "YouTubeSources",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MinRuntimeSeconds",
                schema: "jiten",
                table: "YouTubeSources",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckIntervalDays",
                schema: "jiten",
                table: "YouTubeSources");

            migrationBuilder.DropColumn(
                name: "MaxRuntimeSeconds",
                schema: "jiten",
                table: "YouTubeSources");

            migrationBuilder.DropColumn(
                name: "MinRuntimeSeconds",
                schema: "jiten",
                table: "YouTubeSources");
        }
    }
}
