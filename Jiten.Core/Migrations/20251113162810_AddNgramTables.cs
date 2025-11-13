using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddNgramTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NgramStatistics",
                schema: "jiten",
                columns: table => new
                {
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    TotalNgrams = table.Column<int>(type: "integer", nullable: false),
                    SignificantNgrams = table.Column<int>(type: "integer", nullable: false),
                    AvgSignificanceScore = table.Column<float>(type: "real", nullable: false),
                    BertEmbeddingsComputed = table.Column<int>(type: "integer", nullable: false),
                    LastProcessed = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AmbiguityScore = table.Column<float>(type: "real", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NgramStatistics", x => x.WordId);
                    table.ForeignKey(
                        name: "FK_NgramStatistics_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrecomputedNgrams",
                schema: "jiten",
                columns: table => new
                {
                    NgramId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WordId = table.Column<int>(type: "integer", nullable: false),
                    ReadingIndex = table.Column<byte>(type: "smallint", nullable: false),
                    ContextBefore = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContextAfter = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ContextSize = table.Column<short>(type: "smallint", nullable: false),
                    TokensBefore = table.Column<short>(type: "smallint", nullable: false),
                    TokensAfter = table.Column<short>(type: "smallint", nullable: false),
                    FullContext = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Occurrences = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    SignificanceScore = table.Column<float>(type: "real", nullable: false),
                    BertEmbedding = table.Column<float[]>(type: "real[]", nullable: true),
                    BertEmbeddingComputed = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LastUpdated = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrecomputedNgrams", x => x.NgramId);
                    table.ForeignKey(
                        name: "FK_PrecomputedNgrams_Words_WordId",
                        column: x => x.WordId,
                        principalSchema: "jmdict",
                        principalTable: "Words",
                        principalColumn: "WordId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NgramProcessingQueue",
                schema: "jiten",
                columns: table => new
                {
                    QueueId = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    NgramId = table.Column<int>(type: "integer", nullable: false),
                    Priority = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    RetryCount = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)0),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "NOW()"),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NgramProcessingQueue", x => x.QueueId);
                    table.ForeignKey(
                        name: "FK_NgramProcessingQueue_PrecomputedNgrams_NgramId",
                        column: x => x.NgramId,
                        principalSchema: "jiten",
                        principalTable: "PrecomputedNgrams",
                        principalColumn: "NgramId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NgramSources",
                schema: "jiten",
                columns: table => new
                {
                    NgramId = table.Column<int>(type: "integer", nullable: false),
                    ExampleSentenceId = table.Column<int>(type: "integer", nullable: false),
                    WordPosition = table.Column<short>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NgramSources", x => new { x.NgramId, x.ExampleSentenceId });
                    table.ForeignKey(
                        name: "FK_NgramSources_ExampleSentences_ExampleSentenceId",
                        column: x => x.ExampleSentenceId,
                        principalSchema: "jiten",
                        principalTable: "ExampleSentences",
                        principalColumn: "SentenceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NgramSources_PrecomputedNgrams_NgramId",
                        column: x => x.NgramId,
                        principalSchema: "jiten",
                        principalTable: "PrecomputedNgrams",
                        principalColumn: "NgramId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NgramProcessingQueue_NgramId",
                schema: "jiten",
                table: "NgramProcessingQueue",
                column: "NgramId");

            migrationBuilder.CreateIndex(
                name: "IX_NgramProcessingQueue_Status_Priority",
                schema: "jiten",
                table: "NgramProcessingQueue",
                columns: new[] { "Status", "Priority" },
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_NgramSources_ExampleSentenceId",
                schema: "jiten",
                table: "NgramSources",
                column: "ExampleSentenceId");

            migrationBuilder.CreateIndex(
                name: "IX_NgramSources_NgramId",
                schema: "jiten",
                table: "NgramSources",
                column: "NgramId");

            migrationBuilder.CreateIndex(
                name: "IX_NgramStatistics_AmbiguityScore",
                schema: "jiten",
                table: "NgramStatistics",
                column: "AmbiguityScore");

            migrationBuilder.CreateIndex(
                name: "IX_NgramStatistics_LastProcessed",
                schema: "jiten",
                table: "NgramStatistics",
                column: "LastProcessed");

            migrationBuilder.CreateIndex(
                name: "IX_PrecomputedNgrams_BertEmbeddingComputed",
                schema: "jiten",
                table: "PrecomputedNgrams",
                column: "BertEmbeddingComputed",
                filter: "\"BertEmbeddingComputed\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_PrecomputedNgrams_HighSignificance",
                schema: "jiten",
                table: "PrecomputedNgrams",
                columns: new[] { "WordId", "ReadingIndex", "SignificanceScore" },
                filter: "\"SignificanceScore\" > 0.5");

            migrationBuilder.CreateIndex(
                name: "IX_PrecomputedNgrams_WordId_ReadingIndex",
                schema: "jiten",
                table: "PrecomputedNgrams",
                columns: new[] { "WordId", "ReadingIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_PrecomputedNgrams_WordId_SignificanceScore",
                schema: "jiten",
                table: "PrecomputedNgrams",
                columns: new[] { "WordId", "SignificanceScore" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NgramProcessingQueue",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "NgramSources",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "NgramStatistics",
                schema: "jiten");

            migrationBuilder.DropTable(
                name: "PrecomputedNgrams",
                schema: "jiten");
        }
    }
}
