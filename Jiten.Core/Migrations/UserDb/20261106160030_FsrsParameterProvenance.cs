using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class FsrsParameterProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoOptimise",
                schema: "user",
                table: "UserFsrsSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutoOptimiseAttemptedAtUtc",
                schema: "user",
                table: "UserFsrsSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutoOptimiseAttemptedReviewCount",
                schema: "user",
                table: "UserFsrsSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "AutoOptimiseLastOutcome",
                schema: "user",
                table: "UserFsrsSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParametersFittedAtUtc",
                schema: "user",
                table: "UserFsrsSettings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ParametersFittedReviewCount",
                schema: "user",
                table: "UserFsrsSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ParametersSource",
                schema: "user",
                table: "UserFsrsSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""UPDATE "user"."UserFsrsSettings" SET "ParametersSource" = 4 WHERE "ParametersJson" <> '[]'::jsonb;""");

            migrationBuilder.CreateTable(
                name: "FsrsParameterHistories",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParametersJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    Loss = table.Column<double>(type: "double precision", nullable: true),
                    PreviousLoss = table.Column<double>(type: "double precision", nullable: true),
                    ReviewCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevertedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FsrsParameterHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FsrsParameterHistories_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FsrsParameterHistories_UserId_CreatedAtUtc",
                schema: "user",
                table: "FsrsParameterHistories",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FsrsParameterHistories",
                schema: "user");

            migrationBuilder.DropColumn(
                name: "AutoOptimise",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "AutoOptimiseAttemptedAtUtc",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "AutoOptimiseAttemptedReviewCount",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "AutoOptimiseLastOutcome",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "ParametersFittedAtUtc",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "ParametersFittedReviewCount",
                schema: "user",
                table: "UserFsrsSettings");

            migrationBuilder.DropColumn(
                name: "ParametersSource",
                schema: "user",
                table: "UserFsrsSettings");
        }
    }
}
