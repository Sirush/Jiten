using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class JitenPlusPromoGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PromoCodeId",
                schema: "user",
                table: "UserPromoCredits",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<bool>(
                name: "GrantsFullTier",
                schema: "user",
                table: "UserPromoCredits",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Source",
                schema: "user",
                table: "UserPromoCredits",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrantsFullTier",
                schema: "user",
                table: "UserPromoCredits");

            migrationBuilder.DropColumn(
                name: "Source",
                schema: "user",
                table: "UserPromoCredits");

            migrationBuilder.AlterColumn<int>(
                name: "PromoCodeId",
                schema: "user",
                table: "UserPromoCredits",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
