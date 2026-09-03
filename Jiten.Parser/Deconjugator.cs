using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Jiten.Parser;

public class Deconjugator
{
    private static readonly Lazy<Deconjugator> _instance =
        new Lazy<Deconjugator>(() => new Deconjugator(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static Deconjugator Instance => _instance.Value;

    private const int DefaultMaxCacheEntries = 50_000;
    private const int MaxCacheableInputLength = 40;
    private const int MaxCachedFormsBase = 64;
    private const int MaxCachedFormsPerChar = 12;
    // Deconjugation shortens text; growth beyond this is spurious rule chaining
    private const int MaxTextGrowthFromOriginal = 10;
    // Deepest real conjugation chain is ~6 steps (e.g. 食べさせられたくなかったらしい)
    private const int MaxChainDepthAboveInputLength = 6;
    private readonly int _maxCacheEntries;
    private volatile ConcurrentDictionary<string, DeconjugationForm[]> _gen0 =
        new(Environment.ProcessorCount, 4096, StringComparer.Ordinal);
    private volatile ConcurrentDictionary<string, DeconjugationForm[]>? _gen1;
    private int _gen0Count;
    private int _rotating;
    private long _cacheHitCount;
    private long _cacheMissCount;
    private long _cacheStoreCount;
    private long _cacheEvictionCount;
    private long _bfsTotalTicks;
    private long _bfsCallCount;

    private readonly DeconjugationRule[] _rules;

    public sealed record DeconjugationCacheStats(
        long Hits,
        long Misses,
        long Stores,
        long Evictions,
        int Count,
        int MaxEntries,
        double BfsTimeMs,
        long BfsCalls);

    private enum RuleMode : byte { Standard, OnlyFinal, NeverFinal, Context, Rewrite }

    private readonly struct RuleIndexEntry(DeconjugationRule rule, DeconjugationVirtualRule virtualRule, RuleMode mode)
    {
        public readonly DeconjugationRule Rule = rule;
        public readonly DeconjugationVirtualRule VirtualRule = virtualRule;
        public readonly RuleMode Mode = mode;
    }

    private readonly Dictionary<string, RuleIndexEntry[]> _conEndIndex;
    private readonly Dictionary<string, RuleIndexEntry[]>.AlternateLookup<ReadOnlySpan<char>> _conEndIndexAlt;
    private readonly int _maxConEndLength;
    private readonly int _maxDecEndLength;

    public Deconjugator(int maxCacheEntries = DefaultMaxCacheEntries)
    {
        _maxCacheEntries = Math.Max(1, maxCacheEntries);

        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new StringArrayConverter() }
        };

        var rules = JsonSerializer
            .Deserialize<List<DeconjugationRule>>(
                File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "deconjugator.json")),
                options) ?? [];

        _rules = rules.ToArray();

        (_conEndIndex, _maxConEndLength, _maxDecEndLength) = BuildRuleIndex(_rules);
        _conEndIndexAlt = _conEndIndex.GetAlternateLookup<ReadOnlySpan<char>>();

    }

    private static (Dictionary<string, RuleIndexEntry[]> index, int maxConEndLen, int maxDecEndLen)
        BuildRuleIndex(DeconjugationRule[] rules)
    {
        var tempIndex = new Dictionary<string, List<RuleIndexEntry>>(StringComparer.Ordinal);
        int maxConEndLen = 0;
        int maxDecEndLen = 0;

        foreach (var rule in rules)
        {
            var mode = rule.Type switch
            {
                "onlyfinalrule" => RuleMode.OnlyFinal,
                "neverfinalrule" => RuleMode.NeverFinal,
                "contextrule" => RuleMode.Context,
                "rewriterule" => RuleMode.Rewrite,
                _ => RuleMode.Standard
            };

            for (int i = 0; i < rule.DecEnd.Length; i++)
            {
                var vr = new DeconjugationVirtualRule(
                    rule.DecEnd[i],
                    rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0],
                    rule.DecTag?.ElementAtOrDefault(i) ?? rule.DecTag?[0],
                    rule.ConTag?.ElementAtOrDefault(i) ?? rule.ConTag?[0],
                    rule.Detail);

                var conEnd = vr.ConEnd;
                if (conEnd.Length > maxConEndLen) maxConEndLen = conEnd.Length;
                if (vr.DecEnd.Length > maxDecEndLen) maxDecEndLen = vr.DecEnd.Length;

                if (!tempIndex.TryGetValue(conEnd, out var list))
                    tempIndex[conEnd] = list = new List<RuleIndexEntry>();
                list.Add(new RuleIndexEntry(rule, vr, mode));
            }
        }

        var index = new Dictionary<string, RuleIndexEntry[]>(tempIndex.Count, StringComparer.Ordinal);
        foreach (var (key, list) in tempIndex)
            index[key] = list.ToArray();

        return (index, maxConEndLen, maxDecEndLen);
    }

    private bool TryGetCached(string text, out IReadOnlyList<DeconjugationForm> forms)
    {
        if (_gen0.TryGetValue(text, out var result))
        {
            Interlocked.Increment(ref _cacheHitCount);
            forms = result;
            return true;
        }

        var gen1 = _gen1;
        if (gen1 != null && gen1.TryGetValue(text, out result))
        {
            _gen0.TryAdd(text, result);
            Interlocked.Increment(ref _cacheHitCount);
            forms = result;
            return true;
        }

        Interlocked.Increment(ref _cacheMissCount);
        forms = [];
        return false;
    }

    private void StoreCached(string text, DeconjugationForm[] forms)
    {
        if (text.Length > MaxCacheableInputLength || forms.Length >= MaxCachedFormsBase + MaxCachedFormsPerChar * text.Length)
            return;

        Interlocked.Increment(ref _cacheStoreCount);

        if (_gen0.TryAdd(text, forms))
        {
            var count = Interlocked.Increment(ref _gen0Count);
            if (count > _maxCacheEntries)
                RotateGenerations();
        }
    }

    private void RotateGenerations()
    {
        if (Interlocked.CompareExchange(ref _rotating, 1, 0) != 0)
            return;

        try
        {
            var evicted = _gen1?.Count ?? 0;
            _gen1 = _gen0;
            _gen0 = new ConcurrentDictionary<string, DeconjugationForm[]>(
                Environment.ProcessorCount, 4096, StringComparer.Ordinal);
            Interlocked.Exchange(ref _gen0Count, 0);

            if (evicted > 0)
                Interlocked.Add(ref _cacheEvictionCount, evicted);
        }
        finally
        {
            Interlocked.Exchange(ref _rotating, 0);
        }
    }

    public DeconjugationCacheStats GetCacheStats()
    {
        var bfsTicks = Interlocked.Read(ref _bfsTotalTicks);
        return new DeconjugationCacheStats(
            Interlocked.Read(ref _cacheHitCount),
            Interlocked.Read(ref _cacheMissCount),
            Interlocked.Read(ref _cacheStoreCount),
            Interlocked.Read(ref _cacheEvictionCount),
            _gen0.Count + (_gen1?.Count ?? 0),
            _maxCacheEntries,
            (double)bfsTicks / Stopwatch.Frequency * 1000,
            Interlocked.Read(ref _bfsCallCount));
    }

    internal void ClearCacheForTesting()
    {
        _gen0 = new ConcurrentDictionary<string, DeconjugationForm[]>(
            Environment.ProcessorCount, 4096, StringComparer.Ordinal);
        _gen1 = null;
        Interlocked.Exchange(ref _gen0Count, 0);
        Interlocked.Exchange(ref _cacheHitCount, 0);
        Interlocked.Exchange(ref _cacheMissCount, 0);
        Interlocked.Exchange(ref _cacheStoreCount, 0);
        Interlocked.Exchange(ref _cacheEvictionCount, 0);
    }

    /// <summary>Results are ordered by Text.Length descending, then ordinal — call sites must not re-sort.</summary>
    public IReadOnlyList<DeconjugationForm> Deconjugate(string text)
    {
        if (TryGetCached(text, out var cached))
            return cached;

        if (string.IsNullOrEmpty(text))
            return [];

        bool timed = Diagnostics.ParserCounters.SectionTiming;
        long start = timed ? Stopwatch.GetTimestamp() : 0;
        var result = RunBfs(text);
        if (timed) Interlocked.Add(ref _bfsTotalTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _bfsCallCount);

        StoreCached(text, result);

        return result;
    }

    private DeconjugationForm[] RunBfs(string text)
    {
        // `seen` doubles as the result set: every form reached is emitted, in discovery order.
        var seen = new HashSet<DeconjugationForm>(Math.Max(text.Length * 4, 32));
        var ordered = new List<DeconjugationForm>(Math.Max(text.Length * 4, 32));
        var novel = new List<DeconjugationForm>(20);
        var initial = CreateInitialForm(text);
        seen.Add(initial);
        ordered.Add(initial);
        novel.Add(initial);

        var newNovel = new List<DeconjugationForm>(32);
        while (novel.Count > 0)
        {
            newNovel.Clear();

            foreach (var form in novel)
            {
                if (ShouldSkipForm(form))
                    continue;

                ApplyIndexedRules(form, newNovel, seen, ordered);
            }

            (novel, newNovel) = (newNovel, novel);
        }

        return SortForms(ordered);
    }

    // Length descending, then ordinal text, then discovery order: a stable sort, so equal-key
    // forms (same text, different chains) keep the order the BFS found them in.
    private static DeconjugationForm[] SortForms(List<DeconjugationForm> ordered)
    {
        int n = ordered.Count;
        var index = new int[n];
        for (int i = 0; i < n; i++) index[i] = i;

        Array.Sort(index, (a, b) =>
        {
            var fa = ordered[a];
            var fb = ordered[b];
            int cmp = fb.Text.Length.CompareTo(fa.Text.Length);
            if (cmp != 0) return cmp;
            cmp = string.CompareOrdinal(fa.Text, fb.Text);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        var result = new DeconjugationForm[n];
        for (int i = 0; i < n; i++) result[i] = ordered[index[i]];
        return result;
    }

    private void ApplyIndexedRules(DeconjugationForm form, List<DeconjugationForm> newNovel,
                                    HashSet<DeconjugationForm> seen, List<DeconjugationForm> ordered)
    {
        var text = form.Text;
        int maxSuffix = Math.Min(_maxConEndLength, text.Length);

        // One buffer for every candidate: a stackalloc inside the loops would accumulate
        // stack space until the method returns.
        int maxNewTextLength = text.Length + _maxDecEndLength;
        Span<char> buffer = maxNewTextLength <= 256 ? stackalloc char[256] : new char[maxNewTextLength];

        for (int suffixLen = 0; suffixLen <= maxSuffix; suffixLen++)
        {
            var suffix = text.AsSpan(text.Length - suffixLen);
            if (!_conEndIndexAlt.TryGetValue(suffix, out var entries))
                continue;

            foreach (var entry in entries)
            {
                switch (entry.Mode)
                {
                    case RuleMode.OnlyFinal when form.Tags.Length > 0:
                    case RuleMode.NeverFinal when form.Tags.Length == 0:
                        continue;
                    case RuleMode.Rewrite when !text.Equals(entry.VirtualRule.ConEnd, StringComparison.Ordinal):
                        continue;
                    case RuleMode.Context:
                        if (entry.Rule.ContextRule == "v1inftrap" && !V1InfTrapCheck(form)) continue;
                        if (entry.Rule.ContextRule == "saspecial" && !SaSpecialCheck(form, entry.Rule)) continue;
                        if (entry.Rule.ContextRule == "temirurule" && !TemiruCheck(form, entry.Rule)) continue;
                        break;
                }

                if (string.IsNullOrEmpty(entry.Rule.Detail) && form.Tags.Length == 0)
                    continue;

                if (form.Tags.Length > 0 && form.Tags[^1] != entry.VirtualRule.ConTag)
                    continue;

                var prefixLength = text.Length - entry.VirtualRule.ConEnd.Length;
                int newTextLength = prefixLength + entry.VirtualRule.DecEnd.Length;
                text.AsSpan(0, prefixLength).CopyTo(buffer);
                entry.VirtualRule.DecEnd.AsSpan().CopyTo(buffer[prefixLength..]);
                var newText = new string(buffer[..newTextLength]);

                if (newText.Equals(form.OriginalText, StringComparison.Ordinal))
                    continue;

                var newForm = CreateNewForm(form, newText, entry.VirtualRule.ConTag,
                    entry.VirtualRule.DecTag, entry.VirtualRule.Detail);

                if (seen.Add(newForm))
                {
                    ordered.Add(newForm);
                    newNovel.Add(newForm);
                }
            }
        }
    }

    private DeconjugationForm CreateInitialForm(string text)
    {
        return new DeconjugationForm(text, text, [], null, []);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldSkipForm(DeconjugationForm form)
    {
        return string.IsNullOrEmpty(form.Text) ||
               form.Text.Length > form.OriginalText.Length + MaxTextGrowthFromOriginal ||
               form.Tags.Length > form.OriginalText.Length + MaxChainDepthAboveInputLength;
    }

    private DeconjugationForm CreateNewForm(DeconjugationForm form, string newText, string? conTag, string? decTag, string detail)
    {
        int existingTagCount = form.Tags.Length;
        bool addConTag = existingTagCount == 0 && conTag != null;
        bool addDecTag = decTag != null;
        int newTagCount = existingTagCount + (addConTag ? 1 : 0) + (addDecTag ? 1 : 0);

        var tags = new string[newTagCount];
        for (int i = 0; i < existingTagCount; i++)
            tags[i] = form.Tags[i];
        int idx = existingTagCount;
        if (addConTag) tags[idx++] = conTag!;
        if (addDecTag) tags[idx++] = decTag!;

        var seenText = BuildSeenText(form, newText);

        int procCount = form.Process.Length;
        var process = new string[procCount + 1];
        for (int i = 0; i < procCount; i++)
            process[i] = form.Process[i];
        process[procCount] = detail;

        return new DeconjugationForm(newText, form.OriginalText, tags, seenText, process);
    }

    // Parent chain, then the parent text when the chain is empty, then the new text; a text
    // already on the chain keeps its first position.
    private static SeenTextNode BuildSeenText(DeconjugationForm form, string newText)
    {
        var chain = form.SeenChain ?? SeenTextNode.Append(null, form.Text);
        return SeenTextNode.Append(chain, newText);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TemiruCheck(DeconjugationForm form, DeconjugationRule rule)
    {
        var conEnd = rule.ConEnd[0];
        if (!form.Text.EndsWith(conEnd, StringComparison.Ordinal)) return false;
        var prefix = form.Text.AsSpan(0, form.Text.Length - conEnd.Length);
        return prefix.EndsWith('て') || prefix.EndsWith('で');
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool V1InfTrapCheck(DeconjugationForm form)
    {
        return !(form.Tags is ["stem-ren"]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool SaSpecialCheck(DeconjugationForm form, DeconjugationRule rule)
    {
        if (form.Text.Length == 0) return false;

        var conEnd = rule.ConEnd[0];
        if (!form.Text.EndsWith(conEnd, StringComparison.Ordinal)) return false;

        var prefixLength = form.Text.Length - conEnd.Length;
        return prefixLength <= 0 || !form.Text.AsSpan(prefixLength - 1, 1).SequenceEqual("さ".AsSpan());
    }
}
