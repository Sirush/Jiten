using System.Text.Json;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Resolution;

namespace Jiten.Parser.Scoring;

// Port of Ichiran's calc-score prop-score builder (dict.lisp:777, §10.7 of ichiran-analysis.md).
// Output is the ADDITIVE prop_score plus the (kanji-p, primary-p, common-p, long-p) quadruple.
// The length multiplier (§10.8) and use-length bonus (§10.9) are NOT applied here — they run
// downstream in the beam node scorer so the separation matches Ichiran:
//   score = prop_score × length_multiplier_coeff(len, class) + use_length_bonus + score_mod
//
// Design notes:
//   - Lists (*final-prt*, *semi-final-prt*, *non-final-prt*, *copulae*, *skip-words*) are
//     matched by surface text rather than seq because our WordIds align with JMdict ent_seq
//     but we don't have the Ichiran-specific curated seq sets. Surface matching is close
//     enough for the Japanese grammar particles these lists cover.
//   - `*weak-conj-forms*` / `*skip-conj-forms*` are approximated by mapping our deconjugator
//     `Detail` tags to Ichiran conj-type classes (adj-stem, negative-stem, causative-su,
//     adj-literary, negative-volitional). If every tag in the chain maps to a weak form,
//     conj-types-p becomes false — which disables primary-p branches and the common bonus,
//     matching Ichiran. Skip forms cause an early 0-score return.
//   - primary-p uses the simpler Ichiran branch: (ord=0 OR cop-da-p) AND (kanji-p OR conj-types-p)
//     AND (kanji-p AND NOT prefer-kana OR common-p AND pronoun-p OR n-kanji=0).
//     The prefer-kana-specific branches are approximated via `preferKana && !kanjiP`.
//   - secondary-conj-p is set to false (we don't thread conj-data.via through).
internal readonly record struct Kpcl(bool KanjiP, bool PrimaryP, bool CommonP, bool LongP);

internal readonly record struct IchiranPropScore(
    int Score,
    Kpcl Flags,
    int Len,
    int NKanji,
    int Common,
    bool KatakanaP,
    int CommonBonus = 0);

