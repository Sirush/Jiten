using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddMediaListEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CurrentEntryId",
                schema: "user",
                table: "UserDeckPreferences",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UnfinishedCharacterCount",
                schema: "user",
                table: "UserAccomplishments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "UserMediaListEntries",
                schema: "user",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    FinishedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CharactersRead = table.Column<int>(type: "integer", nullable: true),
                    SeriesEntryId = table.Column<long>(type: "bigint", nullable: true),
                    CoverageAtFinish = table.Column<float>(type: "real", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserMediaListEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserMediaListEntries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "user",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserMediaListEntries_UserMediaListEntries_SeriesEntryId",
                        column: x => x.SeriesEntryId,
                        principalSchema: "user",
                        principalTable: "UserMediaListEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserDeckPreferences_CurrentEntryId",
                schema: "user",
                table: "UserDeckPreferences",
                column: "CurrentEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMediaListEntries_SeriesEntryId",
                schema: "user",
                table: "UserMediaListEntries",
                column: "SeriesEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_UserMediaListEntry_UserId_DeckId",
                schema: "user",
                table: "UserMediaListEntries",
                columns: new[] { "UserId", "DeckId" });

            migrationBuilder.AddForeignKey(
                name: "FK_UserDeckPreferences_UserMediaListEntries_CurrentEntryId",
                schema: "user",
                table: "UserDeckPreferences",
                column: "CurrentEntryId",
                principalSchema: "user",
                principalTable: "UserMediaListEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // UpdatedAt only moves when the status strengthens or a favourite is added, so it never dates a drop and may date a favourite instead.
            // It is also left undated when equal to registration (pre-AddPopularitySignals) or shared by over five writes a day (imports, bulk edits).
            migrationBuilder.Sql("""
                                 WITH dated AS (
                                     SELECT p."UserId", p."DeckId", p."Status", p."UpdatedAt",
                                            CASE WHEN p."UpdatedAt" <> u."CreatedAt" AND p."Status" IN (2, 3) AND NOT p."IsFavourite"
                                                 THEN (p."UpdatedAt" AT TIME ZONE 'UTC')::date END AS "Day"
                                     FROM "user"."UserDeckPreferences" p
                                     JOIN "user"."AspNetUsers" u ON u."Id" = p."UserId"
                                     WHERE p."Status" IN (2, 3, 4)
                                 ),
                                 busy AS (
                                     SELECT "UserId", "Day" FROM dated
                                     WHERE "Day" IS NOT NULL
                                     GROUP BY "UserId", "Day"
                                     HAVING COUNT(*) > 5
                                 )
                                 INSERT INTO "user"."UserMediaListEntries" ("UserId", "DeckId", "State", "StartedOn", "FinishedOn", "CreatedAt", "UpdatedAt")
                                 SELECT d."UserId", d."DeckId",
                                        CASE d."Status" WHEN 2 THEN 0 WHEN 3 THEN 1 ELSE 2 END,
                                        CASE WHEN d."Status" = 2 AND b."UserId" IS NULL THEN d."Day" END,
                                        CASE WHEN d."Status" IN (3, 4) AND b."UserId" IS NULL THEN d."Day" END,
                                        d."UpdatedAt",
                                        d."UpdatedAt"
                                 FROM dated d
                                 LEFT JOIN busy b ON b."UserId" = d."UserId" AND b."Day" = d."Day";

                                 UPDATE "user"."UserDeckPreferences" p
                                 SET "CurrentEntryId" = e."Id"
                                 FROM "user"."UserMediaListEntries" e
                                 WHERE e."UserId" = p."UserId" AND e."DeckId" = p."DeckId";
                                 """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_UserDeckPreferences_UserMediaListEntries_CurrentEntryId",
                schema: "user",
                table: "UserDeckPreferences");

            migrationBuilder.DropTable(
                name: "UserMediaListEntries",
                schema: "user");

            migrationBuilder.DropIndex(
                name: "IX_UserDeckPreferences_CurrentEntryId",
                schema: "user",
                table: "UserDeckPreferences");

            migrationBuilder.DropColumn(
                name: "CurrentEntryId",
                schema: "user",
                table: "UserDeckPreferences");

            migrationBuilder.DropColumn(
                name: "UnfinishedCharacterCount",
                schema: "user",
                table: "UserAccomplishments");
        }
    }
}
