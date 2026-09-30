using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public static class SentenceProfileService
{
    public const int SampleSize = 5000;

    public static async Task SaveProfilesAsync(IDbContextFactory<JitenDbContext> contextFactory,
                                               IReadOnlyCollection<(int DeckId, byte[] Profile)> profiles)
    {
        if (profiles.Count == 0) return;

        await using var context = await contextFactory.CreateDbContextAsync();
        var deckIds = profiles.Select(p => p.DeckId).ToList();
        var existing = await context.DeckSentenceProfiles.Where(p => deckIds.Contains(p.DeckId)).ToDictionaryAsync(p => p.DeckId);

        var now = DateTime.UtcNow;
        foreach (var (deckId, profile) in profiles)
        {
            if (!existing.TryGetValue(deckId, out var row))
            {
                row = new DeckSentenceProfile { DeckId = deckId };
                context.DeckSentenceProfiles.Add(row);
                existing[deckId] = row;
            }

            row.Profile = profile;
            row.SentenceCount = SentenceProfileCodec.ReadSentenceCount(profile);
            row.Version = SentenceProfileCodec.FormatVersion;
            row.BuiltAt = now;
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Rebuilds the sample of the top-level deck above <paramref name="deckId"/> from its children's profiles, or its own.
    /// A title of at most <see cref="SampleSize"/> sentences gets none: its profiles are read whole, so a copy would only take space.
    /// </summary>
    public static async Task RebuildSampleAsync(IDbContextFactory<JitenDbContext> contextFactory, int deckId)
    {
        await using var context = await contextFactory.CreateDbContextAsync();
        var rootId = await context.Decks.AsNoTracking()
                                  .Where(d => d.DeckId == deckId)
                                  .Select(d => d.ParentDeckId ?? d.DeckId)
                                  .FirstOrDefaultAsync();
        if (rootId == 0) return;

        var childIds = await context.Decks.AsNoTracking()
                                    .Where(d => d.ParentDeckId == rootId)
                                    .OrderBy(d => d.DeckOrder).ThenBy(d => d.DeckId)
                                    .Select(d => d.DeckId)
                                    .ToListAsync();
        var partIds = childIds.Count > 0 ? childIds : [rootId];

        var blobs = await context.DeckSentenceProfiles.AsNoTracking()
                                 .Where(p => partIds.Contains(p.DeckId) && p.Profile != null)
                                 .Select(p => new { p.DeckId, p.Profile })
                                 .ToDictionaryAsync(p => p.DeckId, p => p.Profile!);
        var parts = partIds.Where(blobs.ContainsKey).Select(id => SentenceProfileCodec.Decode(blobs[id])).ToList();

        var needsSample = parts.Sum(p => p.SentenceCount) > SampleSize;
        var root = await context.DeckSentenceProfiles.FirstOrDefaultAsync(p => p.DeckId == rootId);
        if (root == null)
        {
            if (!needsSample) return;
            root = new DeckSentenceProfile { DeckId = rootId, Version = SentenceProfileCodec.FormatVersion };
            context.DeckSentenceProfiles.Add(root);
        }

        root.Sample = needsSample ? SentenceProfileCodec.Sample(parts, SampleSize) : null;
        root.SampleCount = root.Sample != null ? SentenceProfileCodec.ReadSentenceCount(root.Sample) : 0;
        root.BuiltAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Swaps a word key for its replacements in the given decks' profiles and samples, as the admin replace (one key),
    /// split (several) and remove (none) do to example sentence tokens.
    /// </summary>
    public static async Task<int> RewriteWordAsync(JitenDbContext context, IReadOnlyCollection<int> deckIds, int oldKey,
                                                   IReadOnlyList<int> newKeys)
    {
        const int chunkSize = 200;
        int changed = 0;
        foreach (var chunk in deckIds.Chunk(chunkSize))
        {
            var rows = await context.DeckSentenceProfiles.Where(p => chunk.Contains(p.DeckId)).ToListAsync();
            foreach (var row in rows)
            {
                bool touched = false;
                if (row.Profile != null && Rewrite(row.Profile, oldKey, newKeys) is { } profile)
                {
                    row.Profile = profile;
                    row.SentenceCount = SentenceProfileCodec.ReadSentenceCount(profile);
                    touched = true;
                }

                if (row.Sample != null && Rewrite(row.Sample, oldKey, newKeys) is { } sample)
                {
                    row.Sample = sample;
                    row.SampleCount = SentenceProfileCodec.ReadSentenceCount(sample);
                    touched = true;
                }

                if (touched) changed++;
            }

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        return changed;
    }

    /// <summary>Null when the key is absent, so untouched blobs are not rewritten.</summary>
    private static byte[]? Rewrite(byte[] blob, int oldKey, IReadOnlyList<int> newKeys)
    {
        var profile = SentenceProfileCodec.Decode(blob);
        if (!profile.Keys.Contains(oldKey)) return null;

        var sentences = new List<IReadOnlyCollection<int>>(profile.SentenceCount);
        foreach (var keys in SentenceProfileCodec.Sentences(profile))
        {
            if (!keys.Contains(oldKey))
            {
                sentences.Add(keys);
                continue;
            }

            var rewritten = keys.Where(k => k != oldKey).Concat(newKeys).Distinct().ToArray();

            if (rewritten.Length > 0) sentences.Add(rewritten);
        }

        return SentenceProfileCodec.Encode(sentences);
    }
}
