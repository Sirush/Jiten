using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCommunityMetadataEdits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsManual",
                schema: "jiten",
                table: "DeckTags",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MetadataLocks",
                schema: "jiten",
                table: "Decks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MetadataContributors",
                schema: "jiten",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    AdminTrusted = table.Column<bool>(type: "boolean", nullable: false),
                    BlockClearedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BannedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataContributors", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "MetadataEditSettings",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    EstablishedMinAccountDays = table.Column<int>(type: "integer", nullable: false),
                    EstablishedCountedEdits = table.Column<int>(type: "integer", nullable: false),
                    EstablishedNoRevertDays = table.Column<int>(type: "integer", nullable: false),
                    TrustedCountedEdits = table.Column<int>(type: "integer", nullable: false),
                    TrustedNoRevertDays = table.Column<int>(type: "integer", nullable: false),
                    EstablishedAutoApplyMaxTier = table.Column<int>(type: "integer", nullable: false),
                    TrustedAutoApplyMaxTier = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataEditSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeckMetadataExclusions",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckMetadataExclusions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeckMetadataExclusions_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetadataEdits",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    Actor = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    Provider = table.Column<int>(type: "integer", nullable: true),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Fields = table.Column<int>(type: "integer", nullable: false),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourcesJson = table.Column<string>(type: "jsonb", nullable: false),
                    EditNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ReviewNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RevertsEditId = table.Column<long>(type: "bigint", nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataEdits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetadataEdits_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetadataEditChecks",
                schema: "jiten",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetadataEditId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    Verdict = table.Column<int>(type: "integer", nullable: false),
                    Fields = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Reason = table.Column<int>(type: "integer", nullable: true),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    SourceUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetadataEditChecks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetadataEditChecks_MetadataEdits_MetadataEditId",
                        column: x => x.MetadataEditId,
                        principalSchema: "jiten",
                        principalTable: "MetadataEdits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeckMetadataExclusions_Deck_Kind_Value",
                schema: "jiten",
                table: "DeckMetadataExclusions",
                columns: new[] { "DeckId", "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetadataEditChecks_Edit_User",
                schema: "jiten",
                table: "MetadataEditChecks",
                columns: new[] { "MetadataEditId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MetadataEditChecks_UserId_CreatedAt",
                schema: "jiten",
                table: "MetadataEditChecks",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetadataEdits_DeckId_CreatedAt",
                schema: "jiten",
                table: "MetadataEdits",
                columns: new[] { "DeckId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetadataEdits_Status_CreatedAt",
                schema: "jiten",
                table: "MetadataEdits",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MetadataEdits_SubmissionId",
                schema: "jiten",
                table: "MetadataEdits",
                column: "SubmissionId");

            migrationBuilder.CreateIndex(
                name: "IX_MetadataEdits_UserId_CreatedAt",
                schema: "jiten",
                table: "MetadataEdits",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckMetadataExclusions",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MetadataContributors",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MetadataEditChecks",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MetadataEditSettings",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "MetadataEdits",
                schema: "jiten");

            migrationBuilder.DropColumn(
                name: "IsManual",
                schema: "jiten",
                table: "DeckTags");

            migrationBuilder.DropColumn(
                name: "MetadataLocks",
                schema: "jiten",
                table: "Decks");
        }
    }
}