internal static class IchiranPropScorer
{
    // Process-wide WordId → non-archaic POS list. Set once at parser startup by
    // Runtime initialisation; null/empty defaults to the scorer using the word's
    // full PartsOfSpeech (permissive, may count arch-only senses).
    public static IReadOnlyDictionary<int, List<string>>? NonArchaicPosOverride { get; set; }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int WordId, string Surface, int ChainHash, bool IsFinal, int UseLen), IchiranPropScore>
        _propCache = new();

    private static int HashConjChain(IReadOnlyList<string>? chain)
    {
        if (chain == null || chain.Count == 0) return 0;
        var h = new HashCode();
        for (int i = 0; i < chain.Count; i++) h.Add(chain[i], StringComparer.Ordinal);
        return h.ToHashCode();
    }

    // *final-prt* (dict-errata.lisp:1182) — must be sentence-final, extra bonus
    private static readonly HashSet<int> FinalParticleSeqs = new()
    {
        2017770, 2425930, 2130430, 2029130, 2834812, 2718360, 2201380, 2722170, 2751630,
    };

    // *semi-final-prt* (dict-errata.lisp:1196) — *final-prt* ∪ {さ, し, な, ね, わ}
    private static readonly HashSet<int> SemiFinalParticleSeqs = new(FinalParticleSeqs)
    {
        2029120, 2086640, 2029110, 2029080, 2029100,
    };

    // *non-final-prt* (dict-errata.lisp:1209) — ん (no final bonus)
    private static readonly HashSet<int> NonFinalParticleSeqs = new() { 2139720 };

    // Ichiran `set-common` errata (dict-errata.lisp:~600-1150, 324 entries).
    // Manually curated common-value overrides per (WordId, Form.Text). Each entry
    // either promotes a reading to common=N (highest tier 0, then 1-9 for nf##, etc.)
    // or demotes it to :null (our -1). Without these, kana-only compound entries
    // like さほど, いつか, ことし lose common-p status and score 4× lower than Ichiran.
    // Key: (WordId, Text). Value: N>=0 is the common integer; -1 is Ichiran's :null.
    private static readonly Dictionary<(int, string), int> CommonErrata = LoadCommonErrata();

    private sealed record CommonErrataEntry(string? Table, int WordId, string? Text, int? Common);

    private static Dictionary<(int, string), int> LoadCommonErrata()
    {
        var map = new Dictionary<(int, string), int>();
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "ichiran_common_errata.json");
            if (!File.Exists(path)) return map;
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<CommonErrataEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            }) ?? new();
            foreach (var e in entries)
            {
                if (e.Text == null) continue;
                map[(e.WordId, e.Text)] = e.Common ?? -1;
            }
        }
        catch { /* missing/malformed → run without errata */ }
        return map;
    }

    // Ichiran errata that clears `entry.root-p` per-WordId (dict-errata.lisp:544).
    // An entry loses its `root-p` → `root-p` in calc-score becomes false even for the
    // unconjugated surface. Affects conj-types-p, primary-p branches, and the
    // skip-by-conj-data gate.
    private static readonly HashSet<int> NonRootEntries = new()
    {
        2611370, // なり — errata removes senses and unsets root-p
    };

    // Ichiran dict-errata.lisp delete-sense-prop "pos" entries. Each strips a specific POS
    // tag from the word's effective posList (affects particleP, pronounP, etc.).
    // "misc"="arch" deletions are handled via NonArchaicOverrideIds below.
    // "misc"="uk" deletions/additions are handled via UkDeleteErrata/UkAddErrata below.
    private static readonly Dictionary<int, HashSet<string>> StripPosByWordId = new()
    {
        [2629920] = new() { "adv-to" },
        [2122310] = new() { "prt" },    // え — prt senses absent from Ichiran's DB
        [1245280] = new() { "adj-no" }, // 空 から
        [1392570] = new() { "adj-no" }, // 前 ぜん
        [2647210] = new() { "suf" },
        [1215240] = new() { "ctr" },
        [1188270] = new() { "pn" },
        [1240530] = new() { "ctr" },    // 玉
        [1138570] = new() { "ctr" },    // ラウンド
    };

    // Ichiran dict-errata.lisp delete-sense-prop "misc" "uk": removes uk tag, making
    // preferKana=false. This enables the kanji-primary primary-p branch and allows
    // AllFromKanjiForms to correctly demote inherited kana-form priorities.
    private static readonly HashSet<int> UkDeleteErrata = new()
    {
        1611000, 1305070, 1583470, 1446760, 1302910, 2802220, 1535790, 2119750,
        2220330, 1207600, 1399970, 2094480, 2729170, 1580640, 1569440, 2423450,
        1578850, 1609500, 1444150, 1546640, 1314490, 2643710, 1611260, 2208960,
        1155020, 1208240, 1207590, 1279680, 1469810, 1474370, 1609300, 1612920,
        2827450, 1333570, 1610400, 2097190, 2021030, 1586730, 1441400, 1303400,
        1434020, 1196520, 1414190, 1896380, 1157000, 1576360, 1598660, 1604890,
        1632980, 1715710, 1426680, 1547720, 1495770, 2611890, 2854117, 2859257,
        1198890, 1236660, 2859279, 1591420,
    };

    // Ichiran dict-errata.lisp add-sense-prop N "misc" "uk": adds uk tag, making
    // preferKana=true. This enables prefer-kana primary-p branches and blocks the
    // kanji-primary path for words Ichiran considers kana-preferred.
    private static readonly HashSet<int> UkAddErrata = new()
    {
        1394680, 2272830, 1270680, 1541560, 1739410, 1207610, 2424410, 1387080,
        1509350, 1637460, 1569590, 1590540, 1430200, 1188380, 1258330, 2217330,
        1238460, 2722640, 1527140, 1208870, 2756830, 1346290, 1615340, 1565100,
        1219510, 1616370, 1586290, 1257260, 2679820, 1590390, 1180540, 2826371,
    };

    // Ichiran dict-errata.lisp delete-sense-prop "misc"="arch": removes the arch tag from a
    // sense, making the entry non-archaic in Ichiran's model. We override IsFullyArchaic=false
    // for these so primary-p and the common bonus are not suppressed.
    // 1270350 = ございます (ご座います) — arch tag removed, allowing normal scoring
    // 2217330 = ワイ (Kansai dialect first-person pronoun) — arch tag removed
    private static readonly HashSet<int> NonArchaicOverrideIds = new() { 1270350, 2217330 };

    // POS-tag additions per Ichiran's DB state (not in our JMdict). Ichiran's 2827864
    // なので carries POS (exp prt), our JMdict has [exp, conj, col]. The prt flag makes
    // particleP=true in calc-score, unlocking the +2 particle bonus — eliminates the 18pt
    // node-score delta we were seeing vs Ichiran's output for this entry. Verified directly
    // via `ichiran-cli -e` calc-score dump: Ichiran prop=6, ours was 4.
    private static readonly HashSet<int> ParticleOverrideIds = new() { 2827864 };

    // *skip-words* (dict-errata.lisp:1155) — words to score at 0. Ichiran curates
    // these as noise-prone single-segment matches (often conjugation fragments that
    // look like standalone words). Seq-based to match JMdict entries precisely.
    private static readonly HashSet<int> SkipWordSeqs = new()
    {
        2822120, 2013800, 2108590, 2029040, 2428180, 2654250, 2561100, 2210270,
        2210710, 2257550, 2210320, 2017560, 2394890, 2194000, 2568000, 2537250,
        2760890, 2831062, 2831063, 2029030, 2568020, 900000, 2827357,
    };

    // *copulae* — だ (seq 2089020 in JMdict). Ichiran's cop-da-p is
    // `(intersection seq-set *copulae*)` where seq-set = (seq . conj-of). Conjugations
    // of だ carry conj-of=(2089020) so their seq-set intersects *copulae*. To match,
    // we treat a word as cop-da-p if it's だ itself OR a known conjugation of だ.
    private const int CopulaDaWordId = 2089020;
    // Conjugations OF だ that Ichiran's conj-prop tracks. Sourced from JMDict's
    // conjugations for seq 2089020. Adding these makes cop-da-p fire for です/でしょ/etc.,
    // which unlocks the longP || copDaP branches in primary/common bonuses.
    private static readonly HashSet<int> CopDaConjugatedSeqs = new()
    {
        2089020, // だ
        1628500, // です
        1008420, // でしょう / でしょ
        2253080, // でございます
    };

    // *weak-conj-forms* / *skip-conj-forms* handling moved to
    // Resolution/ConjChainAnalysis.cs + ConjFormMatcher. The scorer now derives
    // a structured (HasNegative, HasFormal, ConjTypes[]) view from the chain and
    // matches against Ichiran's pattern tables verbatim — no more parallel tag
    // sets, no more drift against IchiranConjType.

    // JMDict POS tags (authoritative subset from JMDictHelper.EntityMap). Our
    // ingestion flattens POS, misc, and field tags into a single PartsOfSpeech
    // list, but Ichiran's `posi` (dict.lisp:826) pulls from sense_prop WHERE
    // tag='pos' — misc (uk, arch, pol, col, ...) and field tags must be
    // excluded when we want pos-only semantics (e.g. no-common-bonus's
    // `(equal posi '("int"))` check: 今日は is POS=[int]+misc=[uk], not POS=[int,uk]).
    private static readonly HashSet<string> RealPosTags = new(StringComparer.Ordinal)
    {
        "adj-f", "adj-i", "adj-ix", "adj-kari", "adj-ku", "adj-na", "adj-nari",
        "adj-no", "adj-pn", "adj-shiku", "adj-t",
        "adv", "adv-to", "aux", "aux-adj", "aux-v",
        "conj", "cop", "cop-da", "ctr", "exp", "int",
        "n", "n-adv", "n-pr", "n-pref", "n-suf", "n-t", "num",
        "pn", "pref", "prt", "suf", "unc", "v-unspec",
        "v1", "v1-s",
        "v2a-s", "v2b-k", "v2b-s", "v2d-k", "v2d-s", "v2g-k", "v2g-s",
        "v2h-k", "v2h-s", "v2k-k", "v2k-s", "v2m-k", "v2m-s", "v2n-s",
        "v2r-k", "v2r-s", "v2s-s", "v2t-k", "v2t-s", "v2w-s",
        "v2y-k", "v2y-s", "v2z-s",
        "v4b", "v4g", "v4h", "v4k", "v4m", "v4n", "v4r", "v4s", "v4t",
        "v5aru", "v5b", "v5g", "v5k", "v5k-s", "v5m", "v5n",
        "v5r", "v5r-i", "v5s", "v5t", "v5u", "v5u-s", "v5uru",
        "vi", "vk", "vn", "vr", "vs", "vs-c", "vs-i", "vs-s", "vt", "vz",
    };

    public static IchiranPropScore Compute(
        JmDictWord word,
        JmDictWordForm form,
        string surface,
        IReadOnlyList<string>? conjChain,
        bool isSentenceFinal,
        int? useLength,
        string? scoreBaseText = null)
    {
        int chainHash = HashConjChain(conjChain);
        int useLenKey = useLength ?? -1;
        bool cacheable = scoreBaseText == null;
        if (cacheable)
        {
            var cacheKey = (word.WordId, surface, chainHash, isSentenceFinal, useLenKey);
            if (_propCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        bool isCompound = useLength.HasValue;
        string text = !string.IsNullOrEmpty(scoreBaseText) ? scoreBaseText
                    : (isCompound && !string.IsNullOrEmpty(form.Text) ? form.Text : (surface ?? string.Empty));
        int len = Math.Max(1, CountMora(text));
        int nKanji = CountKanji(text);

        // §10.4 early exit — *skip-words* zeroes the score regardless of other signals.
        // Also: *final-prt* words score 0 unless they're genuinely at the sentence end.
        // Both now seq-based (WordId) rather than surface-based, matching Ichiran.
        if (SkipWordSeqs.Contains(word.WordId))
            return new IchiranPropScore(0, new Kpcl(false, false, false, false), len, nKanji, -1, false);

        if (!isSentenceFinal && FinalParticleSeqs.Contains(word.WordId))
            return new IchiranPropScore(0, new Kpcl(false, false, false, false), len, nKanji, -1, false);

        // *skip-conj-forms* (dict.lisp:855-857): `(not root-p) AND skip-by-conj-data` → 0.
        // When a non-root (conjugated) match's entire chain consists of skip-forms,
        // the whole segment is noise. Verbatim port of dict-errata.lisp:1310-1314 via
        // ConjFormMatcher — (10,t,any), (3,t,t), ("vs-s",5,any,any).
        var chainAnalysis = ConjChainAnalysis.From(conjChain);
        if (chainAnalysis.MeaningfulStepCount > 0
            && ConjFormMatcher.AllMatchSkip(chainAnalysis, word.PartsOfSpeech))
            return new IchiranPropScore(0, new Kpcl(false, false, false, false), len, nKanji, -1, false);

        // Script-mismatch filter: Ichiran's find-word-as-hiragana (dict.lisp:1094) only
        // fires for PURE katakana runs. Our TableCandidateProvider has a laxer gate to
        // cover mixed-script emphasis cases (うわッ → うわっ). But mixed-script surfaces
        // like コレは that match a pure-hiragana multi-word [exp] entry (e.g. これは
        // seq 2176280) are almost always wrong — the surface is katakana+hiragana,
        // the entry is hiragana compound. Drop these: return 0 score.
        if (!string.IsNullOrEmpty(surface) && !string.IsNullOrEmpty(form.Text)
            && ContainsKatakana(surface) && ContainsHiragana(surface)
            && IsPureHiragana(form.Text)
            && word.PartsOfSpeech.Contains("exp"))
            return new IchiranPropScore(0, new Kpcl(false, false, false, false), len, nKanji, -1, false);

        // Spurious ん-contraction filter for long multi-word [exp adj-i] entries.
        // Our EmitJitenNegativeVariants / deconjugator adds ない → ん as a slang
        // abbreviation. That's correct for short lexical adj-i (じゃない → じゃん)
        // and productive verb negatives (分からない → 分からん), but for long
        // lexicalized phrases like 使い物にならない the trailing ない is baked into
        // the entry, not a productive suffix, so 使い物にならん isn't a real form.
        // Ichiran only generates these through its conjo.csv paradigm (which
        // doesn't produce ん-contraction at all) plus a separate `ならん` entry
        // (seq 2083990) — our conj table emits the synthetic 使い物にならん and
        // scores it as a 7-char kanji compound (~1157), swamping the correct
        // 使い物 | に | ならん | だろ split. Gate: mora-length of the form before
        // the trailing ない > 3 (「じゃ」=1 mora keeps legitimate じゃん). Only
        // fires when surface = form[:-2] + ん.
        if (HasConjTag(conjChain, "negative")
            && word.PartsOfSpeech.Contains("exp")
            && word.PartsOfSpeech.Contains("adj-i")
            && !string.IsNullOrEmpty(surface)
            && !string.IsNullOrEmpty(form.Text)
            && surface.Length == form.Text.Length - 1
            && surface[^1] == 'ん'
            && form.Text.EndsWith("ない", StringComparison.Ordinal)
            && form.Text.AsSpan(0, form.Text.Length - 2)
                        .SequenceEqual(surface.AsSpan(0, surface.Length - 1))
            && CountMora(form.Text.Substring(0, form.Text.Length - 2)) > 3)
            return new IchiranPropScore(0, new Kpcl(false, false, false, false), len, nKanji, -1, false);

        // Te-form + い absorption guard: a non-compound surface ending in "てい"/"でい"
        // whose chain contains a te-form step is almost certainly boundary bleed where
        // the beam absorbed the leading "い" from the next word into the te-form stem
        // (e.g. 間違えて + い from いらっしゃる → 間違えてい chain=['(te form)']).
        if (!useLength.HasValue
            && conjChain != null
            && HasConjTag(conjChain, "(te form)")
            && surface.Length >= 3
            && (surface.EndsWith("てい", StringComparison.Ordinal)
                || surface.EndsWith("でい", StringComparison.Ordinal)))
            return new IchiranPropScore(0, new Kpcl(false, false, false, false), len, nKanji, -1, false);

        bool kanjiP = nKanji > 0;
        bool katakanaP = !kanjiP && ContainsKatakana(text);

        // Conjugation status.
        // Ichiran: `root-p = (or ctr-mode (and (not conj-only) (root-p entry)))` (dict.lisp:805).
        // entry.root-p defaults to true in JMdict loading (dict-load.lisp:142). Errata can
        // clear it per-entry. The only known clearing is WordId 2611370 (なり) in
        // dict-errata.lisp:544. Ported here as NonRootEntries.
        bool conjOnly = conjChain != null && conjChain.Count > 0;
        bool rootP = !conjOnly && !NonRootEntries.Contains(word.WordId);
        bool hasTeForm = HasConjTag(conjChain, "(te form)") || HasConjTag(conjChain, "(te-form)");
        bool hasNegative = HasConjTag(conjChain, "negative");
        bool hasMasuStem = HasConjTag(conjChain, "(infinitive)") || HasConjTag(conjChain, "masu-stem")
                           || HasConjTag(conjChain, "ren-youkei") || HasConjTag(conjChain, "ren'youkei");

        // secondary-conj-p (dict.lisp:808) — in Ichiran, a conjugation reached via another
        // conjugation (conj-data-via is non-null). Affects the primary bonus (+2 when !kanjiP)
        // and the common-bonus branching.
        //
        // Our deconjugator emits intermediate stem-transition tags alongside the actual
        // conjugation steps. For a plain te-form of a v5m/v5b/v5n/v5g verb the chain is
        //   [(unstressed infinitive), (te form)]
        // which has two tags but represents ONE conjugation category (te-form). To match
        // Ichiran's via-based semantics we count only real conjugation steps, excluding
        // intermediate stem tags.
        bool secondaryConjP = chainAnalysis.RealConjStepCount >= 2;

        // conj-types-p (dict.lisp:816-819):
        //   (or root-p use-length (notevery (weak?) conj-props))
        // FALSE only when the word is non-root, non-compound AND every conj-prop is weak.
        // Matched via ConjFormMatcher using structured (type, neg, fml) patterns.
        //
        // Flag-only *unrelated-surface* chain guard: Ichiran's conj-data always carries
        // a conj-type. Our deconjugator occasionally emits chains containing only flag
        // tags (bare "negative", dialectal negatives) — structurally invalid in
        // Ichiran's model. Most of the time the surface still shares a leading character
        // with the dict form (知らん/知る, 気をつけねー/気をつける) — a legitimate negative
        // form that just happens to carry a type-less chain. But garbage deconjugations
        // like `ってん → さがる [negative]` share NO prefix — the surface and the dict
        // form are structurally unrelated. Only treat the unrelated-surface case as weak.
        bool flagOnlyChain = conjOnly && chainAnalysis.MeaningfulStepCount == 0;
        // Irregular-conjugation POS (vs-i=する, vk=くる, vs-s/vs-c=suru-compounds) have
        // neg/conj stems that legitimately don't share first char with the dict form
        // (す→し for する, く→こ for くる). Exempt them from the unrelated-surface guard
        // so `しない` [negative] via 1157170 stays scored as a legitimate conjugation.
        bool irregularConj = word.PartsOfSpeech.Contains("vs-i")
                             || word.PartsOfSpeech.Contains("vk")
                             || word.PartsOfSpeech.Contains("vs-s")
                             || word.PartsOfSpeech.Contains("vs-c")
                             || word.PartsOfSpeech.Contains("cop");
        bool unrelatedFlagChain = flagOnlyChain && !irregularConj
                                  && !SurfaceSharesLeadWithForm(surface, form.Text);
        bool allWeak = conjOnly && !useLength.HasValue
                       && (unrelatedFlagChain
                           || (chainAnalysis.MeaningfulStepCount > 0
                               && ConjFormMatcher.AllMatchWeak(chainAnalysis, word.PartsOfSpeech)));

        // Empirical fix: conj-only kana masu-stem edges of len ≤ 2 that Ichiran's lattice
        // doesn't surface (kana-text lookup only returns root forms; conjugated readings
        // require the conjugation table join, which is gated elsewhere). Our conj table
        // emits them freely, and they steal boundaries from noun+particle splits (はね,
        // 次が-style mid-kanji-boundary cases). Mark them weak so conj-types-p=false
        // shuts off the primary-p preferKana branch, dropping the score to baseline.
        bool shortConjMasuStem = conjOnly && !useLength.HasValue
                                 && len <= 2 && !kanjiP
                                 && chainAnalysis.MeaningfulStepCount == 1
                                 && chainAnalysis.ConjTypes.Count == 1
                                 && chainAnalysis.ConjTypes[0] == IchiranConjType.Continuative;
        if (shortConjMasuStem) allWeak = true;

        bool conjTypesP = rootP || useLength.HasValue || !allWeak;

        // Ichiran's `get-non-arch-posi` (dict.lisp ~860): look up pre-computed non-arch
        // POS from the process-wide map. Falls back to the word's full PartsOfSpeech
        // when the map hasn't been initialised.
        IReadOnlyList<string> posList;
        if (NonArchaicPosOverride != null
            && NonArchaicPosOverride.TryGetValue(word.WordId, out var napOverride))
            posList = napOverride;
        else
            posList = word.PartsOfSpeech;
        if (StripPosByWordId.TryGetValue(word.WordId, out var stripTags))
        {
            var filtered = new List<string>(posList.Count);
            foreach (var p in posList)
                if (!stripTags.Contains(p)) filtered.Add(p);
            posList = filtered;
        }
        bool particleP = posList.Contains("prt") || ParticleOverrideIds.Contains(word.WordId);
        bool pronounP = posList.Contains("pn");
        bool copDaP = CopDaConjugatedSeqs.Contains(word.WordId);
        bool preferKana = posList.Contains("uk");
        if (UkDeleteErrata.Contains(word.WordId)) preferKana = false;
        else if (UkAddErrata.Contains(word.WordId)) preferKana = true;
        bool isArch = word.IsFullyArchaic && !NonArchaicOverrideIds.Contains(word.WordId);
        // Ichiran's no-common-bonus uses `(equal posi '("int"))` — pos-only comparison.
        // Our posList mixes misc tags, so filter to real POS before the check.
        int realPosCount = 0;
        bool hasInt = false;
        foreach (var p in posList)
        {
            if (!RealPosTags.Contains(p)) continue;
            realPosCount++;
            if (p == "int") hasInt = true;
        }
        bool intOnly = realPosCount == 1 && hasInt;

        // Common value: lower = more common; -1 = no frequency marker.
        // Ichiran scores by reading-variant: `(common reading)` — the specific reading's
        // priority, not the aggregated word's. A kana form of a word with ichi1-only on
        // kanji returns :null (no priority) on the kana.
        //
        // §10.5 bump: Ichiran starts conj-only readings with common=:null, then sets
        // common=0 if any source form is common. Net effect: conj-only matches cap at
        // common=0 regardless of the specific priority.
        //
        // Data-layer workaround: our ingestion sometimes copies kanji-form priorities
        // (ke_pri) onto kana forms. When the matched form is kana, the entry has kanji
        // forms, it isn't "uk" / primary-nokanji, AND the kana form's priorities are a
        // subset of the kanji form's priorities, treat the kana-form priorities as
        // inherited noise and drop them. Mirrors Ichiran's per-kana-text common=:null
        // for kanji-primary entries without an explicit re_pri.
        int common;
        // Ichiran errata override (dict-errata.lisp set-common list) takes precedence
        // over priority-derived common. Keyed by (WordId, Form.Text).
        if (form.Text != null && CommonErrata.TryGetValue((word.WordId, form.Text), out int errataCommon))
        {
            common = errataCommon;
        }
        else if (form.FormType != JmDictFormType.KanjiForm
                 && !preferKana
                 && !IsPrimaryNokanjiEntry(word)
                 && form.Priorities != null && form.Priorities.Count > 0
                 && AllFromKanjiForms(word, form.Priorities)
                 && form.Priorities.Count < MaxKanjiPriorityCount(word))
            common = -1;
        else
            common = ComputeCommon(form.Priorities);
        if (conjOnly && common < 0)
        {
            // Bump: if any form has a priority, set common=0 (most common).
            // Ichiran (dict.lisp:859-870) bumps ONLY when (not common-p) — i.e. when the
            // matched conjugation reading has NO own priority. Previously we bumped
            // unconditionally, which over-promoted kana conj forms whose matched form
            // already had an inherited priority.
            bool anyCommon = false;
            foreach (var f in word.Forms)
            {
                if (f.Priorities != null && f.Priorities.Count > 0) { anyCommon = true; break; }
            }
            if (anyCommon) common = 0;
            // Floor for uk-tagged compound verbs with zero frequency data.
            // Entries like 分かりかねる (6 chars) whose compound forms compete
            // with high-frequency suffix splits. Without a common floor, their
            // prop.Score is too low for the length multiplier to overcome the
            // split's frequency advantage. Gated by form length >= 5 to avoid
            // promoting short kana words.
            else if (preferKana && conjTypesP
                     && form.Text != null && form.Text.Length >= 5)
                common = 5;
        }
        bool commonP = common >= 0;

        // long-p threshold (§10.3) — Ichiran:
        //   (kanji-p AND !prefer-kana AND (root-p-and-no-conj-data OR use-length-and-conj-type-13)) → 2
        //   (common-p AND 0 < common < 10) → 2   ; STRICT — common=0 does NOT match
        //   ((3 OR 9) in conj-types AND NOT use-length) → 4   ; te-form OR volitional
        //   else → 3
        bool hasImperative = HasConjTag(conjChain, "imperative");
        bool hasVolitional = HasConjTag(conjChain, "volitional")
                             || HasConjTag(conjChain, "polite volitional")
                             || HasConjTag(conjChain, "shortened volitional")
                             || HasConjTag(conjChain, "negative volition/conjecture");
        int longThresh;
        if (kanjiP && !preferKana && ((rootP && !conjOnly) || (useLength.HasValue && hasMasuStem)))
            longThresh = 2;
        else if (commonP && common > 0 && common < 10)
            longThresh = 2;
        else if ((hasTeForm || hasVolitional) && !useLength.HasValue)
            longThresh = 4;
        else
            longThresh = 3;
        bool longP = len > longThresh;

        int ord = form.ReadingIndex;
        // §10.5 bump: for conj-only readings, Ichiran propagates ord from the original
        // text (source of the conjugation). If any other form with the same script as
        // the matched form has a lower ord, bump ord down. This helps kana-form conj
        // matches whose source kana variant is the primary kana reading (ord=0).
        if (conjOnly)
        {
            bool surfaceKanjiLike = kanjiP;
            foreach (var f in word.Forms)
            {
                if (f.ReadingIndex < 0) continue;
                bool fKanji = f.FormType == JmDictFormType.KanjiForm;
                if (fKanji != surfaceKanjiLike) continue;
                if (f.ReadingIndex < ord) ord = f.ReadingIndex;
            }
        }

        // primary-p (§10.6) — combined simpler form
        bool primaryP = false;
        if (!isArch)
        {
            // Branch 2: (ord=0 OR cop-da-p) AND (kanji-p OR conj-types-p) AND
            //          (kanji-p AND NOT prefer-kana  OR  common-p AND pronoun-p  OR  entry-n-kanji=0)
            // Ichiran's (n-kanji entry) is the entry's kanji-form count, NOT the matched
            // text's. For a kana-only entry (no kanji forms), the kana reading is genuinely
            // primary. Distinct from our nKanji variable (chars of matched text).
            int entryNKanji = EntryKanjiFormCount(word);
            if ((ord == 0 || copDaP) && (kanjiP || conjTypesP) &&
                ((kanjiP && !preferKana) || (commonP && pronounP) || entryNKanji == 0))
                primaryP = true;

            // (Removed: non-faithful kana-only primary-p lift. The branch was originally
            // added to compensate for un-ported Ichiran mechanisms, but the comment cited
            // あのさ ord=3 — which doesn't actually fire (form 3 has no priority → commonP
            // is false → branch's commonP gate fails). Tightening to ord>=2 cost -2 (なんだ
            // family fixtures); full removal is the cleanest end state. Re-add only if
            // future regressions trace back to a missing primary-p in a real case.)



            // Branch 1: prefer-kana AND conj-types-p AND not kanji-p AND (NOT primary-nokanji OR nokanji)
            if (!primaryP && preferKana && conjTypesP && !kanjiP && (!IsPrimaryNokanjiEntry(word) || form.IsNoKanji))
                primaryP = true;

            // Branch 3: prefer-kana AND kanji-p AND ord=0 AND no "uk"-sense has ord=0.
            // Ichiran queries whether any uk-marked sense is at sense.ord=0. When none,
            // the primary sense of this entry is NOT the uk sense, so the kanji form IS
            // genuinely primary for that sense. Our check: no definition at SenseIndex=0
            // lists "uk" in Misc.
            if (!primaryP && preferKana && kanjiP && ord == 0 && !PrimarySenseIsUk(word))
                primaryP = true;
        }

        bool noCommonBonus = particleP || !conjTypesP || (!longP && intOnly);

        // Semi-final / non-final particle membership — seq-based, matching Ichiran.
        bool semiFinalP = SemiFinalParticleSeqs.Contains(word.WordId);
        bool nonFinalP = NonFinalParticleSeqs.Contains(word.WordId);

        // Ichiran calc-score (dict.lisp:794): `(score 1) (prop-score 0)`. Score starts
        // at 1, not 0 — gives every matched edge a baseline prop of 1 so length-coeff
        // contributes even when no bonus fires.
        int score = 1;

        // Primary bonus (§10.7 first block)
        if (primaryP)
        {
            if (longP) score += 10;
            else if (secondaryConjP && !kanjiP) score += 2;
            else if (commonP && conjTypesP) score += 5;
            else if (preferKana || nKanji == 0) score += 3;
            else score += 2;
        }

        // Particle bonus (§10.7 second block)
        if (particleP && (isSentenceFinal || !semiFinalP))
        {
            score += 2;
            if (commonP) score += 2 + len;
            if (isSentenceFinal && !nonFinalP)
            {
                if (primaryP) score += 5;
                else if (semiFinalP) score += 2;
            }
        }

        // Common bonus (§10.7 third block)
        int commonBonus = 0;
        if (commonP && !noCommonBonus)
        {
            int bonus;
            if (secondaryConjP && !useLength.HasValue)
                bonus = (kanjiP && primaryP) ? 4 : 2;
            else if (longP || copDaP || (rootP && (kanjiP || (primaryP && len > 2))))
            {
                if (common == 0) bonus = 10;
                else if (!primaryP) bonus = Math.Max(15 - common, 10);
                else bonus = Math.Max(20 - common, 10);
            }
            else if (kanjiP) bonus = 8;
            else if (primaryP) bonus = 4;
            else if (len > 2 || (common > 0 && common < 10)) bonus = 3;
            else bonus = 2;

            // Ichiran calc-score §10.7: `(member 10 conj-types)` — conj-type 10 is
            // imperative. The bonus−4 penalty applies to imperative forms, not volitional.
            if (bonus >= 10 && hasImperative) bonus -= 4;
            commonBonus = bonus;
            score += bonus;
        }

        // Min/max adjustments (§10.7 final block)
        if (longP) score = Math.Max(len, score);
        if (kanjiP)
        {
            score = Math.Max(isArch ? 3 : 5, score);
            if (longP && (nKanji > 1 || len > 4)) score += 2;
        }

        // §10.2 counter-text minimum — Ichiran's `ctr-mode` is set only when the
        // edge came from a counter-text lookup (digits + counter). It is NOT enabled
        // merely because the word has "ctr" POS. We don't currently track counter-text
        // mode separately, so this floor is intentionally omitted — firing it on any
        // ctr-POS word over-promotes ambiguous kana readings like の (2671670 has
        // n/n-suf/ctr) and lets them tie with the real particle.

        var result = new IchiranPropScore(score, new Kpcl(kanjiP, primaryP, commonP, longP), len, nKanji, common, katakanaP, commonBonus);
        if (cacheable)
            _propCache.TryAdd((word.WordId, surface, chainHash, isSentenceFinal, useLenKey), result);
        return result;
    }

    // -------- helpers --------

    // Port of Ichiran's mora-length (characters.lisp:245):
    //   (count-if-not (lambda (char) (find char "っッぁァぃィぅゥぇェぉォゃャゅュょョー")) str)
    // Exclusion set: sokuon (っ/ッ), all small kana, and chōonpu (ー).
    public static int CountMora(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c is 'っ' or 'ッ' or 'ー'
                or 'ゃ' or 'ゅ' or 'ょ' or 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ'
                or 'ャ' or 'ュ' or 'ョ' or 'ァ' or 'ィ' or 'ゥ' or 'ェ' or 'ォ')
                continue;
            count++;
        }
        return count;
    }

    private static int CountKanji(string text)
    {
        int n = 0;
        foreach (char c in text)
            if (c >= '\u4E00' && c <= '\u9FFF') n++;
        return n;
    }

    private static bool ContainsKatakana(string text)
    {
        foreach (char c in text)
            if (c >= 0x30A0 && c <= 0x30FF) return true;
        return false;
    }

    // True if every kanji in `surface` appears somewhere in `formText`. Used to detect
    // spurious conj-table hits where the surface's kanji is unrelated to the dict form.
    private static bool FormHasAnyKanjiOf(string formText, string? surface)
    {
        if (string.IsNullOrEmpty(surface)) return true;
        foreach (char c in surface)
        {
            if (c >= '\u4E00' && c <= '\u9FFF')
            {
                bool found = false;
                foreach (char fc in formText) if (fc == c) { found = true; break; }
                if (!found) return false;
            }
        }
        return true;
    }

    private static bool ContainsHiragana(string text)
    {
        foreach (char c in text)
            if (c >= 0x3040 && c <= 0x309F) return true;
        return false;
    }

    private static bool IsPureHiragana(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
            if (c < 0x3040 || c > 0x309F) return false;
        return true;
    }

    // Stem-transition / weak / skip form detection moved to ConjChainAnalysis +
    // ConjFormMatcher. HasConjTag remains for the scorer's per-site tag checks
    // (te-form, imperative, volitional, past polite) that shape bonuses rather
    // than gate the whole score.
    // Structural relatedness check for flag-only chain guard: does the surface share
    // a leading character with any form of the word? Conjugations preserve the stem,
    // so a legitimate conjugated surface always starts with the same kanji or same
    // initial kana as at least one dict form. When the surface shares nothing, the
    // deconjugator produced an unrelated (surface, word) mapping — suspect.
    private static bool SurfaceSharesLeadWithForm(string? surface, string? formText)
    {
        if (string.IsNullOrEmpty(surface)) return true;  // empty — defer to other checks
        if (!string.IsNullOrEmpty(formText) && surface[0] == formText[0]) return true;
        return false;
    }

    private static bool HasConjTag(IReadOnlyList<string>? chain, string fragment)
    {
        if (chain == null) return false;
        foreach (var t in chain)
            if (t != null && t.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // JMdict priority → common integer, faithful to Ichiran's dict-load.lisp:53-55:
    //   ANY priority tag flips :null → 0. Only `nf##` overrides with its numeric value.
    //   Our earlier mapping (ichi1→1, spec1→20, news2→15, etc.) made common words score
    //   too high because Ichiran's long-p threshold fires on `0 < common < 10` — a value
    //   of 0 fails that check, but our mapped `1` passed it and bumped the multiplier.
    private static int ComputeCommon(IReadOnlyList<string>? priorities)
    {
        if (priorities == null || priorities.Count == 0) return -1;
        int common = 0;
        foreach (var p in priorities)
        {
            if (p.StartsWith("nf") && p.Length > 2 && int.TryParse(p.AsSpan(2), out int nf))
                common = nf;
        }
        return common;
    }

    // Count of kanji FORMS on the entry (not kanji chars of matched text). Used by
    // primary-p branch 2 to distinguish genuinely kana-only entries from kana-form
    // matches of words that also have kanji forms.
    private static int EntryKanjiFormCount(JmDictWord word)
    {
        int n = 0;
        foreach (var f in word.Forms)
            if (f.FormType == JmDictFormType.KanjiForm && !f.IsNoKanji) n++;
        return n;
    }

    // True when every priority in `priorities` also appears on at least one kanji form
    // of `word`. Used to detect kana forms whose priority list is inherited pollution
    // from sibling kanji forms (we ingest ke_pri into per-form lists). If the kana form
    // introduces no new priority beyond what the kanji forms already carry, it's almost
    // certainly inheritance rather than a genuine re_pri.
    // Tighten the kanji-form inheritance heuristic: only treat kana priorities as
    // "inherited noise" when strictly fewer than the max kanji form's priorities.
    // Identical counts mean the kana form has its own explicit re_pri tags — data
    // that our ingest happens to copy verbatim but which is also what legitimate
    // JMdict entries look like (e.g. 後/あと both carry [ichi1,news1,nf01] for real).
    private static int MaxKanjiPriorityCount(JmDictWord word)
    {
        int max = 0;
        foreach (var f in word.Forms)
        {
            if (f.FormType != JmDictFormType.KanjiForm || f.IsNoKanji) continue;
            int c = f.Priorities?.Count ?? 0;
            if (c > max) max = c;
        }
        return max;
    }

    private static bool AllFromKanjiForms(JmDictWord word, IReadOnlyList<string> priorities)
    {
        var kanjiPriorities = new HashSet<string>(StringComparer.Ordinal);
        bool anyKanji = false;
        foreach (var f in word.Forms)
        {
            if (f.FormType != JmDictFormType.KanjiForm || f.IsNoKanji) continue;
            anyKanji = true;
            if (f.Priorities != null)
                foreach (var p in f.Priorities) kanjiPriorities.Add(p);
        }
        if (!anyKanji) return false;
        foreach (var p in priorities)
            if (!kanjiPriorities.Contains(p)) return false;
        return true;
    }

    // Is any SenseIndex=0 definition marked "uk"? Used by primary-p branch 3 to gate on
    // "is the primary sense the uk sense". When false, branch 3 is allowed to fire — the
    // entry has uk senses but the primary sense is not one of them, so the kanji form is
    // genuinely primary.
    //
    // Cache layer / ingest can leave per-sense Misc unloaded for some entries. The previous
    // implementation returned false unconditionally there, which mis-fired branch 3 →
    // primaryP=true on multi-word [exp uk] kanji-form matches (e.g. 事になる). Concrete:
    // 事になる (uk, no per-sense misc loaded) was scoring with primaryP=true → prop 23 →
    // 事になります baseScore 1656, halved by kanji-break to 828, beating 大事|に|なります
    // (629) by ~190. Fallback: when no sense-0 row carries Misc data, default to using
    // the entry-wide POS list — `uk` there means at least one sense is uk, and for
    // preferKana entries it's nearly always the primary one.
    private static bool PrimarySenseIsUk(JmDictWord word)
    {
        // Narrow the missing-defs fallback to **multi-word [exp uk]** entries only.
        // Single-word [uk] entries (e.g. 貰う, 居る) keep the old false-on-empty behavior
        // because their kanji forms ARE legitimately primary in suffix-synth contexts
        // (信じて貰える, 落ちている), and assuming primary-sense-is-uk for them flips
        // primaryP to false → reduces compound scores → wrong splits.
        bool isMultiWordExpUk = word.PartsOfSpeech.Contains("exp") && word.PartsOfSpeech.Contains("uk");
        if (word.Definitions == null || word.Definitions.Count == 0)
            return isMultiWordExpUk;
        bool sawSense0Misc = false;
        foreach (var d in word.Definitions)
        {
            if (d.SenseIndex != 0) continue;
            if (d.Misc == null) continue;
            sawSense0Misc = true;
            foreach (var m in d.Misc)
                if (m == "uk") return true;
        }
        if (!sawSense0Misc) return isMultiWordExpUk;
        return false;
    }

    // Ichiran's entry.primary-nokanji (dict-load.lisp:60) is initialised TRUE when ANY
    // kana reading has `<re_nokanji/>`. Then `remove-hiragana-nokanji`
    // (dict-errata.lisp:217) unsets it for entries whose nokanji readings are hiragana —
    // so the default rule narrows to "katakana-nokanji reading". Finally explicit errata
    // (set-primary-nokanji / add-primary-nokanji) override specific seqs.
    private static readonly HashSet<int> PrimaryNokanjiOverrideTrue = new()
    {
        1415510, // タカ
        2722640, // オケ
        2217330, // ワイ
        1346290, // (set-primary-nokanji t)
    };

    private static readonly HashSet<int> PrimaryNokanjiOverrideFalse = new()
    {
        1538900, 1580640, 1289030, 1374550, 1591900, 1000230, 1517810, 1585410,
        1258330, 1588930, 1565440, 1631830, 1409110, 2081610, 1495000, 1756600,
        1502390,
    };

    private static bool IsPrimaryNokanjiEntry(JmDictWord word)
    {
        if (PrimaryNokanjiOverrideTrue.Contains(word.WordId)) return true;
        if (PrimaryNokanjiOverrideFalse.Contains(word.WordId)) return false;
        foreach (var f in word.Forms)
        {
            if (f.FormType != JmDictFormType.KanaForm) continue;
            if (!f.IsNoKanji) continue;
            if (IsAllKatakanaText(f.Text)) return true;
        }
        return false;
    }

    private static bool IsAllKatakanaText(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
            if (c < 0x30A0 || c > 0x30FF) return false;
        return true;
    }

    // Length multiplier tables (§10.8 of ichiran-analysis.md).
    // :strong (kanji/katakana): 1, 8, 24, 40, 60, then linear ×15
    // :weak   (hiragana):       1, 4, 9, 16, 25, 36, then linear ×7
    // :tail   (compound suffix, default):  4,  9, 16, 24, then linear ×8
    // :ltail  (compound suffix, strong/long): 4, 12, 18, 24, then linear ×8
    // Index 0 is unused (maps to "no length"); table[n] is the coeff for mora length n.
    private static readonly int[] StrongCoeff = { 0, 1, 8, 24, 40, 60 };
    private static readonly int[] WeakCoeff   = { 0, 1, 4, 9, 16, 25, 36 };
    private static readonly int[] TailCoeff   = { 0, 4, 9, 16, 24 };
    private static readonly int[] LtailCoeff  = { 0, 4, 12, 18, 24 };

    public enum LengthClass { Strong, Weak, Tail, Ltail }

    public static int LengthMultiplierCoeff(int n, LengthClass cls)
    {
        if (n <= 0) return 0;
        int[] table = cls switch
        {
            LengthClass.Strong => StrongCoeff,
            LengthClass.Weak   => WeakCoeff,
            LengthClass.Tail   => TailCoeff,
            LengthClass.Ltail  => LtailCoeff,
            _ => WeakCoeff
        };
        if (n < table.Length) return table[n];
        int lastIdx = table.Length - 1;
        int step = table[lastIdx] / lastIdx;
        return n * step;
    }

    // n-kanji > 1 gets an additional per-extra-kanji multiplier bump (§10.8).
    public static int ApplyNKanjiBonus(int coeff, int nKanji)
        => coeff + (nKanji > 1 ? (nKanji - 1) * 5 : 0);

    public static LengthClass ClassFor(bool kanjiP, bool katakanaP)
        => (kanjiP || katakanaP) ? LengthClass.Strong : LengthClass.Weak;
}
