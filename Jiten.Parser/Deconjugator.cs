using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Jiten.Parser;

public class Deconjugator
{
    private static readonly Lazy<Deconjugator> _instance =
        new Lazy<Deconjugator>(() => new Deconjugator(), LazyThreadSafetyMode.ExecutionAndPublication);

    public static Deconjugator Instance => _instance.Value;

    private const int DefaultMaxCacheEntries = 10_000;
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

    private readonly DeconjugationRule[] _rules;

    // Cache virtual rules to avoid recreating them
    private readonly Dictionary<DeconjugationRule, DeconjugationVirtualRule[]> _virtualRulesCache = new();

    private static readonly bool UseCache = true;

    internal sealed record DeconjugationCacheStats(
        long Hits,
        long Misses,
        long Stores,
        long Evictions,
        int Count,
        int MaxEntries);

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
        {
            // Pre-cache virtual rules
            CacheVirtualRules(rule);
        }
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
        if (text.Length > 20 || forms.Count >= 55)
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

    internal DeconjugationCacheStats GetCacheStats()
    {
        return new DeconjugationCacheStats(
            Interlocked.Read(ref _cacheHitCount),
            Interlocked.Read(ref _cacheMissCount),
            Interlocked.Read(ref _cacheStoreCount),
            Interlocked.Read(ref _cacheEvictionCount),
            _gen0.Count + (_gen1?.Count ?? 0),
            _maxCacheEntries);
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
        var instrStart = Diagnostics.ParserBenchmarkInstrumentation.Now();
        try
        {
            if (UseCache && TryGetCached(text, out var cached))
            {
                Diagnostics.ParserBenchmarkInstrumentation.RecordDeconjugatorCall(cacheHit: true);
                return cached;
            }

            Diagnostics.ParserBenchmarkInstrumentation.RecordDeconjugatorCall(cacheHit: false);

            if (string.IsNullOrEmpty(text))
                return [];

        var processed = new HashSet<DeconjugationForm>(Math.Min(text.Length * 2, 100));
        var novel = new HashSet<DeconjugationForm>(20);
        var startForm = CreateInitialForm(text);
        novel.Add(startForm);

        var ruleCount = _rules.Length;

        while (novel.Count > 0)
        {
            var newNovel = new HashSet<DeconjugationForm>(novel.Count * 2);

            foreach (var form in novel)
            {
                if (ShouldSkipForm(form))
                    continue;

                // Use for loop instead of foreach for better performance
                for (int i = 0; i < ruleCount; i++)
                {
                    var rule = _rules[i];
                    var newForms = ApplyRule(form, rule);

                    if (newForms == null) continue;

                    foreach (var f in newForms)
                    {
                        if (!processed.Contains(f) && !novel.Contains(f) && !newNovel.Contains(f))
                            newNovel.Add(f);
                    }
                }
            }

            processed.UnionWith(novel);
            novel = newNovel;
        }

        var result = processed
            .OrderByDescending(f => f.Text.Length)
            .ThenBy(f => f.Text, StringComparer.Ordinal)
            .ToList();

        if (UseCache)
            StoreCached(text, result);

            return result;
        }
        finally
        {
            Diagnostics.ParserBenchmarkInstrumentation.AddDeconjugator(instrStart);
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
               form.Text.Length > form.OriginalText.Length + 10 ||
               form.Tags.Count > form.OriginalText.Length + 6;
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
