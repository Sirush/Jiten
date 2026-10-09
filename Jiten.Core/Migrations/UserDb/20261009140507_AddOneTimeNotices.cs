using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations.UserDb
{
    /// <inheritdoc />
    public partial class AddOneTimeNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DismissedNoticesJson",
                schema: "user",
                table: "UserSettings",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "GrantedNoticesJson",
                schema: "user",
                table: "UserSettings",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Snapshot of who the study-order change affects, taken before the new ordering ships: a non-global default rank
            // source plus an active media, media group or word list deck in frequency order. The notice's live check still
            // drops anyone whose source no longer resolves (deleted list, lapsed Jiten+).
            migrationBuilder.Sql("""
                INSERT INTO "user"."UserSettings" ("UserId", "GrantedNoticesJson")
                SELECT DISTINCT s."UserId", '["study-order-follows-rank-source"]'::jsonb
                FROM "user"."UserFsrsSettings" s
                JOIN "user"."UserStudyDecks" d ON d."UserId" = s."UserId"
                WHERE d."IsActive"
                  AND d."Order" = 2
                  AND d."DeckType" IN (0, 2, 4)
                  AND ((jsonb_typeof(s."SettingsJson" -> 'defaultFrequencyMediaType') = 'number'
                        AND (s."SettingsJson" ->> 'defaultFrequencyMediaType')::int > 0)
                    OR (jsonb_typeof(s."SettingsJson" -> 'defaultFrequencyListId') = 'number'
                        AND (s."SettingsJson" ->> 'defaultFrequencyListId')::bigint > 0))
                ON CONFLICT ("UserId") DO UPDATE SET "GrantedNoticesJson" = EXCLUDED."GrantedNoticesJson";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DismissedNoticesJson",
                schema: "user",
                table: "UserSettings");

            migrationBuilder.DropColumn(
                name: "GrantedNoticesJson",
                schema: "user",
                table: "UserSettings");
        }
    }
}
