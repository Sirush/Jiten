using System.Text;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Jiten.Api.Services.MetadataEdits;

public sealed class CharacterNameCheckService(JitenDbContext db, IDbContextFactory<JitenDbContext> contextFactory, IMemoryCache cache)
{
    public const int MaxExamples = 3;

    /// <summary>Characters kept on each side of the name in an excerpt.</summary>
    public const int ExampleContext = 24;

    /// <summary>Deck text changes only on reparse, so a scan stays valid long enough to serve every queue load in between.</summary>
    private static readonly TimeSpan ScanCacheLifetime = TimeSpan.FromMinutes(15);

    public const int MinNameLength = 2;
    public const int MaxNameLength = 20;
    public const int MaxPreviewLines = 50;
    public const int MaxPreviewExamples = 3;

    /// <summary>Lines longer than this are cut to a window around the first match, which bounds parse cost on paragraph-per-line text.</summary>
    public const int MaxPreviewLineLength = 160;

    public const string InvalidCharactersError = "Names can only use kanji, kana, ー, 々, ・ and a space between the family and given name.";
    public const string InvalidLengthError = "Names must be 2 to 20 characters long, not counting spaces.";
    public const string NotInTextError = "This name doesn't appear anywhere in this deck's text. Check the spelling against the text.";
    public const string AlreadyListedError = "This name is already on the deck's list.";
    public const string HonorificError = "Leave out honorifics and titles like さん, くん, ちゃん or 先生. Add only the name itself.";

    /// <summary>Suffixes that make a name surface match only one form of address; the bare name already covers it.</summary>
    private static readonly string[] Honorifics =
        ["さん", "くん", "ちゃん", "さま", "様", "君", "殿", "先生", "先輩", "せんぱい", "センパイ", "氏", "たん", "っち", "ちん", "嬢"];

    public const string NothingToAddError = "Nothing in this name can be added. Each part is already listed, missing from the text, or too short for the parser to use.";

    private const string DuplicateDetail = "Already on this deck's list.";
    private const string RiskyDetail = "Too short to use: the parser ignores hiragana names of 1 or 2 characters.";
    private const string NotInTextDetail = "Doesn't appear in this deck's text.";

    private static readonly CharacterNamePreview EmptyPreview = new(0, 0, []);

    public async Task<List<CharacterNameCheckResult>> CheckManyAsync(int deckId, IReadOnlyCollection<string> existingSurfaces,
                                                                     IReadOnlyList<string> fullNames, CancellationToken ct = default)
    {
        var candidates = fullNames.Select(Derive).ToList();
        var surfaces = candidates.Where(c => c.Error == null).SelectMany(c => c.Surfaces).Distinct(StringComparer.Ordinal).ToList();

        var occurrences = (await ScanAsync(deckId, surfaces, ct)).ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.Ordinal);
        var commonWords = await FindCommonWordsAsync(surfaces, ct);

