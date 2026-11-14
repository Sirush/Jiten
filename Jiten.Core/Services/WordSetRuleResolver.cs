using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Services;

public sealed record WordSetResolution(List<(int WordId, short ReadingIndex)> Forms, List<string> UnknownPos);

public sealed record WordSetDiff(
    List<(int WordId, short ReadingIndex)> ToAdd,
    List<(int WordId, short ReadingIndex)> Unmatched,
    int Unchanged,
    int Manual,
    int Excluded,
    List<string> UnknownPos);

public static class WordSetRuleResolver
{
    private const int NameRangeStart = 5000000;
    private const int NameRangeEnd = 8000000;
    private const int FormLookupThreshold = 20000;

    private readonly record struct FormInfo(short ReadingIndex, JmDictFormType FormType, bool IsKatakana);

    public static async Task<WordSetDiff> DiffAsync(JitenDbContext context, int setId, WordSetRule rule, CancellationToken ct = default)
    {
        var resolution = await ResolveAsync(context, rule, ct);

        var exclusions = new HashSet<(int, short)>();
        await foreach (var e in context.WordSetExclusions.AsNoTracking()
                                       .Where(e => e.SetId == setId)
                                       .Select(e => new { e.WordId, e.ReadingIndex })
                                       .AsAsyncEnumerable().WithCancellation(ct))
            exclusions.Add((e.WordId, e.ReadingIndex));

        var target = new HashSet<(int, short)>(resolution.Forms.Count);
        var excluded = 0;
        foreach (var form in resolution.Forms)
        {
            if (exclusions.Contains(form))
                excluded++;
            else
                target.Add(form);
        }

        var current = new HashSet<(int, short)>();
        var unmatched = new List<(int, short)>();
        int unchanged = 0, manual = 0;
        await foreach (var m in context.WordSetMembers.AsNoTracking()
                                       .Where(m => m.SetId == setId)
                                       .OrderBy(m => m.Position)
                                       .Select(m => new { m.WordId, m.ReadingIndex, m.IsManual })
                                       .AsAsyncEnumerable().WithCancellation(ct))
        {
            var key = (m.WordId, m.ReadingIndex);
            current.Add(key);
            if (m.IsManual)
                manual++;
            else if (target.Contains(key))
                unchanged++;
            else
                unmatched.Add(key);
        }

        var toAdd = resolution.Forms.Where(f => target.Contains(f) && !current.Contains(f)).ToList();
        return new WordSetDiff(toAdd, unmatched, unchanged, manual, excluded, resolution.UnknownPos);
    }

    public static async Task<WordSetResolution> ResolveAsync(JitenDbContext context, WordSetRule rule, CancellationToken ct = default)
    {
        var include = rule.IncludePos.ToHashSet();
        var exclude = rule.ExcludePos.ToHashSet();
        var origins = rule.Origins.ToHashSet();
        var languages = rule.SourceLanguages.ToHashSet();

        var seenPos = new HashSet<string>();
        var wordIds = new HashSet<int>();

        await foreach (var w in context.JMDictWords.AsNoTracking()
                                       .Select(w => new { w.WordId, w.PartsOfSpeech, w.Origin, w.LanguageSourcesJson })
                                       .AsAsyncEnumerable().WithCancellation(ct))
        {
            foreach (var pos in w.PartsOfSpeech)
                seenPos.Add(pos);

            if (!MatchesDictionary(rule.Dictionary, w.WordId)) continue;
            if (include.Count > 0 && !w.PartsOfSpeech.Exists(include.Contains)) continue;
            if (exclude.Count > 0 && w.PartsOfSpeech.Exists(exclude.Contains)) continue;
            if (origins.Count > 0 && !origins.Contains(w.Origin)) continue;
            if (languages.Count > 0 && !HasSourceLanguage(w.LanguageSourcesJson, languages)) continue;

            wordIds.Add(w.WordId);
        }

        var unknownPos = include.Concat(exclude).Where(p => !seenPos.Contains(p)).Distinct().Order().ToList();

        var formsByWord = await LoadForms(context, wordIds, ct);
        var rankedForms = rule.MaxFrequencyRank is { } maxRank ? await LoadRankedForms(context, maxRank, ct) : null;

        var selected = new List<(int, short)>();
        foreach (var wordId in wordIds.Order())
        {
            if (!formsByWord.TryGetValue(wordId, out var forms)) continue;

            foreach (var readingIndex in SelectForms(rule, forms).Order())
            {
                if (readingIndex is < 0 or > byte.MaxValue) continue;
                if (rankedForms != null && !rankedForms.Contains((wordId, readingIndex))) continue;
                selected.Add((wordId, readingIndex));
            }
        }

        return new WordSetResolution(selected, unknownPos);
    }

