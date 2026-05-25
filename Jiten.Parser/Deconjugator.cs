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

    // Cache virtual rules to avoid recreating them
    private readonly Dictionary<DeconjugationRule, DeconjugationVirtualRule[]> _virtualRulesCache = new();

    private static readonly bool UseCache = true;

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
    private readonly DeconjugationRule[] _substitutionRules;

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

        foreach (var rule in _rules)
            CacheVirtualRules(rule);

        (_conEndIndex, _maxConEndLength, _substitutionRules) = BuildRuleIndex(_rules);
        _conEndIndexAlt = _conEndIndex.GetAlternateLookup<ReadOnlySpan<char>>();

    }

    private void CacheVirtualRules(DeconjugationRule rule)
    {
        if (rule.DecEnd.Length <= 1) return;

        var virtualRules = new DeconjugationVirtualRule[rule.DecEnd.Length];
        for (int i = 0; i < rule.DecEnd.Length; i++)
        {
            virtualRules[i] = new DeconjugationVirtualRule(
                rule.DecEnd.ElementAtOrDefault(i) ?? rule.DecEnd[0],
                rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0],
                rule.DecTag?.ElementAtOrDefault(i) ?? rule.DecTag?[0],
                rule.ConTag?.ElementAtOrDefault(i) ?? rule.ConTag?[0],
                rule.Detail
            );
        }
        _virtualRulesCache[rule] = virtualRules;
    }

    private static (Dictionary<string, RuleIndexEntry[]> index, int maxLen, DeconjugationRule[] substitutions)
        BuildRuleIndex(DeconjugationRule[] rules)
    {
        var tempIndex = new Dictionary<string, List<RuleIndexEntry>>(StringComparer.Ordinal);
        var substitutions = new List<DeconjugationRule>();
        int maxConEndLen = 0;

        foreach (var rule in rules)
        {
            if (rule.Type == "substitution")
            {
                substitutions.Add(rule);
                continue;
            }

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
                    rule.DecEnd.ElementAtOrDefault(i) ?? rule.DecEnd[0],
                    rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0],
                    rule.DecTag?.ElementAtOrDefault(i) ?? rule.DecTag?[0],
                    rule.ConTag?.ElementAtOrDefault(i) ?? rule.ConTag?[0],
                    rule.Detail);

                var conEnd = vr.ConEnd;
                if (conEnd.Length > maxConEndLen) maxConEndLen = conEnd.Length;

                if (!tempIndex.TryGetValue(conEnd, out var list))
                    tempIndex[conEnd] = list = new List<RuleIndexEntry>();
                list.Add(new RuleIndexEntry(rule, vr, mode));
            }
        }

        var index = new Dictionary<string, RuleIndexEntry[]>(tempIndex.Count, StringComparer.Ordinal);
        foreach (var (key, list) in tempIndex)
            index[key] = list.ToArray();

        return (index, maxConEndLen, substitutions.ToArray());
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

    private void StoreCached(string text, List<DeconjugationForm> forms)
    {
        if (text.Length > MaxCacheableInputLength || forms.Count >= MaxCachedFormsBase + MaxCachedFormsPerChar * text.Length)
            return;

        var snapshot = forms.ToArray();
        Interlocked.Increment(ref _cacheStoreCount);

        if (_gen0.TryAdd(text, snapshot))
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

    public IReadOnlyList<DeconjugationForm> Deconjugate(string text)
    {
        if (UseCache && TryGetCached(text, out var cached))
            return cached;

        if (string.IsNullOrEmpty(text))
            return [];

        long start = Stopwatch.GetTimestamp();
        var result = RunBfs(text);
        Interlocked.Add(ref _bfsTotalTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Increment(ref _bfsCallCount);

        if (UseCache)
            StoreCached(text, result);

        return result;
    }

    public bool CanDeconjugateTo(string text, string target)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(target))
            return false;
        if (text == target) return true;

        if (UseCache && TryGetCached(text, out var cached))
        {
            for (int i = 0; i < cached.Count; i++)
                if (cached[i].Text == target) return true;
            return false;
        }

        var processed = new HashSet<DeconjugationForm>(Math.Min(text.Length * 2, 100));
        var novel = new HashSet<DeconjugationForm>(20);
        novel.Add(CreateInitialForm(text));
        bool firstLevel = true;

        while (novel.Count > 0)
        {
            var newNovel = new HashSet<DeconjugationForm>(novel.Count * 2);

            foreach (var form in novel)
            {
                if (form.Text == target) return true;
                if (ShouldSkipForm(form)) continue;

                if (firstLevel)
                {
                    foreach (var rule in _substitutionRules)
                    {
                        var newForms = SubstitutionDeconjugate(form, rule);
                        if (newForms == null) continue;
                        foreach (var f in newForms)
                        {
                            if (f.Text == target) return true;
                            if (!processed.Contains(f) && !novel.Contains(f) && !newNovel.Contains(f))
                                newNovel.Add(f);
                        }
                    }
                }

                var formText = form.Text;
                int maxSuffix = Math.Min(_maxConEndLength, formText.Length);
                for (int suffixLen = 0; suffixLen <= maxSuffix; suffixLen++)
                {
                    var suffix = formText.AsSpan(formText.Length - suffixLen);
                    if (!_conEndIndexAlt.TryGetValue(suffix, out var entries))
                        continue;

                    foreach (var entry in entries)
                    {
                        switch (entry.Mode)
                        {
                            case RuleMode.OnlyFinal when form.Tags.Count > 0:
                            case RuleMode.NeverFinal when form.Tags.Count == 0:
                                continue;
                            case RuleMode.Rewrite when !formText.Equals(entry.VirtualRule.ConEnd, StringComparison.Ordinal):
                                continue;
                            case RuleMode.Context:
                                if (entry.Rule.ContextRule == "v1inftrap" && !V1InfTrapCheck(form)) continue;
                                if (entry.Rule.ContextRule == "saspecial" && !SaSpecialCheck(form, entry.Rule)) continue;
                                if (entry.Rule.ContextRule == "temirurule" && !TemiruCheck(form, entry.Rule)) continue;
                                break;
                        }

                        if (string.IsNullOrEmpty(entry.Rule.Detail) && form.Tags.Count == 0)
                            continue;

                        if (form.Tags.Count > 0 && form.Tags[^1] != entry.VirtualRule.ConTag)
                            continue;

                        var prefixLength = formText.Length - entry.VirtualRule.ConEnd.Length;
                        Span<char> buffer = stackalloc char[prefixLength + entry.VirtualRule.DecEnd.Length];
                        formText.AsSpan(0, prefixLength).CopyTo(buffer);
                        entry.VirtualRule.DecEnd.AsSpan().CopyTo(buffer[prefixLength..]);
                        var newText = new string(buffer);

                        if (newText.Equals(form.OriginalText, StringComparison.Ordinal))
                            continue;

                        if (newText == target) return true;

                        var newForm = CreateNewForm(form, newText, entry.VirtualRule.ConTag,
                            entry.VirtualRule.DecTag, entry.VirtualRule.Detail);

                        if (!processed.Contains(newForm) && !novel.Contains(newForm) && !newNovel.Contains(newForm))
                            newNovel.Add(newForm);
                    }
                }
            }

            processed.UnionWith(novel);
            novel = newNovel;
        }

        return false;
    }

    private List<DeconjugationForm> RunBfs(string text)
    {
        var processed = new HashSet<DeconjugationForm>(Math.Min(text.Length * 2, 100));
        var novel = new HashSet<DeconjugationForm>(20);
        novel.Add(CreateInitialForm(text));
        bool firstLevel = true;

        while (novel.Count > 0)
        {
            var newNovel = new HashSet<DeconjugationForm>(novel.Count * 2);

            foreach (var form in novel)
            {
                if (ShouldSkipForm(form))
                    continue;

                if (firstLevel)
                {
                    foreach (var rule in _substitutionRules)
                    {
                        var newForms = SubstitutionDeconjugate(form, rule);
                        if (newForms == null) continue;
                        foreach (var f in newForms)
                            if (!processed.Contains(f) && !novel.Contains(f) && !newNovel.Contains(f))
                                newNovel.Add(f);
                    }
                }

                ApplyIndexedRules(form, newNovel, processed, novel);
            }

            processed.UnionWith(novel);
            novel = newNovel;
            firstLevel = false;
        }

        return processed
            .OrderByDescending(f => f.Text.Length)
            .ThenBy(f => f.Text, StringComparer.Ordinal)
            .ToList();
    }

    private void ApplyIndexedRules(DeconjugationForm form, HashSet<DeconjugationForm> newNovel,
                                    HashSet<DeconjugationForm> processed, HashSet<DeconjugationForm> novel)
    {
        var text = form.Text;
        int maxSuffix = Math.Min(_maxConEndLength, text.Length);

        for (int suffixLen = 0; suffixLen <= maxSuffix; suffixLen++)
        {
            var suffix = text.AsSpan(text.Length - suffixLen);
            if (!_conEndIndexAlt.TryGetValue(suffix, out var entries))
                continue;

            foreach (var entry in entries)
            {
                switch (entry.Mode)
                {
                    case RuleMode.OnlyFinal when form.Tags.Count > 0:
                    case RuleMode.NeverFinal when form.Tags.Count == 0:
                        continue;
                    case RuleMode.Rewrite when !text.Equals(entry.VirtualRule.ConEnd, StringComparison.Ordinal):
                        continue;
                    case RuleMode.Context:
                        if (entry.Rule.ContextRule == "v1inftrap" && !V1InfTrapCheck(form)) continue;
                        if (entry.Rule.ContextRule == "saspecial" && !SaSpecialCheck(form, entry.Rule)) continue;
                        if (entry.Rule.ContextRule == "temirurule" && !TemiruCheck(form, entry.Rule)) continue;
                        break;
                }

                if (string.IsNullOrEmpty(entry.Rule.Detail) && form.Tags.Count == 0)
                    continue;

                if (form.Tags.Count > 0 && form.Tags[^1] != entry.VirtualRule.ConTag)
                    continue;

                var prefixLength = text.Length - entry.VirtualRule.ConEnd.Length;
                Span<char> buffer = stackalloc char[prefixLength + entry.VirtualRule.DecEnd.Length];
                text.AsSpan(0, prefixLength).CopyTo(buffer);
                entry.VirtualRule.DecEnd.AsSpan().CopyTo(buffer[prefixLength..]);
                var newText = new string(buffer);

                if (newText.Equals(form.OriginalText, StringComparison.Ordinal))
                    continue;

                var newForm = CreateNewForm(form, newText, entry.VirtualRule.ConTag,
                    entry.VirtualRule.DecTag, entry.VirtualRule.Detail);

                if (!processed.Contains(newForm) && !novel.Contains(newForm) && !newNovel.Contains(newForm))
                    newNovel.Add(newForm);
            }
        }
    }

    private DeconjugationForm CreateInitialForm(string text)
    {
        return new DeconjugationForm(text, text, [], new HashSet<string>(StringComparer.Ordinal), []);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private HashSet<DeconjugationForm>? ApplyRule(DeconjugationForm form, DeconjugationRule rule)
    {
        return rule.Type switch
        {
            "stdrule" => StdRuleDeconjugate(form, rule),
            "rewriterule" => RewriteRuleDeconjugate(form, rule),
            "onlyfinalrule" => OnlyFinalRuleDeconjugate(form, rule),
            "neverfinalrule" => NeverFinalRuleDeconjugate(form, rule),
            "contextrule" => ContextRuleDeconjugate(form, rule),
            "substitution" => SubstitutionDeconjugate(form, rule),
            _ => null
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldSkipForm(DeconjugationForm form)
    {
        return string.IsNullOrEmpty(form.Text) ||
               form.Text.Length > form.OriginalText.Length + MaxTextGrowthFromOriginal ||
               form.Tags.Count > form.OriginalText.Length + MaxChainDepthAboveInputLength;
    }

    private HashSet<DeconjugationForm>? StdRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule)
    {
        if (string.IsNullOrEmpty(rule.Detail) && form.Tags.Count == 0)
            return null;

        if (rule.DecEnd.Length == 1)
        {
            var virtualRule = new DeconjugationVirtualRule(
                rule.DecEnd[0],
                rule.ConEnd[0],
                rule.DecTag?[0],
                rule.ConTag?[0],
                rule.Detail
            );

            if (StdRuleDeconjugateInner(form, virtualRule) is { } hit)
                return new HashSet<DeconjugationForm>(1) { hit };

            return null;
        }

        if (!_virtualRulesCache.TryGetValue(rule, out var cachedVirtualRules))
            return null;

        HashSet<DeconjugationForm>? collection = null;
        
        foreach (var virtualRule in cachedVirtualRules)
        {
            if (StdRuleDeconjugateInner(form, virtualRule) is { } hit)
            {
                collection ??= new HashSet<DeconjugationForm>(cachedVirtualRules.Length);
                collection.Add(hit);
            }
        }

        return collection;
    }

    private DeconjugationForm? StdRuleDeconjugateInner(DeconjugationForm form, DeconjugationVirtualRule rule)
    {
        if (!form.Text.EndsWith(rule.ConEnd, StringComparison.Ordinal))
            return null;

        if (form.Tags.Count > 0 && form.Tags[^1] != rule.ConTag)
            return null;

        var prefixLength = form.Text.Length - rule.ConEnd.Length;
        
        // Use stackalloc for small strings to avoid heap allocation
        Span<char> buffer = stackalloc char[prefixLength + rule.DecEnd.Length];
        form.Text.AsSpan(0, prefixLength).CopyTo(buffer);
        rule.DecEnd.AsSpan().CopyTo(buffer[prefixLength..]);
        var newText = new string(buffer);

        if (newText.Equals(form.OriginalText, StringComparison.Ordinal))
            return null;

        return CreateNewForm(form, newText, rule.ConTag, rule.DecTag, rule.Detail);
    }

    private DeconjugationForm CreateNewForm(DeconjugationForm form, string newText, string? conTag, string? decTag, string detail)
    {
        int existingTagCount = form.Tags.Count;
        bool addConTag = existingTagCount == 0 && conTag != null;
        bool addDecTag = decTag != null;
        int newTagCount = existingTagCount + (addConTag ? 1 : 0) + (addDecTag ? 1 : 0);

        var tags = new string[newTagCount];
        for (int i = 0; i < existingTagCount; i++)
            tags[i] = form.Tags[i];
        int idx = existingTagCount;
        if (addConTag) tags[idx++] = conTag!;
        if (addDecTag) tags[idx++] = decTag!;

        var seenText = new HashSet<string>(form.SeenText.Count + 2, StringComparer.Ordinal);
        foreach (var s in form.SeenText)
            seenText.Add(s);
        if (seenText.Count == 0)
            seenText.Add(form.Text);
        seenText.Add(newText);

        int procCount = form.Process.Count;
        var process = new string[procCount + 1];
        for (int i = 0; i < procCount; i++)
            process[i] = form.Process[i];
        process[procCount] = detail;

        return new DeconjugationForm(newText, form.OriginalText, tags, seenText, process);
    }

    private HashSet<DeconjugationForm>? SubstitutionDeconjugate(DeconjugationForm form, DeconjugationRule rule)
    {
        if (form.Process.Count != 0 || string.IsNullOrEmpty(form.Text))
            return null;

        if (rule.DecEnd.Length == 1)
        {
            if (SubstitutionInnerOptimized(form, rule.ConEnd[0], rule.DecEnd[0], rule.Detail) is { } hit)
                return new HashSet<DeconjugationForm>(1) { hit };
            return null;
        }

        HashSet<DeconjugationForm>? collection = null;
        
        for (int i = 0; i < rule.DecEnd.Length; i++)
        {
            var decEnd = rule.DecEnd.ElementAtOrDefault(i) ?? rule.DecEnd[0];
            var conEnd = rule.ConEnd.ElementAtOrDefault(i) ?? rule.ConEnd[0];
            
            if (SubstitutionInnerOptimized(form, conEnd, decEnd, rule.Detail) is { } ret)
            {
                collection ??= new HashSet<DeconjugationForm>(rule.DecEnd.Length);
                collection.Add(ret);
            }
        }

        return collection;
    }

    private DeconjugationForm? SubstitutionInnerOptimized(DeconjugationForm form, string conEnd, string decEnd, string detail)
    {
        if (!form.Text.Contains(conEnd, StringComparison.Ordinal))
            return null;

        var newText = form.Text.Replace(conEnd, decEnd, StringComparison.Ordinal);
        return CreateSubstitutionForm(form, newText, detail);
    }

    private DeconjugationForm CreateSubstitutionForm(DeconjugationForm form, string newText, string detail)
    {
        var seenText = new HashSet<string>(form.SeenText.Count + 2, StringComparer.Ordinal);
        foreach (var s in form.SeenText)
            seenText.Add(s);
        if (seenText.Count == 0)
            seenText.Add(form.Text);
        seenText.Add(newText);

        int procCount = form.Process.Count;
        var process = new string[procCount + 1];
        for (int i = 0; i < procCount; i++)
            process[i] = form.Process[i];
        process[procCount] = detail;

        int tagCount = form.Tags.Count;
        var tags = new string[tagCount];
        for (int i = 0; i < tagCount; i++)
            tags[i] = form.Tags[i];

        return new DeconjugationForm(newText, form.OriginalText, tags, seenText, process);
    }

    private HashSet<DeconjugationForm>? RewriteRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule)
    {
        return form.Text.Equals(rule.ConEnd[0], StringComparison.Ordinal) ? StdRuleDeconjugate(form, rule) : null;
    }

    private HashSet<DeconjugationForm>? OnlyFinalRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule)
    {
        return form.Tags.Count == 0 ? StdRuleDeconjugate(form, rule) : null;
    }

    private HashSet<DeconjugationForm>? NeverFinalRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule)
    {
        return form.Tags.Count != 0 ? StdRuleDeconjugate(form, rule) : null;
    }

    private HashSet<DeconjugationForm>? ContextRuleDeconjugate(DeconjugationForm form, DeconjugationRule rule)
    {
        if (rule.ContextRule == "v1inftrap" && !V1InfTrapCheck(form))
            return null;

        if (rule.ContextRule == "saspecial" && !SaSpecialCheck(form, rule))
            return null;

        if (rule.ContextRule == "temirurule" && !TemiruCheck(form, rule))
            return null;

        return StdRuleDeconjugate(form, rule);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TemiruCheck(DeconjugationForm form, DeconjugationRule rule)
    {
        var conEnd = rule.ConEnd[0];
        if (!form.Text.EndsWith(conEnd, StringComparison.Ordinal)) return false;
        var prefix = form.Text.AsSpan(0, form.Text.Length - conEnd.Length);
        return prefix.EndsWith("て") || prefix.EndsWith("で");
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
