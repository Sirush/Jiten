using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class OnboardingState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OnboardingJson",
                schema: "user",
                table: "UserSettings",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "ReadingCheckRuns",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Units = table.Column<int>(type: "integer", nullable: false),
                    Candidates = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Seeded = table.Column<int>(type: "integer", nullable: false),
                    A = table.Column<double>(type: "double precision", nullable: false),
                    B = table.Column<double>(type: "double precision", nullable: false),
                    C = table.Column<double>(type: "double precision", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadingCheckRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadingCheckRuns_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReadingCheckRuns_UserId_CreatedAt",
                schema: "user",
                table: "ReadingCheckRuns",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OnboardingJson",
                schema: "user",
                table: "UserSettings");

            migrationBuilder.DropTable(
                name: "ReadingCheckRuns",
                schema: "user");
        }
    }
}