    private static IEnumerable<short> SelectForms(WordSetRule rule, List<FormInfo> forms)
    {
        if (rule.Script == WordSetScript.KatakanaForm)
            return forms.Where(f => f.IsKatakana).Select(f => f.ReadingIndex);

        if (rule.Script == WordSetScript.KatakanaWord && !forms.TrueForAll(f => f.IsKatakana))
            return [];

        var kanjiForms = forms.Where(f => f.FormType == JmDictFormType.KanjiForm).Select(f => f.ReadingIndex).ToList();
        var kanaForms = forms.Where(f => f.FormType == JmDictFormType.KanaForm).Select(f => f.ReadingIndex).Order().ToList();
        if (kanjiForms.Count == 0)
            return kanaForms;

        if (rule.AddKanaForm && kanaForms.Count > 0)
            kanjiForms.Add(kanaForms[0]);

        return kanjiForms;
    }

    private static async Task<Dictionary<int, List<FormInfo>>> LoadForms(JitenDbContext context, HashSet<int> wordIds, CancellationToken ct)
    {
        var formsByWord = new Dictionary<int, List<FormInfo>>(wordIds.Count);
        if (wordIds.Count == 0)
            return formsByWord;

        var query = context.WordForms.AsNoTracking();
        if (wordIds.Count <= FormLookupThreshold)
        {
            var ids = wordIds.ToList();
            query = query.Where(f => ids.Contains(f.WordId));
        }

        await foreach (var f in query.Select(f => new { f.WordId, f.ReadingIndex, f.FormType, f.Text })
                                     .AsAsyncEnumerable().WithCancellation(ct))
        {
            if (!wordIds.Contains(f.WordId)) continue;

            if (!formsByWord.TryGetValue(f.WordId, out var forms))
                formsByWord[f.WordId] = forms = [];
            forms.Add(new FormInfo(f.ReadingIndex, f.FormType, IsKatakana(f.Text)));
        }

        return formsByWord;
    }

    private static async Task<HashSet<(int, short)>> LoadRankedForms(JitenDbContext context, int maxRank, CancellationToken ct)
    {
        var ranked = new HashSet<(int, short)>();
        await foreach (var f in context.WordFormFrequencies.AsNoTracking()
                                       .Where(f => f.FrequencyRank > 0 && f.FrequencyRank <= maxRank)
                                       .Select(f => new { f.WordId, f.ReadingIndex })
                                       .AsAsyncEnumerable().WithCancellation(ct))
            ranked.Add((f.WordId, f.ReadingIndex));

        return ranked;
    }

    private static bool MatchesDictionary(WordSetDictionary dictionary, int wordId)
    {
        var isName = wordId is >= NameRangeStart and < NameRangeEnd;
        return dictionary switch
        {
            WordSetDictionary.Vocabulary => !isName,
            WordSetDictionary.Names => isName,
            _ => true
        };
    }

    private static bool HasSourceLanguage(string? languageSourcesJson, HashSet<string> languages)
    {
        if (string.IsNullOrEmpty(languageSourcesJson)) return false;

        var sources = System.Text.Json.JsonSerializer.Deserialize<List<JmDictLanguageSource>>(languageSourcesJson);
        return sources != null && sources.Exists(s => languages.Contains(s.Lang));
    }

    private static bool IsKatakana(string text)
    {
        if (text.Length == 0) return false;

        foreach (var c in text)
        {
            if (c is < 'ァ' or > 'ヿ')
                return false;
        }

        return true;
    }
}