        var existing = new HashSet<string>(existingSurfaces.Select(s => s.Trim()), StringComparer.Ordinal);
        var results = new List<CharacterNameCheckResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.Error != null)
            {
                results.Add(new CharacterNameCheckResult(candidate.Input, candidate.Error, []));
                continue;
            }

            var checks = candidate.Surfaces
                                  .Select(s => Classify(s, occurrences.GetValueOrDefault(s), existing, commonWords))
                                  .ToList();
            var result = new CharacterNameCheckResult(candidate.Input, NameError(checks), checks);
            existing.UnionWith(result.Kept);
            results.Add(result);
        }

        return results;
    }

    /// <summary>Classifies already-derived surfaces, such as those stored on a pending edit, without deriving or validating a full name.</summary>
    public async Task<List<(CharacterNameSurfaceCheck Check, List<string> Examples)>> CheckSurfacesAsync(
        int deckId, IReadOnlyCollection<string> existingSurfaces, IReadOnlyCollection<string> surfaces, CancellationToken ct = default)
    {
        var distinct = surfaces.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var scans = await ScanAsync(deckId, distinct, ct);
        var commonWords = await FindCommonWordsAsync(distinct, ct);
        var existing = new HashSet<string>(existingSurfaces.Select(s => s.Trim()), StringComparer.Ordinal);

        return distinct.Select(s =>
        {
            var scan = scans.GetValueOrDefault(s) ?? SurfaceScan.Empty;
            return (Classify(s, scan.Count, existing, commonWords), scan.Examples);
        }).ToList();
    }

    /// <summary>Parses up to <see cref="MaxPreviewLines"/> lines containing the surfaces with and without them; the deck's existing entries are in both runs.</summary>
    public async Task<CharacterNamePreview> PreviewAsync(int deckId, IReadOnlyCollection<string> surfacesToAdd, CancellationToken ct = default)
    {
        var deck = await db.Decks.AsNoTracking()
                           .Where(d => d.DeckId == deckId)
                           .Select(d => new { d.MediaType, d.ParentDeckId })
                           .FirstOrDefaultAsync(ct);
        if (deck == null)
            return EmptyPreview;

        var existing = await LoadParserEntriesAsync(deckId, deck.ParentDeckId, ct);
        var existingSurfaces = existing.Select(e => e.Surface.Trim()).ToHashSet(StringComparer.Ordinal);
        var added = surfacesToAdd.Select(s => s.Trim())
                                 .Where(s => s.Length > 0 && !existingSurfaces.Contains(s))
                                 .Distinct(StringComparer.Ordinal)
                                 .ToList();
        if (added.Count == 0)
            return EmptyPreview;

        var lines = await CollectLinesAsync(deckId, added, ct);
        if (lines.Count == 0)
            return EmptyPreview;

        var withNames = existing.Concat(added.Select(s => new DeckDictionaryEntry
                                {
                                    DeckId = deckId, Surface = s, EntryType = DeckDictionaryEntryType.Name
                                }))
                                .ToList();

        var before = await SegmentAsync(lines, deck.MediaType, existing.Count > 0 ? existing : null);
        ct.ThrowIfCancellationRequested();
        var after = await SegmentAsync(lines, deck.MediaType, withNames);

        var changed = 0;
        var examples = new List<CharacterNamePreviewLine>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (before[i] == after[i])
                continue;

            changed++;
            if (examples.Count < MaxPreviewExamples)
                examples.Add(new CharacterNamePreviewLine(lines[i], before[i], after[i]));
        }

        return new CharacterNamePreview(lines.Count, changed, examples);
    }

    private sealed record Candidate(string Input, string? Error, List<string> Surfaces);

    /// <summary>Surfaces come from the same derivation the VNDB and AniList imports use, so a contributed name matches what an import would store.</summary>
    private static Candidate Derive(string input)
    {
        var name = (input ?? "").Trim();
        if (!HasOnlyNameCharacters(name))
            return new Candidate(input ?? "", InvalidCharactersError, []);

        var length = name.Replace(" ", "").Replace("　", "").EnumerateRunes().Count();
        if (length is < MinNameLength or > MaxNameLength)
            return new Candidate(input ?? "", InvalidLengthError, []);

        if (EndsWithHonorific(name))
            return new Candidate(input ?? "", HonorificError, []);

        var surfaces = MetadataProviderHelper.BuildDictionaryEntriesFromNames([(name, null, null)])
                                             .Select(e => e.Surface)
                                             .ToList();
        return new Candidate(input ?? "", null, surfaces);
    }

    /// <summary>A spaced name whose last part is an honorific, or an unspaced one ending in a two-plus character honorific (one-character ones like 君 also end real given names).</summary>
    public static bool EndsWithHonorific(string name)
    {
        var parts = name.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;
        if (parts.Length > 1)
            return Honorifics.Contains(parts[^1]);
        return Honorifics.Any(h => h.Length >= 2 && name.Length > h.Length && name.EndsWith(h, StringComparison.Ordinal));
    }

    public static bool HasOnlyNameCharacters(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            var allowed = rune.Value is ' ' or '　' or 'ー' or '々' or '・'
                          || (rune.IsBmp && (JapaneseTextHelper.IsHiragana((char)rune.Value) || JapaneseTextHelper.IsKatakana((char)rune.Value)))
                          || JapaneseTextHelper.IsKanji(rune);
            if (!allowed)
                return false;
        }

        return true;
    }

    private static CharacterNameSurfaceCheck Classify(string surface, int occurrences, HashSet<string> existing,
                                                      Dictionary<string, string?> commonWords)
    {
        if (existing.Contains(surface))
            return new CharacterNameSurfaceCheck(surface, occurrences, CharacterNameSurfaceStatus.Duplicate, DuplicateDetail);
        if (Jiten.Parser.Parser.IsRiskyNameEntry(surface))
            return new CharacterNameSurfaceCheck(surface, occurrences, CharacterNameSurfaceStatus.Risky, RiskyDetail);
        if (occurrences == 0)
            return new CharacterNameSurfaceCheck(surface, occurrences, CharacterNameSurfaceStatus.NotInText, NotInTextDetail);
        if (commonWords.TryGetValue(surface, out var gloss))
            return new CharacterNameSurfaceCheck(surface, occurrences, CharacterNameSurfaceStatus.Flagged,
                                                 gloss == null ? "Also a common word." : $"Also a common word ({gloss}).");

        return new CharacterNameSurfaceCheck(surface, occurrences, CharacterNameSurfaceStatus.Ok, null);
    }

    private static string? NameError(List<CharacterNameSurfaceCheck> checks)
    {
        if (checks.Count > 0 && checks.All(c => c.Occurrences == 0))
            return NotInTextError;
        if (checks.Any(c => c.Status is CharacterNameSurfaceStatus.Ok or CharacterNameSurfaceStatus.Flagged))
            return null;
        return checks.Count > 0 && checks.All(c => c.Status == CharacterNameSurfaceStatus.Duplicate) ? AlreadyListedError : NothingToAddError;
    }

    /// <summary>A parent deck's names apply to all of its subdecks, so its own raw text and every descendant's count.</summary>
    private async Task<List<int>> DeckAndDescendantIdsAsync(int deckId, CancellationToken ct)
    {
        var ids = new List<int> { deckId };
        var seen = new HashSet<int> { deckId };
        var frontier = new List<int> { deckId };
        while (frontier.Count > 0)
        {
            var parents = frontier;
            var children = await db.Decks.AsNoTracking()
                                   .Where(d => d.ParentDeckId != null && parents.Contains(d.ParentDeckId.Value))
                                   .OrderBy(d => d.DeckOrder).ThenBy(d => d.DeckId)
                                   .Select(d => d.DeckId)
                                   .ToListAsync(ct);
            frontier = children.Where(seen.Add).ToList();
            ids.AddRange(frontier);
        }

        return ids;
    }

    private sealed record SurfaceScan(int Count, List<string> Examples)
    {
        public static readonly SurfaceScan Empty = new(0, []);
    }

    /// <summary>Occurrences and the first excerpts of each surface across the deck and its subdecks, cached per surface.</summary>
    private async Task<Dictionary<string, SurfaceScan>> ScanAsync(int deckId, List<string> surfaces, CancellationToken ct)
    {
        var result = new Dictionary<string, SurfaceScan>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var surface in surfaces.Distinct(StringComparer.Ordinal))
        {
            if (cache.TryGetValue<SurfaceScan>(ScanKey(deckId, surface), out var cached) && cached != null)
                result[surface] = cached;
            else
                missing.Add(surface);
        }

        if (missing.Count == 0)
            return result;

        var counts = missing.ToDictionary(s => s, _ => 0, StringComparer.Ordinal);
        var examples = missing.ToDictionary(s => s, _ => new List<string>(), StringComparer.Ordinal);
        var deckIds = await DeckAndDescendantIdsAsync(deckId, ct);
        var texts = db.DeckRawTexts.AsNoTracking()
                      .Where(r => deckIds.Contains(r.DeckId))
                      .Select(r => r.RawText)
                      .AsAsyncEnumerable()
                      .WithCancellation(ct);

        await foreach (var text in texts)
        {
            foreach (var surface in missing)
            {
                counts[surface] += CountOrdinal(text, surface);
                var found = examples[surface];
                for (var at = 0; found.Count < MaxExamples && (at = text.IndexOf(surface, at, StringComparison.Ordinal)) >= 0; at += surface.Length)
                {
                    var excerpt = Excerpt(text, at, surface.Length);
                    if (!found.Contains(excerpt))
                        found.Add(excerpt);
                }
            }
        }

        foreach (var surface in missing)
        {
            var scan = new SurfaceScan(counts[surface], examples[surface]);
            cache.Set(ScanKey(deckId, surface), scan, ScanCacheLifetime);
            result[surface] = scan;
        }

        return result;
    }

    private static string ScanKey(int deckId, string surface) => $"metadata-edit:name-scan:{deckId}:{surface}";

    /// <summary>The surface with up to <see cref="ExampleContext"/> characters either side, cut at line breaks.</summary>
    private static string Excerpt(string text, int at, int length)
    {
        var start = Math.Max(0, at - ExampleContext);
        var end = Math.Min(text.Length, at + length + ExampleContext);
        var lineStart = text.LastIndexOfAny(['\n', '\r'], Math.Max(0, at - 1));
        if (lineStart >= start)
            start = lineStart + 1;
        var lineEnd = text.IndexOfAny(['\n', '\r'], at + length);
        if (lineEnd >= 0 && lineEnd < end)
            end = lineEnd;
        return text[start..end].Trim();
    }

    public static int CountOrdinal(ReadOnlySpan<char> text, ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return 0;

        var count = 0;
        while (true)
        {
            var index = text.IndexOf(value, StringComparison.Ordinal);
            if (index < 0)
                return count;
            count++;
            text = text[(index + value.Length)..];
        }
    }

    /// <summary>Common means a JMDict form with a priority tag on an entry without a name POS; corpus frequency is never used because misparses inflate it.</summary>
    private async Task<Dictionary<string, string?>> FindCommonWordsAsync(List<string> surfaces, CancellationToken ct)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (surfaces.Count == 0)
            return result;

        var forms = (await db.WordForms.AsNoTracking()
                             .Where(f => surfaces.Contains(f.Text) && f.IsActiveInLatestSource && f.Priorities != null)
                             .Select(f => new { f.WordId, f.Text, f.Priorities })
                             .ToListAsync(ct))
                    .Where(f => f.Priorities!.Count > 0)
                    .ToList();
        if (forms.Count == 0)
            return result;

        var wordIds = forms.Select(f => f.WordId).Distinct().ToList();
        var nameWordIds = (await db.JMDictWords.AsNoTracking()
                                   .Where(w => wordIds.Contains(w.WordId))
                                   .Select(w => new { w.WordId, w.PartsOfSpeech })
                                   .ToListAsync(ct))
                          .Where(w => w.PartsOfSpeech.Any(IsNamePos))
                          .Select(w => w.WordId)
                          .ToHashSet();

        var best = forms.Where(f => !nameWordIds.Contains(f.WordId))
                        .GroupBy(f => f.Text, StringComparer.Ordinal)
                        .ToDictionary(g => g.Key,
                                      g => g.OrderByDescending(f => f.Priorities!.Count).ThenBy(f => f.WordId).First().WordId,
                                      StringComparer.Ordinal);
        if (best.Count == 0)
            return result;

        var glossWordIds = best.Values.Distinct().ToList();
        var glosses = (await db.Definitions.AsNoTracking()
                               .Where(d => glossWordIds.Contains(d.WordId))
                               .Select(d => new { d.WordId, d.SenseIndex, d.DefinitionId, d.EnglishMeanings })
                               .ToListAsync(ct))
                      .GroupBy(d => d.WordId)
                      .ToDictionary(g => g.Key,
                                    g => g.OrderBy(d => d.SenseIndex).ThenBy(d => d.DefinitionId)
                                          .SelectMany(d => d.EnglishMeanings)
                                          .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)));

        foreach (var (surface, wordId) in best)
            result[surface] = glosses.GetValueOrDefault(wordId);

        return result;
    }

    /// <summary>Mirrors <c>JmDictHelper.LoadNameOnlyWordIds</c>: a name tag starts with "name-" or is one of the JMnedict type codes.</summary>
    public static bool IsNamePos(string pos) =>
        pos.StartsWith("name-", StringComparison.Ordinal) || NamePosCodes.Contains(pos);

    private static readonly HashSet<string> NamePosCodes = new(StringComparer.Ordinal)
    {
        "company", "given", "place", "person", "product", "ship", "surname", "unclass", "station", "group", "char", "creat", "dei",
        "doc", "ev", "fem", "fict", "leg", "masc", "myth", "obj", "organization", "oth", "relig", "serv", "work", "unc"
    };

    /// <summary>A subdeck without its own names parses with its parent's, the same fallback ReparseJob applies.</summary>
    private async Task<List<DeckDictionaryEntry>> LoadParserEntriesAsync(int deckId, int? parentDeckId, CancellationToken ct)
    {
        var entries = await db.DeckDictionaryEntries.AsNoTracking().Where(e => e.DeckId == deckId).ToListAsync(ct);
        if (entries.Count == 0 && parentDeckId != null)
            entries = await db.DeckDictionaryEntries.AsNoTracking().Where(e => e.DeckId == parentDeckId).ToListAsync(ct);

        return entries.Where(e => e.Surface.Trim().Length > 0)
                      .DistinctBy(e => e.Surface.Trim(), StringComparer.Ordinal)
                      .ToList();
    }

    private async Task<List<string>> CollectLinesAsync(int deckId, List<string> surfaces, CancellationToken ct)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var deckIds = await DeckAndDescendantIdsAsync(deckId, ct);
        var texts = db.DeckRawTexts.AsNoTracking()
                      .Where(r => deckIds.Contains(r.DeckId))
                      .OrderBy(r => r.DeckId)
                      .Select(r => r.RawText)
                      .AsAsyncEnumerable()
                      .WithCancellation(ct);

        await foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text))
                continue;

            foreach (var rawLine in text.AsSpan().EnumerateLines())
            {
                var line = rawLine.Trim();
                var hit = FirstHit(line, surfaces);
                if (hit < 0)
                    continue;

                var clipped = Clip(line, hit).ToString();
                if (seen.Add(clipped))
                    lines.Add(clipped);
                if (lines.Count >= MaxPreviewLines)
                    return lines;
            }
        }

        return lines;
    }

    private static int FirstHit(ReadOnlySpan<char> line, List<string> surfaces)
    {
        var first = -1;
        foreach (var surface in surfaces)
        {
            var index = line.IndexOf(surface, StringComparison.Ordinal);
            if (index >= 0 && (first < 0 || index < first))
                first = index;
        }

        return first;
    }

    private static ReadOnlySpan<char> Clip(ReadOnlySpan<char> line, int hit)
    {
        if (line.Length <= MaxPreviewLineLength)
            return line;

        var start = Math.Clamp(hit - MaxPreviewLineLength / 2, 0, line.Length - MaxPreviewLineLength);
        return line.Slice(start, MaxPreviewLineLength);
    }

    private async Task<List<string>> SegmentAsync(List<string> lines, MediaType mediaType, List<DeckDictionaryEntry>? entries)
    {
        var sink = new List<List<ParsedOccurrence>>();
        await Jiten.Parser.Parser.ParseTextsToDeck(contextFactory, lines, storeRawText: false, predictDifficulty: false, mediatype: mediaType,
                                                   dictionaryEntries: entries, occurrenceSink: sink);

        return lines.Select((_, i) => i < sink.Count ? string.Join(" / ", sink[i].Select(o => o.Surface)) : "").ToList();
    }
}

public sealed record CharacterNameCheckResult(string Input, string? Error, List<CharacterNameSurfaceCheck> Surfaces)
{
    public IEnumerable<string> Kept => Surfaces.Where(s => s.Status is CharacterNameSurfaceStatus.Ok or CharacterNameSurfaceStatus.Flagged)
                                               .Select(s => s.Surface);
}

public sealed record CharacterNameSurfaceCheck(string Surface, int Occurrences, CharacterNameSurfaceStatus Status, string? Detail);

public enum CharacterNameSurfaceStatus
{
    Ok,
    Flagged,
    NotInText,
    Risky,
    Duplicate
}

public sealed record CharacterNamePreview(int LinesChecked, int LinesChanged, List<CharacterNamePreviewLine> Examples);

public sealed record CharacterNamePreviewLine(string Line, string Before, string After);
