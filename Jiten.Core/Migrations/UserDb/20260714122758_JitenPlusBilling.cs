using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class JitenPlusBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AdminPremiumOverride",
                schema: "user",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLifetime",
                schema: "user",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "LifetimeSource",
                schema: "user",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StripeCustomerId",
                schema: "user",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StripeSubscriptionActive",
                schema: "user",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "StripeSubscriptionId",
                schema: "user",
                table: "AspNetUsers",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubscriptionPeriodEnd",
                schema: "user",
                table: "AspNetUsers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubscriptionPlan",
                schema: "user",
                table: "AspNetUsers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PromoCodes",
                schema: "user",
                columns: table => new
                {
                    CodeId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DurationDays = table.Column<int>(type: "integer", nullable: false),
                    MaxUses = table.Column<int>(type: "integer", nullable: true),
                    CurrentUses = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    GrantsFullTier = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromoCodes", x => x.CodeId);
                });

            migrationBuilder.CreateTable(
                name: "UserPromoCredits",
                schema: "user",
                columns: table => new
                {
                    UserPromoCreditId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromoCodeId = table.Column<int>(type: "integer", nullable: false),
                    RemainingDays = table.Column<int>(type: "integer", nullable: false),
                    GrantedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastDecrementDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FullyUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ThankYouMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPromoCredits", x => x.UserPromoCreditId);
                    table.ForeignKey(
                        name: "FK_UserPromoCredits_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserPromoCredits_PromoCodes_PromoCodeId",
                        column: x => x.PromoCodeId,
                        principalSchema: "user",
                        principalTable: "PromoCodes",
                        principalColumn: "CodeId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromoCode_Code",
                schema: "user",
                table: "PromoCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPromoCredit_UserId",
                schema: "user",
                table: "UserPromoCredits",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserPromoCredit_UserId_PromoCodeId",
                schema: "user",
                table: "UserPromoCredits",
                columns: new[] { "UserId", "PromoCodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPromoCredits_PromoCodeId",
                schema: "user",
                table: "UserPromoCredits",
                column: "PromoCodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserPromoCredits",
                schema: "user");

            migrationBuilder.DropTable(
                name: "PromoCodes",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "AdminPremiumOverride",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IsLifetime",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "LifetimeSource",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeCustomerId",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionActive",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "StripeSubscriptionId",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SubscriptionPeriodEnd",
                schema: "user",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "SubscriptionPlan",
                schema: "user",
                table: "AspNetUsers");
        }
    }
}
