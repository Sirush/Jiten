using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CommunityLists",
                schema: "jiten",
                columns: table => new
                {
                    ListId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Slug = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FavouriteCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    FavouriteMilestoneNotified = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ItemCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityLists", x => x.ListId);
                });

            migrationBuilder.CreateTable(
                name: "CommunityListActivityDaily",
                schema: "jiten",
                columns: table => new
                {
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Views = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityListActivityDaily", x => new { x.ListId, x.Date });
                    table.ForeignKey(
                        name: "FK_CommunityListActivityDaily_CommunityLists_ListId",
                        column: x => x.ListId,
                        principalSchema: "jiten",
                        principalTable: "CommunityLists",
                        principalColumn: "ListId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityListFavourites",
                schema: "jiten",
                columns: table => new
                {
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityListFavourites", x => new { x.ListId, x.UserId });
                    table.ForeignKey(
                        name: "FK_CommunityListFavourites_CommunityLists_ListId",
                        column: x => x.ListId,
                        principalSchema: "jiten",
                        principalTable: "CommunityLists",
                        principalColumn: "ListId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityListGenres",
                schema: "jiten",
                columns: table => new
                {
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    Genre = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityListGenres", x => new { x.ListId, x.Genre });
                    table.ForeignKey(
                        name: "FK_CommunityListGenres_CommunityLists_ListId",
                        column: x => x.ListId,
                        principalSchema: "jiten",
                        principalTable: "CommunityLists",
                        principalColumn: "ListId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityListItems",
                schema: "jiten",
                columns: table => new
                {
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityListItems", x => new { x.ListId, x.DeckId });
                    table.ForeignKey(
                        name: "FK_CommunityListItems_CommunityLists_ListId",
                        column: x => x.ListId,
                        principalSchema: "jiten",
                        principalTable: "CommunityLists",
                        principalColumn: "ListId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CommunityListItems_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityListReports",
                schema: "jiten",
                columns: table => new
                {
                    ReportId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    ReporterId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityListReports", x => x.ReportId);
                    table.ForeignKey(
                        name: "FK_CommunityListReports_CommunityLists_ListId",
                        column: x => x.ListId,
                        principalSchema: "jiten",
                        principalTable: "CommunityLists",
                        principalColumn: "ListId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommunityListTags",
                schema: "jiten",
                columns: table => new
                {
                    ListId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommunityListTags", x => new { x.ListId, x.TagId });
                    table.ForeignKey(
                        name: "FK_CommunityListTags_CommunityLists_ListId",
                        column: x => x.ListId,
                        principalSchema: "jiten",
                        principalTable: "CommunityLists",
                        principalColumn: "ListId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CommunityListTags_Tags_TagId",
                        column: x => x.TagId,
                        principalSchema: "jiten",
                        principalTable: "Tags",
                        principalColumn: "TagId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListFavourite_CreatedAt_ListId",
                schema: "jiten",
                table: "CommunityListFavourites",
                columns: new[] { "CreatedAt", "ListId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListFavourite_UserId",
                schema: "jiten",
                table: "CommunityListFavourites",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListGenre_Genre_ListId",
                schema: "jiten",
                table: "CommunityListGenres",
                columns: new[] { "Genre", "ListId" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListItem_DeckId",
                schema: "jiten",
                table: "CommunityListItems",
                column: "DeckId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListItem_ListId_Position",
                schema: "jiten",
                table: "CommunityListItems",
                columns: new[] { "ListId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListReport_Open_ListId_ReporterId",
                schema: "jiten",
                table: "CommunityListReports",
                columns: new[] { "ListId", "ReporterId" },
                unique: true,
                filter: "\"ResolvedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListReport_ResolvedAt",
                schema: "jiten",
                table: "CommunityListReports",
                column: "ResolvedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityList_OwnerId",
                schema: "jiten",
                table: "CommunityLists",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_CommunityList_Status_FavouriteCount",
                schema: "jiten",
                table: "CommunityLists",
                columns: new[] { "Status", "FavouriteCount" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityList_Status_PublishedAt",
                schema: "jiten",
                table: "CommunityLists",
                columns: new[] { "Status", "PublishedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityList_Status_UpdatedAt",
                schema: "jiten",
                table: "CommunityLists",
                columns: new[] { "Status", "UpdatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_CommunityListTag_TagId_ListId",
                schema: "jiten",
                table: "CommunityListTags",
                columns: new[] { "TagId", "ListId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CommunityListActivityDaily",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "CommunityListFavourites",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "CommunityListGenres",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "CommunityListItems",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "CommunityListReports",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "CommunityListTags",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "CommunityLists",
                schema: "jiten");
        }
    }
}
