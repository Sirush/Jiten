using System.Globalization;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public sealed record SeriesConversionMember(int DeckId, string Title, DateOnly ReleaseDate);

public sealed record SeriesConversionGroup(int SeriesId, string Name, SeriesKind Kind, List<SeriesConversionMember> Members);

public sealed record SeriesConversionResult(int RelationRows, List<SeriesConversionGroup> Groups);

/// <summary>Turns the pairwise SameSeries (7) / SameSetting (8) rows into Series membership.</summary>
public static class SeriesRelationConversion
{
    /// <summary>
    /// PostgreSQL only; must run inside a transaction (temp tables drop on commit). Each connected component of type 7
    /// edges becomes a Series, of type 8 edges a Setting, named after the earliest-release member (unknown dates last).
    /// </summary>
    public static readonly string Sql = $"""
        CREATE TEMP TABLE series_conv_edges ON COMMIT DROP AS
        SELECT "SourceDeckId" AS a, "TargetDeckId" AS b, "RelationshipType" AS t
        FROM jiten."DeckRelationships"
        WHERE "RelationshipType" IN (7, 8) AND "SourceDeckId" <> "TargetDeckId"
        UNION
        SELECT "TargetDeckId", "SourceDeckId", "RelationshipType"
        FROM jiten."DeckRelationships"
        WHERE "RelationshipType" IN (7, 8) AND "SourceDeckId" <> "TargetDeckId";

        CREATE TEMP TABLE series_conv_components ON COMMIT DROP AS
        WITH RECURSIVE reach(t, root, deck) AS (
            SELECT DISTINCT t, a, a FROM series_conv_edges
            UNION
            SELECT r.t, r.root, e.b
            FROM reach r
            JOIN series_conv_edges e ON e.t = r.t AND e.a = r.deck
        )
        SELECT t, deck, MIN(root) AS comp FROM reach GROUP BY t, deck;

        CREATE TEMP TABLE series_conv_groups ON COMMIT DROP AS
        SELECT g.t, g.comp,
               nextval(pg_get_serial_sequence('jiten."Series"', 'SeriesId'))::int AS series_id,
               (SELECT d."OriginalTitle"
                FROM series_conv_components c
                JOIN jiten."Decks" d ON d."DeckId" = c.deck
                WHERE c.t = g.t AND c.comp = g.comp
                ORDER BY d."ReleaseDate" < DATE '{FranchiseNaming.UnknownReleaseCutoff.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}', d."ReleaseDate", d."DeckId"
                LIMIT 1) AS name
        FROM (SELECT DISTINCT t, comp FROM series_conv_components) g;

        INSERT INTO jiten."Series" ("SeriesId", "Name", "Kind", "CreatedAt", "UpdatedAt")
        SELECT series_id, LEFT(COALESCE(name, 'Unnamed'), {FranchiseNaming.MaxNameLength}), CASE WHEN t = 7 THEN 1 ELSE 2 END, now(), now()
        FROM series_conv_groups;

        INSERT INTO jiten."SeriesMembers" ("SeriesId", "DeckId")
        SELECT g.series_id, c.deck
        FROM series_conv_components c
        JOIN series_conv_groups g ON g.t = c.t AND g.comp = c.comp;

        DELETE FROM jiten."DeckRelationships" WHERE "RelationshipType" IN (7, 8);
        """;

    /// <summary>Runs <see cref="Sql"/> in a transaction and reports the groups it created; only commits when <paramref name="apply"/> is set.</summary>
    public static async Task<SeriesConversionResult> RunAsync(JitenDbContext db, bool apply, CancellationToken ct = default)
    {
        var relationRows = await db.DeckRelationships.CountAsync(r => (int)r.RelationshipType == 7 || (int)r.RelationshipType == 8, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(Sql, ct);

        var createdIds = await db.Database
                                 .SqlQueryRaw<int>("SELECT series_id AS \"Value\" FROM series_conv_groups")
                                 .ToListAsync(ct);

        var series = await db.Series.AsNoTracking()
                             .Where(s => createdIds.Contains(s.SeriesId))
                             .Select(s => new
                             {
                                 s.SeriesId, s.Name, s.Kind,
                                 Members = s.Members.Select(m => new SeriesConversionMember(m.DeckId, m.Deck.OriginalTitle, m.Deck.ReleaseDate)).ToList()
                             })
                             .ToListAsync(ct);

        var groups = series.OrderBy(s => s.Kind).ThenBy(s => s.Name)
                           .Select(s => new SeriesConversionGroup(s.SeriesId, s.Name, s.Kind,
                                                                  s.Members.OrderBy(m => m.ReleaseDate).ThenBy(m => m.DeckId).ToList()))
                           .ToList();

        if (apply)
            await transaction.CommitAsync(ct);
        else
            await transaction.RollbackAsync(ct);

        return new SeriesConversionResult(relationRows, groups);
    }
}
