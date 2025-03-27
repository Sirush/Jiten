using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialBeta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "jiten");

            migrationBuilder.EnsureSchema(
                name: "jmdict");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:fuzzystrmatch", ",,");

            migrationBuilder.CreateTable(
                name: "Decks",
                schema: "jiten",
                columns: table => new
                {
                    DeckId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreationDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUpdate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CoverName = table.Column<string>(type: "text", nullable: false),
                    MediaType = table.Column<int>(type: "integer", nullable: false),
                    OriginalTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RomajiTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    EnglishTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CharacterCount = table.Column<int>(type: "integer", nullable: false),
                    WordCount = table.Column<int>(type: "integer", nullable: false),
                    UniqueWordCount = table.Column<int>(type: "integer", nullable: false),
                    UniqueWordUsedOnceCount = table.Column<int>(type: "integer", nullable: false),
                    UniqueKanjiCount = table.Column<int>(type: "integer", nullable: false),
                    UniqueKanjiUsedOnceCount = table.Column<int>(type: "integer", nullable: false),
                    Difficulty = table.Column<int>(type: "integer", nullable: false),
                    SentenceCount = table.Column<int>(type: "integer", nullable: false),
                    ParentDeckId = table.Column<int>(type: "integer", nullable: true),
                    DeckOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Decks", x => x.DeckId);
                    table.ForeignKey(
                        name: "FK_Decks_Decks_ParentDeckId",
                        column: x => x.ParentDeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Words",
                schema: "jmdict",
                columns: table => new
                {
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    Readings = table.Column<List<string>>(type: "text[]", nullable: false),
                    ReadingsFurigana = table.Column<List<string>>(type: "text[]", nullable: false),
                    ReadingTypes = table.Column<int[]>(type: "int[]", nullable: false),
                    ObsoleteReadings = table.Column<List<string>>(type: "text[]", nullable: true),
                    PartsOfSpeech = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Words", x => x.WordId);
                });

            migrationBuilder.CreateTable(
                name: "DeckWords",
                schema: "jiten",
                columns: table => new
                {
                    DeckWordId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeckId = table.Column<int>(type: "integer", nullable: false),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    OriginalText = table.Column<string>(type: "text", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    Occurrences = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeckWords", x => x.DeckWordId);
                    table.ForeignKey(
                        name: "FK_DeckWords_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Links",
                schema: "jiten",
                columns: table => new
                {
                    LinkId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LinkType = table.Column<int>(type: "integer", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    DeckId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Links", x => x.LinkId);
                    table.ForeignKey(
                        name: "FK_Links_Decks_DeckId",
                        column: x => x.DeckId,
                        principalSchema: "jiten",
                        principalTable: "Decks",
                        principalColumn: "DeckId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Definitions",
                schema: "jmdict",
                columns: table => new
                {
                    DefinitionId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    PartsOfSpeech = table.Column<List<string>>(type: "text[]", nullable: false),
                    EnglishMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    DutchMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    FrenchMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    GermanMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    SpanishMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    HungarianMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    RussianMeanings = table.Column<List<string>>(type: "text[]", nullable: false),
                    SlovenianMeanings = table.Column<List<string>>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Definitions", x => x.DefinitionId);
                    table.ForeignKey(
                        name: "FK_Definitions_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Lookups",
                schema: "jmdict",
                columns: table => new
                {
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    LookupKey = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lookups", x => new { x.WordId, x.LookupKey });
                    table.ForeignKey(
                        name: "FK_Lookups_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WordFrequencies",
                schema: "jmdict",
                columns: table => new
                {
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    FrequencyRank = table.Column<int>(type: "integer", nullable: false),
                    UsedInMediaAmount = table.Column<int>(type: "integer", nullable: false),
                    ObservedFrequency = table.Column<double>(type: "double precision", nullable: false),
                    ReadingsFrequencyRank = table.Column<List<int>>(type: "integer[]", nullable: false),
                    ReadingsFrequencyPercentage = table.Column<List<double>>(type: "double precision[]", nullable: false),
                    ReadingsObservedFrequency = table.Column<List<double>>(type: "double precision[]", nullable: false),
                    ReadingsUsedInMediaAmount = table.Column<List<int>>(type: "integer[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WordFrequencies", x => x.WordId);
                    table.ForeignKey(
                        name: "FK_WordFrequencies_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Decks_ParentDeckId",
                schema: "jiten",
                table: "Decks",
                column: "ParentDeckId");

            migrationBuilder.CreateIndex(
                name: "IX_EnglishTitle",
                schema: "jiten",
                table: "Decks",
                column: "EnglishTitle");

            migrationBuilder.CreateIndex(
                name: "IX_MediaType",
                schema: "jiten",
                table: "Decks",
                column: "MediaType");

            migrationBuilder.CreateIndex(
                name: "IX_OriginalTitle",
                schema: "jiten",
                table: "Decks",
                column: "OriginalTitle");

            migrationBuilder.CreateIndex(
                name: "IX_RomajiTitle",
                schema: "jiten",
                table: "Decks",
                column: "RomajiTitle");

            migrationBuilder.CreateIndex(
                name: "IX_DeckId",
                schema: "jiten",
                table: "DeckWords",
                column: "DeckId");

            migrationBuilder.CreateIndex(
                name: "IX_DeckWordReadingIndexDeck",
                schema: "jiten",
                table: "DeckWords",
                columns: new[] { "WordId", "ReadingIndex", "DeckId" });

            migrationBuilder.CreateIndex(
                name: "IX_WordReadingIndex",
                schema: "jiten",
                table: "DeckWords",
                columns: new[] { "WordId", "ReadingIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_Definitions_WordId",
                schema: "jmdict",
                table: "Definitions",
                column: "WordId");

            migrationBuilder.CreateIndex(
                name: "IX_Links_DeckId",
                schema: "jiten",
                table: "Links",
                column: "DeckId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeckWords",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "Definitions",
                schema: "jmdict");

            migrationBuilder.DropTable(
                name: "Links",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "Lookups",
                schema: "jmdict");

            migrationBuilder.DropTable(
                name: "WordFrequencies",
                schema: "jmdict");

            migrationBuilder.DropTable(
                name: "Decks",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "Words",
                schema: "jmdict");
        }
    }
}
