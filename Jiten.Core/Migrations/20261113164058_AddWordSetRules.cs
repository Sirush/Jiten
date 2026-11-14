using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddWordSetRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RegeneratedAt",
                schema: "jiten",
                table: "WordSets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleJson",
                schema: "jiten",
                table: "WordSets",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsManual",
                schema: "jiten",
                table: "WordSetMembers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "WordSetExclusions",
                schema: "jiten",
                columns: table => new
                {
                    SetId = table.Column<int>(type: "integer", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordSetExclusions", x => new { x.SetId, x.WordId, x.ReadingIndex });
                    table.ForeignKey(
                        name: "FK_WordSetExclusions_WordSets_SetId",
                        column: x => x.SetId,
                        principalSchema: "jiten",
                        principalTable: "WordSets",
                        principalColumn: "SetId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                UPDATE jiten."WordSets" s
                SET "RuleJson" = jsonb_build_object('IncludePos', to_jsonb(r.pos), 'AddKanaForm', true)
                FROM (VALUES
                    ('onomatopoeia', ARRAY['on-mim']),
                    ('counters', ARRAY['ctr']),
                    ('names', ARRAY['name-fem', 'name-masc', 'given', 'surname', 'unclass']),
                    ('people', ARRAY['person']),
                    ('places', ARRAY['place']),
                    ('other-names', ARRAY['char', 'company', 'group', 'product', 'serv', 'station', 'ship', 'work'])
                ) AS r(slug, pos)
                WHERE s."Slug" = r.slug;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WordSetExclusions",
                schema: "jiten");

            migrationBuilder.DropColumn(
                name: "RegeneratedAt",
                schema: "jiten",
                table: "WordSets");

            migrationBuilder.DropColumn(
                name: "RuleJson",
                schema: "jiten",
                table: "WordSets");

            migrationBuilder.DropColumn(
                name: "IsManual",
                schema: "jiten",
                table: "WordSetMembers");
        }
    }
}
