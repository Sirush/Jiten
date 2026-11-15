using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jiten.Core.Migrations
{
    /// <inheritdoc />
    public partial class TrimDeckMetadata : Migration
    {
        private const string Edge = @"[\s   -     　﻿]";
        private const string HasEdge = $"'^{Edge}|{Edge}$'";
        private const string TrimPattern = $"'^{Edge}+|{Edge}+$', '', 'g'";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($"""
                WITH cleaned AS (
                    SELECT "DeckTitleId", "DeckId", "Title", regexp_replace("Title", {TrimPattern}) AS trimmed
                    FROM jiten."DeckTitles"
                    WHERE "TitleType" = 3
                ),
                ranked AS (
                    SELECT "DeckTitleId", trimmed,
                           row_number() OVER (PARTITION BY "DeckId", trimmed ORDER BY ("Title" = trimmed) DESC, "DeckTitleId") AS rn
                    FROM cleaned
                )
                DELETE FROM jiten."DeckTitles" d
                USING ranked r
                WHERE d."DeckTitleId" = r."DeckTitleId" AND (r.rn > 1 OR r.trimmed = '');
                """);

            migrationBuilder.Sql($"""
                UPDATE jiten."DeckTitles"
                SET "Title" = regexp_replace("Title", {TrimPattern})
                WHERE "TitleType" = 3 AND "Title" ~ {HasEdge};
                """);

            migrationBuilder.Sql($"""
                UPDATE jiten."Decks"
                SET "OriginalTitle" = CASE
                        WHEN regexp_replace("OriginalTitle", {TrimPattern}) = '' THEN "OriginalTitle"
                        ELSE regexp_replace("OriginalTitle", {TrimPattern}) END,
                    "RomajiTitle" = NULLIF(regexp_replace("RomajiTitle", {TrimPattern}), ''),
                    "EnglishTitle" = NULLIF(regexp_replace("EnglishTitle", {TrimPattern}), ''),
                    "Description" = NULLIF(regexp_replace("Description", {TrimPattern}), '')
                WHERE "OriginalTitle" ~ {HasEdge} OR "RomajiTitle" ~ {HasEdge} OR "EnglishTitle" ~ {HasEdge} OR "Description" ~ {HasEdge};
                """);

            migrationBuilder.Sql($"""
                UPDATE jiten."YouTubeVideos"
                SET "Title" = regexp_replace("Title", {TrimPattern})
                WHERE "Title" ~ {HasEdge} AND regexp_replace("Title", {TrimPattern}) <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
