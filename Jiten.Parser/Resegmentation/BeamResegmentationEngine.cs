using System.Collections.Concurrent;
using System.Diagnostics;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Data.Redis;
using Jiten.Parser.Diagnostics;
using Jiten.Parser.Grammar;
using Jiten.Parser.Scoring;

namespace Jiten.Parser.Resegmentation;

// Sentence-scope DP beam resegmentation (Lesson #3).
//
// Pipeline:
//   1. Enumerate every substring (start, len<=MaxEdgeLength) of the sentence,
//      ask ICandidateProvider for candidates, collect unique WordIds.
//   2. One batch JmDictCache fetch for the whole sentence.
//   3. Build a single representative FormCandidate per (start, len, wordId).
//      Node score = FormCandidateScorer.TotalScore.
//   4. DP beam over end-position / segment-lists. Each state carries the
//      last scored segment; adjacency is evaluated pairwise when the next
//      segment is placed. Gap edges (length 1, no wordIds) cost
//      UncoveredCharPenalty per char.
//   5. If best beam score is within SudachiFallbackThreshold of the
//      Sudachi-aligned path's score, prefer Sudachi (safety net during rollout).
//   6. Replace sentence.Words with the winning path's WordInfos. Reading
//      index and full form resolution happen downstream in
//      ProcessWordsInBatches — per the Plan B decision, the beam picks
//      segmentation only; reading resolution is deferred.
internal static class BeamResegmentationEngine
{
    private const int MaxEdgeLength            = 16;
    private const int BeamWidth                = 16;
    private const int MaxGapChars              = 1;
    private const int MaxDpGapChars            = 5;
    private const int MaxSynergyBound          = 200;
    // Top-K per segment-list in segment-DP. Ichiran's `find-best-path` uses
    // `:limit 5` (dict.lisp:1190). Reduced to 3 — zero regressions on parser
    // test suite (tested at 2 and 3), ~40% DP work reduction vs 5.
    private static readonly int SegmentTopK =
        int.TryParse(Environment.GetEnvironmentVariable("JITEN_BEAM_SEGMENT_TOPK"), out var topK)
            ? Math.Max(1, topK)
            : 3;
    // The beam always runs in Ichiran scoring mode. The additive rollout path has
    // been retired; threshold/hint env vars remain only as debug controls around
    // the now-default beam behaviour.
    private const bool UseIchiranScoring = true;
    // Pure-Ichiran mode: remove every Sudachi-path influence on the beam's segmentation
    // decision. The beam still runs AFTER Sudachi + RunPipeline produced sentence.Words
    // (that's where we get the sentence text and the fallback WordInfos for boundaries
    // that happen to agree), but the beam's lattice, synthesis and writeback short-
    // circuits are all cut off from Sudachi's opinions. Implicit whenever Ichiran scoring
    // is on — the whole point of Ichiran mode is to measure what the beam produces alone,
    // uncontaminated by CombineVerbDependantsSuru / CombineCompounds / SpecialCases
    // merges that landed in sentence.Words upstream. Set JITEN_BEAM_PURE_ICHIRAN=0 to
    // opt back into the hybrid (Sudachi-seeded) behaviour for debugging comparisons.
    private static readonly bool PureIchiranInternal =
        Environment.GetEnvironmentVariable("JITEN_BEAM_PURE_ICHIRAN") is { } s
            ? s == "1"
            : UseIchiranScoring;
    private static bool PureIchiran => PureIchiranInternal;

    // Exposed so the outer parser pipeline can detect pure-Ichiran mode and skip
    // Jiten-specific pre-beam rewrites (PreprocessSentences, first resegmentation pass,
    // etc.). In pure mode the beam is the segmentation authority and must see raw
    // Sudachi output — not a Jiten-rewritten lattice.
    public static bool IsPureIchiranMode => PureIchiranInternal;
    // Segment-list DP (Ichiran-style), default under Ichiran scoring. Phase 4
    // builds paths by chaining non-overlapping segment-lists rather than
    // advancing char-by-char. Gap spans between chosen segments are inferred
    // and priced at -500/char (matching *gap-penalty*), with no hard cap —
    // matches Ichiran's `find-best-path` (dict.lisp:1190) exactly. Opt back
    // to position DP via JITEN_BEAM_SEGMENT_DP=0 for debugging.
    private static readonly bool UseSegmentDP =
        Environment.GetEnvironmentVariable("JITEN_BEAM_SEGMENT_DP") is { } sdp
            ? sdp == "1"
            : UseIchiranScoring;
    private static readonly bool ProfileBeam =
        Environment.GetEnvironmentVariable("JITEN_BEAM_PROFILE") == "1";
    // Phase B empirical calibration: threshold 1500 holds parity under Ichiran scoring
    // (0 regressions vs default scoring on parser-test suite). Lowering it to 1000 gave 4
    // regressions on over-aggressive compound formation (なお winning over な+お, etc.).
    // Phase C (use-length bonus) and Phase F (score-cutoff culling) should enable
    // lowering this further without regressions.
    private static readonly int SudachiFallbackThreshold =
        int.TryParse(Environment.GetEnvironmentVariable("JITEN_BEAM_THRESHOLD"), out var t) ? t :
        (UseIchiranScoring ? 1500 : 100);
    // Sudachi hint bonus: when > 0, edges matching Sudachi token boundaries get
    // a positive bias. Set to 0 via JITEN_BEAM_NOHINT=1 for hint-free operation
    // (beam decides purely on length + freq + adjacency + rules). Ichiran doesn't
    // use Sudachi hints at all. When Ichiran scoring is enabled, hints are
    // auto-disabled (they were compensating for weak additive scoring; in multiplicative
    // scoring they can't meaningfully compete and would just add noise).
    private static readonly bool NoHints =
        Environment.GetEnvironmentVariable("JITEN_BEAM_NOHINT") == "1" || UseIchiranScoring || PureIchiran;
    private static readonly int SudachiHintBonus    = NoHints ? 0 : 30;
    private static readonly int RawSudachiHintBonus = NoHints ? 0 : 10;
    // Single-character kana edges are extremely ambiguous (every hiragana is some
    // particle or common noun); without a penalty the beam happily chains them.
    // Full penalty for non-functional POS (nouns, etc. matched as 1-char kana —
    // usually spurious). Reduced (but non-zero) penalty for functional POS so
    // grammatically mandatory copulas/particles (な/だ/を/に/の/と/か/よ/ね) can
    // win when they're the right segmentation, but don't freely chain to out-
    // fragment longer compound edges.
    private const int SingleCharKanaPenalty    = 40;
    private const int SingleCharFunctionalKanaPenalty = 15;

    // Ichiran's *score-cutoff* (dict.lisp:985). Nodes scoring below this are dropped
    // before they enter the lattice. The cutoff keeps noise 1-char matches and
    // low-prop katakana fragments out of the beam in Ichiran mode.
    private const int IchiranScoreCutoff = 5;

    private static readonly BeamTransitionInterner _sharedTransitionInterner = new();
    private static readonly BonusCacheTable _sharedBonusCache = new();

    // Ichiran *skip-words* (dict-errata.lisp:1155). Seqs Ichiran considers "not really
    // words, like suffixes etc." — calc-score returns 0 for these, removing them from
    // the lattice entirely. Common polite/contracted suffix-ish JMDict entries that
    // should only ever be attached, never standalone.
    private static readonly HashSet<int> SkipWords = new()
    {
        2822120, // ても良い
        2013800, // ちゃう
        2108590, // とく
        2029040, // ば
        2428180, // い
        2654250, // た
        2561100, // うまいな
        2210270, // ませんか
        2210710, // ましょうか
        2257550, // ない
        2210320, // ません
        2017560, // たい
        2394890, // とる
        2194000, // であ
        2568000, // れる/られる
        2537250, // しようとする
        2760890, // 三箱
        2831062, // てる
        2831063, // てく
        2029030, // ものの
        2568020, // せる
        900000,  // たそう
        2827357, // まう
    };

    // Ichiran *final-prt* (dict-errata.lisp:1182). Particles that ONLY have meaning at
    // sentence-final position. calc-score returns 0 when these appear mid-sentence —
    // much stronger than the -15 penalty-semi-final applied to the broader
    // *semi-final-prt* set (final-prt + さ/し/な/ね/わ).
    private static readonly HashSet<int> FinalPrtSeqs = new()
    {
        2017770, // かい
        2425930, // なの
        2130430, // け / っけ
        2029130, // ぞ
        2834812, // ぜ
        2718360, // がな
        2201380, // わい
        2722170, // のう
        2751630, // かいな
    };

    // Ichiran *no-kanji-break-penalty* (dict-errata.lisp:1214). WordIds whose seq-set
    // intersection short-circuits the kanji-break penalty — common verbs / nouns whose
    // boundary regularly sits at a kanji-kanji position in context without being a
    // "bad split" (飲む, 会議, 好き, …).
    private static readonly HashSet<int> NoKanjiBreakPenaltyWordIds = new()
    {
        1169870, // 飲む
        1198360, // 会議
        1277450, // 好き
        2028980, // で
        1423000, // 着る
        1164690, // 一段
        1587040, // 言う
        2827864, // なので
        2144600, // ソビエト連邦 (mixed-script compound; its kanji tail ends on a kanji-kanji break)
    };

    // Ichiran *force-kanji-break* (dict-errata.lisp:1226). For each occurrence of these
    // surfaces in the sentence, every internal position contributes a kanji-break — even
    // though no kanji-kanji pair exists. です is the load-bearing entry: penalises edges
    // whose boundary cuts between で and す, discouraging spurious merges like X|です
    // becoming Xです when で and す belong to different lemmas.
    private static readonly HashSet<string> ForceKanjiBreakSurfaces = new() { "です" };

    // Ichiran *no-kanji-break* (dict-errata.lisp:1229). Surfaces whose kanji-kanji pairs
    // are suppressed — 日置 was problematic with 一日置く where the 日 | 置 boundary was
    // getting a false penalty despite the split being correct.
    private static readonly HashSet<string> NoKanjiBreakSurfaces = new() { "日置" };

    public static async Task<bool> ResegmentSentences(
        List<SentenceInfo> sentences,
        ICandidateProvider candidateProvider,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        IJmDictCache jmDictCache,
        Func<WordInfo, int?>? resolvedWordIdLookup = null,
        ParserDiagnostics? diagnostics = null)
    {
        bool anyApplied = false;
        foreach (var sentence in sentences)
        {
            var hints = BuildSudachiHints(sentence);
            if (await ResegmentSentence(sentence, candidateProvider, lookups, frequencyRanks, jmDictCache, hints, resolvedWordIdLookup, diagnostics))
                anyApplied = true;
        }
        return anyApplied;
    }

    private static Dictionary<(int Start, int Len), int> BuildSudachiHints(SentenceInfo sentence)
    {
        // Stage A (asymmetric variant): hint BOTH raw Sudachi boundaries (pre-combine) AND
        // post-combine boundaries, but at DIFFERENT weights. Post-combine boundaries reflect
        // Jiten's opinionated compound merges and get the full bonus; raw boundaries only
        // make split paths reachable without stacking hint rewards per fragment. Without
        // this asymmetry, a path of N 1-char edges each hint-aligned would accumulate N×30
        // hint bonus and beat a single longer edge worth only 30. Post-combine wins on ties.
        //
        // In pure-Ichiran mode the map stays empty — no Sudachi-derived positional priors
        // influence the beam at all.
        var map = new Dictionary<(int, int), int>();
        if (PureIchiran) return map;
        foreach (var (pos, len) in sentence.RawSudachiBoundaries)
            map[(pos, len)] = RawSudachiHintBonus;
        foreach (var (_, pos, len) in sentence.Words)
            map[(pos, len)] = SudachiHintBonus; // overrides raw where they overlap
        return map;
    }

    // Functional POS tags whose 1-char kana forms are grammatically mandatory
    // (particles, copulas, auxiliaries, conjunctions). Suppresses SingleCharKanaPenalty
    // so な/だ/を/に/が/で/と/は/も/の/か/よ/ね etc. don't lose to 2-char nonsense
    // neighbours in the beam. Ichiran-compatible: these POS classes are scored normally
    // there too; the penalty is a Jiten-specific garbage-suppressor for non-functional
    // single-char kana.
    private static bool IsFunctionalKanaPos(JmDictWord word)
    {
        foreach (var p in word.PartsOfSpeech)
        {
            if (p is "prt" or "cop" or "cop-da" or "aux" or "aux-v" or "aux-adj" or "conj" or "int")
                return true;
        }
        return false;
    }

    private static async Task<bool> ResegmentSentence(
        SentenceInfo sentence,
        ICandidateProvider candidateProvider,
        Dictionary<string, List<int>> lookups,
        Dictionary<int, int> frequencyRanks,
        IJmDictCache jmDictCache,
        IReadOnlyDictionary<(int Start, int Len), int> sudachiHints,
        Func<WordInfo, int?>? resolvedWordIdLookup,
        ParserDiagnostics? diagnostics)
    {
        var text = sentence.Text;
        if (string.IsNullOrEmpty(text) || sentence.Words.Count == 0) return false;
        long totalStart = 0;
        long phaseStart = 0;
        double enumMs = 0, splitMs = 0, fetchMs = 0, injectMs = 0, nodeMs = 0, dpMs = 0, baselineMs = 0, writebackMs = 0;
        var beamProfile = ProfileBeam ? new BeamProfileStats() : null;
        if (ProfileBeam)
        {
            totalStart = Stopwatch.GetTimestamp();
            phaseStart = totalStart;
        }

        // Sentence-local surface cache: the lattice pass, split/suffix
        // fallbacks, and virtual-stem synthesis all hit GetCandidates for
        // overlapping substrings. Wrapping the provider once per sentence
        // collapses the repeat cost to a plain Dictionary probe.
        var sentenceCache = new SentenceSurfaceCache(text, candidateProvider);
        candidateProvider = sentenceCache;

        // Phase 1: enumerate edges, collect wordIds.
        //
        // Sticky positions (port of Ichiran's `find-sticky-positions`, dict.lisp:990):
        // Ichiran forbids substring boundaries after a sokuon (っ/ッ) and at modifier
        // positions (small kana ぁぃぅぇぉゃゅょゎ + long-vowel ー). Enforcing this during
        // enumeration prevents obviously-invalid segments like splitting "がっこう"
        // into "がっ | こう" or "きゃく" into "き | ゃく". Applied ONLY to the generic
        // substring→JMDict enumeration; compound-edge injections below (split seeds,
        // sudachi seeds, suffix compounds) bypass sticky gating because they represent
        // lexical atoms that may legitimately span a sticky position.
        var stickyPositions = FindStickyPositions(text);
        var edgesByStart = new List<(int Length, IReadOnlyList<SurfaceCandidate> Cands)>[text.Length];
        var allWordIds = new HashSet<int>();
        // Precomputed per-char max dict/table prefix length. If no entry in
        // either _lookups or the ConjugationTable begins with text[start],
        // maxLenByFirstChar[text[start]] == 0 and the inner length loop skips
        // entirely — saves thousands of Substring/GetCandidates calls per
        // long text (Phase 1 is the dominant hot path).
        var maxLenByFirstChar = GetMaxLenByFirstChar(lookups);
        for (int start = 0; start < text.Length; start++)
        {
            edgesByStart[start] = new List<(int, IReadOnlyList<SurfaceCandidate>)>();
            if (stickyPositions.Contains(start)) continue;
            int prefixCap = maxLenByFirstChar[text[start]];
            if (prefixCap == 0) continue;
            int maxLen = Math.Min(MaxEdgeLength, Math.Min(text.Length - start, prefixCap));
            for (int len = 1; len <= maxLen; len++)
            {
                if (stickyPositions.Contains(start + len)) continue;
                var cands = sentenceCache.GetCandidates(start, len);
                if (cands.Count == 0) continue;
                edgesByStart[start].Add((len, cands));
                foreach (var c in cands) allWordIds.Add(c.WordId);
            }
        }
        if (ProfileBeam)
        {
            enumMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // §10.11 kanji-break positions, derived from our OWN lattice edges — not
        // Sudachi tokens. Ichiran computes `sequential-kanji-positions` per successfully-
        // matched substring in `join-substring-words*` (dict.lisp:1106); we replicate that
        // by treating every (start, start+len) pair with a non-empty candidate list as a
        // "part". A position `p` is a kanji-break when text[p-1] and text[p] are both
        // kanji AND some edge covers both characters (so the break is INTERNAL to a
        // match, not at an edge boundary). Force/no-kanji-break surface overrides run
        // after. This closes the last Sudachi-contamination channel into the beam.
        var partSpans = new List<(int Start, int Len)>();
        for (int s = 0; s < text.Length; s++)
            foreach (var (l, _) in edgesByStart[s])
                partSpans.Add((s, l));
        var kanjiBreaks = ComputeKanjiBreakPositions(text, partSpans);

        // Also seed Sudachi token edges as first-class lattice entries. Compound idioms
        // resolved by CombineCompounds/SpecialCases (e.g. 「あけましておめでとうございます」,
        // 「臆病風に吹かれていた」) are NOT substring-decomposable into JMDict lookups — their
        // WordId is a synthetic compound. Without this step, the beam's lattice has no edge
        // covering the whole compound and the baseline has no path to win against beam
        // re-segmentation. Adding the Sudachi edge gives the beam a chance to keep the
        // existing compound when scoring says to. ResolvedWordId isn't populated until
        // ApplyAdjacentScoring runs AFTER the beam, so we rely on the caller's resolver.
        //
        // In pure-Ichiran mode the seeds are NOT injected: the beam's segmentation must come
        // from the candidate provider + JMDict lookup + grammar rules alone. Seeds would
        // smuggle CombineVerbDependantsSuru / CombineCompounds opinions into the lattice
        // and bias the beam away from its own decisions.
        var sudachiSeeds = new List<(int Start, int Len, int WordId)>();
        var sudachiSeedSet = new HashSet<(int, int, int)>();
        if (!PureIchiran)
        {
            for (int wi = 0; wi < sentence.Words.Count; wi++)
            {
                var (w, pos, len) = sentence.Words[wi];
                int id = w.PreMatchedWordId
                         ?? resolvedWordIdLookup?.Invoke(w)
                         ?? w.ResolvedWordId
                         ?? 0;
                if (id == 0) continue;
                allWordIds.Add(id);
                if (pos >= 0 && pos < text.Length && pos + len <= text.Length)
                {
                    sudachiSeeds.Add((pos, len, id));
                    sudachiSeedSet.Add((pos, len, id));
                }
            }
        }

        // Phase 1c: plan split-rule expansions. For each edge whose wordId has a split
        // rule (ported from Ichiran's dict-split.lisp), resolve the piece texts to
        // wordIds via _lookups and stage them. Piece wordIds are added to allWordIds so
        // Phase 2's batch fetch loads their JmDictWords too; injection into the lattice
        // happens in Phase 2c after the fetch.
        var splitSeeds = new List<(int Start, int Len, int WordId, int Bonus, bool IsFirstPiece)>();
        // §10.10 non-FirstPieceBonus modes: record compound edge → rule so Phase 3's
        // post-pass can revise the COMPOUND's nodeScore per Ichiran's split dispatch.
        // Under Score / PScore / Replace the rule acts on the compound side (keeping
        // it in the lattice while raising or rescaling its score), not on the first
        // piece. Piece edges are still injected — they remain alternative paths.
        // Piece ranges are stored as (start, len) — we pick the best-scoring node at
        // that range in the post-pass, not a specific wordId. The candidate provider
        // and the split loader may disagree on which wordId is the canonical "お" at a
        // given position (both are valid JMDict entries for the surface), so locking to
        // `lookups[text][0]` loses the piece when the lattice chose a different entry.
        var compoundSplitRules =
            new Dictionary<(int Start, int Len, int WordId),
                (int ScoreMod, Jiten.Parser.Resolution.SplitMode Mode, (int Start, int Len, int? WordId)[] PieceRanges)>();
        for (int start0 = 0; start0 < text.Length; start0++)
        {
            var list0 = edgesByStart[start0];
            int n0 = list0.Count;
            for (int ei = 0; ei < n0; ei++)
            {
                var (elen, ecands) = list0[ei];
                if (start0 + elen > text.Length) continue;
                var surface0 = sentenceCache.GetSurface(start0, elen);
                foreach (var ec in ecands)
                {
                    if (!Jiten.Parser.Resolution.Splits.TryGet(ec.WordId, out var rule)) continue;

                    // Two matching modes:
                    //   (a) Exact surface match — original behaviour. Every piece's
                    //       surface equals its rule.PieceTexts[i].
                    //   (b) Conjugated-tail match — surface0 starts with the rule's
                    //       non-final-piece prefix but is longer. The last piece
                    //       absorbs the conjugated remainder (e.g. rule.Text=なくなる
                    //       fires on surface=なくなった with last piece = なった). Ichiran's
                    //       def-simple-split fires on seq identity, so its rules match
                    //       any conjugation of the compound — porting that here. Only
                    //       multi-piece rules are eligible; single-piece rules are
                    //       wordId rewrites, not conjugation-aware splits.
                    bool exactMatch = rule.Text == surface0;
                    bool conjMatch = false;
                    int nonFinalLen = 0;
                    if (!exactMatch && rule.PieceTexts.Length >= 2)
                    {
                        for (int i = 0; i < rule.PieceTexts.Length - 1; i++)
                            nonFinalLen += rule.PieceTexts[i].Length;
                        if (elen > nonFinalLen
                            && rule.Text.Length >= nonFinalLen
                            && surface0.Length > nonFinalLen
                            && string.CompareOrdinal(rule.Text, 0, surface0, 0, nonFinalLen) == 0)
                        {
                            conjMatch = true;
                        }
                    }
                    if (!exactMatch && !conjMatch) continue;

                    int off = 0;
                    bool ok = true;
                    var staged = new List<(int PS, int PL, int PW, int? RuleWid)>(rule.PieceTexts.Length);
                    int pieceCount = rule.PieceTexts.Length;
                    for (int pi = 0; pi < pieceCount; pi++)
                    {
                        var pt = rule.PieceTexts[pi];
                        int pieceLen;
                        int pieceWid;
                        int? ruleWid = null;
                        if (pi == pieceCount - 1 && conjMatch)
                        {
                            // Last piece absorbs the conjugated tail. Use the candidate
                            // provider's deconjugating lookup so the tail's base-form
                            // wordId is found (plain `lookups` only stores base surfaces).
                            pieceLen = elen - off;
                            var tailSurface = sentenceCache.GetSurface(start0 + off, pieceLen);
                            var tailCands = candidateProvider.GetCandidates(tailSurface);
                            if (tailCands.Count == 0) { ok = false; break; }
                            pieceWid = tailCands[0].WordId;
                        }
                        else
                        {
                            pieceLen = pt.Length;
                            if (rule.PieceWordIds != null && pi < rule.PieceWordIds.Length
                                && rule.PieceWordIds[pi].HasValue)
                            {
                                pieceWid = rule.PieceWordIds[pi]!.Value;
                                ruleWid = pieceWid;
                            }
                            else if (lookups.TryGetValue(pt, out var pwids) && pwids.Count > 0)
                            {
                                pieceWid = pwids[0];
                            }
                            else
                            {
                                // Piece text is a conjugated form not present in Lookups
                                // (e.g. わからない in 2757500 わけのわからない, where only the base
                                // 分かる is a lookup key). Resolve via the candidate provider
                                // so the conjugation table maps it back to the base wordId.
                                // Mirrors Ichiran's (seq nil t) final-piece flag.
                                var pcands = candidateProvider.GetCandidates(pt);
                                if (pcands.Count == 0) { ok = false; break; }
                                pieceWid = pcands[0].WordId;
                            }
                        }
                        staged.Add((start0 + off, pieceLen, pieceWid, ruleWid));
                        off += pieceLen;
                    }
                    if (!ok) continue;
                    var pieceRanges = new (int Start, int Len, int? WordId)[staged.Count];
                    for (int i = 0; i < staged.Count; i++)
                    {
                        var s = staged[i];
                        allWordIds.Add(s.PW);
                        pieceRanges[i] = (s.PS, s.PL, s.RuleWid);
                        // Legacy FirstPieceBonus mode: score-mod goes to the first piece so
                        // the split path gets a head-start bonus. Other modes operate on the
                        // compound edge instead (applied in the Phase 3 post-pass below).
                        int bonus = (rule.Mode == Jiten.Parser.Resolution.SplitMode.FirstPieceBonus && i == 0)
                            ? rule.Score : 0;
                        splitSeeds.Add((s.PS, s.PL, s.PW, bonus, i == 0));
                    }
                    // Apply the split-rule compound transformation on BOTH exact-surface
                    // and conjugated-tail matches — Ichiran's dict.lisp:939-974 applies
                    // get-split dispatch unconditionally inside calc-score regardless of
                    // whether the reading is a plain surface or a conjugated proxy-text.
                    if (rule.Mode != Jiten.Parser.Resolution.SplitMode.FirstPieceBonus)
                    {
                        compoundSplitRules[(start0, elen, ec.WordId)] = (rule.Score, rule.Mode, pieceRanges);
                    }
                }
            }
        }

        // Phase 1c-seq: seq-identity split scan. The edge-driven loop above only
        // fires a split rule when the candidate provider has returned an edge at
        // (start, len) with exactly the rule's wordId. That misses rules where
        // the compound's base wordId isn't among the deconjugated candidates for
        // the conjugated surface — e.g. a rule keyed by 2679080 (なくなる) won't
        // fire when `なくなった` only yields candidates for なる (1375610) via the
        // deconjugator. Ichiran's get-split uses seq identity (the rule's seq OR
        // any ancestor in conj-of), which in practice means "scan the sentence
        // for rule.Text or its non-final-piece prefix at any position". We do
        // that here as an independent scan. Matches are deduplicated against
        // (start, len, wordId) so the edge-driven loop's work isn't duplicated.
        var synthCompoundSeeds = new List<(int Start, int Len, int WordId)>();
        // Lazy Phase 1c-seq pre-filter. Two-stage narrowing:
        //   (1) build positionsByChar: char → list of sentence positions where
        //       that char appears. Single O(textLen) pass.
        //   (2) per-char bucket of rules: skip entirely if the bucket's char
        //       isn't in text. For rules in a present bucket, scan only the
        //       positions where text[pos] == firstChar (not every position).
        //       This inverts the old O(rules × textLen) scan into
        //       O(rules × positions_for_its_firstChar), which for rare chars
        //       is a large cut without changing any decisions.
        var positionsByChar = new Dictionary<char, List<int>>();
        var textChars = new HashSet<char>();
        for (int ti = 0; ti < text.Length; ti++)
        {
            char tc = text[ti];
            textChars.Add(tc);
            if (!positionsByChar.TryGetValue(tc, out var plist))
                positionsByChar[tc] = plist = new List<int>();
            plist.Add(ti);
        }
        var rulesByFirstChar = Jiten.Parser.Resolution.Splits.RulesByFirstChar;
        foreach (var (firstChar, bucket) in rulesByFirstChar)
        {
            if (!positionsByChar.TryGetValue(firstChar, out var firstCharPositions)) continue;
        foreach (var (ruleWordId, rule) in bucket)
        {
            int pieceCount = rule.PieceTexts.Length;
            // Whole-prefix char presence filter — skip rules whose text
            // contains a char not in the sentence. Cheap per-rule check that
            // eliminates the vast majority of non-matching rules before the
            // position scan. For remaining rules the positional check proves
            // the exact match.
            bool prefixOk = true;
            for (int ci = 0; ci < rule.Text.Length; ci++)
            {
                if (!textChars.Contains(rule.Text[ci])) { prefixOk = false; break; }
            }
            if (!prefixOk) continue;
            // Single-piece rewrite rules: rule's only piece has an explicit wordId
            // that differs from the rule's key. Ichiran's def-simple-split allows
            // a single-piece rewrite form ((text seq)) — the surface is tagged with
            // the target seq instead of the rule's own seq. We scan the sentence
            // for the surface and inject an edge with the rewrite target wordId.
            if (pieceCount == 1)
            {
                if (rule.PieceWordIds == null || rule.PieceWordIds.Length != 1
                    || !rule.PieceWordIds[0].HasValue) continue;
                int targetWid = rule.PieceWordIds[0]!.Value;
                int surfLen = rule.Text.Length;
                foreach (int start0 in firstCharPositions)
                {
                    if (start0 + surfLen > text.Length) break;
                    if (string.CompareOrdinal(rule.Text, 0, text, start0, surfLen) != 0) continue;
                    allWordIds.Add(targetWid);
                    synthCompoundSeeds.Add((start0, surfLen, targetWid));
                }
                continue;
            }
            if (pieceCount < 2) continue;
            int nonFinalLen = 0;
            for (int i = 0; i < pieceCount - 1; i++) nonFinalLen += rule.PieceTexts[i].Length;

            int ruleLen = rule.Text.Length;
            int scanMax = text.Length - nonFinalLen;
            foreach (int start0 in firstCharPositions)
            {
                if (start0 >= scanMax) break;
                if (string.CompareOrdinal(rule.Text, 0, text, start0, nonFinalLen) != 0) continue;

                // Exact-match only for seq-identity scan. The edge-driven Phase 1c
                // loop already handles conjugated tails when the compound's base
                // wordId is in the lattice via the candidate provider. Extending the
                // scan to synthesise conjugated compounds at arbitrary positions
                // over-fires catastrophically (every mid-sentence occurrence of a
                // non-final prefix becomes a candidate compound of unbounded length,
                // polluting the lattice).
                bool exactMatch = start0 + ruleLen <= text.Length
                    && string.CompareOrdinal(rule.Text, 0, text, start0, ruleLen) == 0;
                if (!exactMatch) continue;

                int compoundLen = ruleLen;
                int tailWid;
                var lastPiece = rule.PieceTexts[pieceCount - 1];
                if (rule.PieceWordIds != null && rule.PieceWordIds.Length == pieceCount
                    && rule.PieceWordIds[pieceCount - 1].HasValue)
                    tailWid = rule.PieceWordIds[pieceCount - 1]!.Value;
                else if (lookups.TryGetValue(lastPiece, out var lastWids) && lastWids.Count > 0)
                    tailWid = lastWids[0];
                else continue;

                if (compoundLen > MaxEdgeLength) continue;
                if (compoundSplitRules.ContainsKey((start0, compoundLen, ruleWordId))) continue;

                // Stage piece seeds (mirrors the edge-driven loop).
                int off = 0;
                var pieceRanges = new (int Start, int Len, int? WordId)[pieceCount];
                bool ok = true;
                for (int pi = 0; pi < pieceCount; pi++)
                {
                    int pieceLen;
                    int pieceWid;
                    int? ruleWid = null;
                    if (pi == pieceCount - 1)
                    {
                        pieceLen = compoundLen - off;
                        pieceWid = tailWid;
                        if (rule.PieceWordIds != null && rule.PieceWordIds.Length == pieceCount
                            && rule.PieceWordIds[pi].HasValue)
                            ruleWid = tailWid;
                    }
                    else
                    {
                        pieceLen = rule.PieceTexts[pi].Length;
                        if (rule.PieceWordIds != null && pi < rule.PieceWordIds.Length
                            && rule.PieceWordIds[pi].HasValue)
                        {
                            pieceWid = rule.PieceWordIds[pi]!.Value;
                            ruleWid = pieceWid;
                        }
                        else if (lookups.TryGetValue(rule.PieceTexts[pi], out var pwids) && pwids.Count > 0)
                        {
                            pieceWid = pwids[0];
                        }
                        else
                        {
                            // Conjugated-form piece fallback — see Phase 1c for rationale.
                            var pcands = candidateProvider.GetCandidates(rule.PieceTexts[pi]);
                            if (pcands.Count == 0) { ok = false; break; }
                            pieceWid = pcands[0].WordId;
                        }
                    }
                    allWordIds.Add(pieceWid);
                    pieceRanges[pi] = (start0 + off, pieceLen, ruleWid);
                    int bonus = (rule.Mode == Jiten.Parser.Resolution.SplitMode.FirstPieceBonus && pi == 0)
                        ? rule.Score : 0;
                    splitSeeds.Add((start0 + off, pieceLen, pieceWid, bonus, pi == 0));
                    off += pieceLen;
                }
                if (!ok) continue;

                // Synthesise a compound edge at (start0, compoundLen) carrying the
                // rule's wordId so Phase 3 has something to score against the
                // split path. Needed because the edge-driven loop couldn't find
                // this edge in the lattice — the deconjugator doesn't map the
                // conjugated surface back to the compound's base wordId.
                allWordIds.Add(ruleWordId);
                synthCompoundSeeds.Add((start0, compoundLen, ruleWordId));

                if (rule.Mode != Jiten.Parser.Resolution.SplitMode.FirstPieceBonus)
                {
                    compoundSplitRules[(start0, compoundLen, ruleWordId)] = (rule.Score, rule.Mode, pieceRanges);
                }
            }
        }
        }

        if (ProfileBeam)
        {
            splitMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }

        if (allWordIds.Count == 0) return false;

        // Pre-pass for stemStrip synth (chau/ちまう/じゃう/じまう contractions): look up
        // virtual-stem candidates (e.g. 分かって for 分かっちゃう) and collect their wordIds
        // so Phase 2's batch fetch loads them into wordCache. Without this, the synth
        // in Phase 2d below can't resolve POS or conjugation chain for virtual stems,
        // since Phase 1 only enumerates candidate wordIds for substrings of the input.
        if (UseIchiranScoring && Jiten.Parser.Resolution.Suffixes.All.Any(r => r.StemStrip > 0))
        {
            CollectStemStripVirtualStemWordIds(text, sentenceCache, candidateProvider, allWordIds);
        }

        // Ichiran load-kf virtual attached surfaces (see InjectVirtualAttachedEdge):
        // ensure their wordIds are in wordCache for the suffix-compound pass.
        if (UseIchiranScoring && text.IndexOf("ろう", StringComparison.Ordinal) >= 0)
            allWordIds.Add(1928670);

        // Phase 2: resolve word data. The flat wordArray (10M slots, O(1)
        // indexed by WordId) is the primary source — zero hashing, single memory
        // load. A small overflow dict catches rare IDs that aren't in the array
        // (fetched from Redis/Postgres). No per-sentence Dictionary construction.
        var wordArray = jmDictCache.GetWordArray();
        Dictionary<int, JmDictWord>? wordCacheOverflow = null;
        if (wordArray != null)
        {
            List<int>? uncached = null;
            foreach (var id in allWordIds)
            {
                if (!((uint)id < (uint)wordArray.Length && wordArray[id] != null))
                {
                    uncached ??= new List<int>();
                    uncached.Add(id);
                }
            }
            if (uncached != null)
            {
                try { wordCacheOverflow = await jmDictCache.GetWordsAsync(uncached); }
                catch { }
            }
        }
        else
        {
            wordCacheOverflow = await jmDictCache.GetWordsAsync(allWordIds);
        }
        var wordCache = new WordCacheView(wordArray, wordCacheOverflow);
        if (ProfileBeam)
        {
            fetchMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // Phase 2b: merge Sudachi token edges into the lattice. For each seed (pos, len, id),
        // if no existing edge at (pos, len) already includes this wordId, append a synthetic
        // SurfaceCandidate. This makes the "keep the compound" path reachable in the beam.
        foreach (var (spos, slen, sid) in sudachiSeeds)
        {
            if (spos >= text.Length) continue;
            var list = edgesByStart[spos];
            int idx = list.FindIndex(e => e.Length == slen);
            if (idx < 0)
            {
                list.Add((slen, new SurfaceCandidate[] { new(sid, 0, null, sentenceCache.GetSurface(spos, slen)) }));
            }
            else
            {
                var existing = list[idx].Cands;
                if (!existing.Any(c => c.WordId == sid))
                {
                    var merged = new SurfaceCandidate[existing.Count + 1];
                    for (int i = 0; i < existing.Count; i++) merged[i] = existing[i];
                    merged[existing.Count] = new SurfaceCandidate(sid, 0, null, sentenceCache.GetSurface(spos, slen));
                    list[idx] = (slen, merged);
                }
            }
        }

        // Phase 2c-compound: inject the synthesised compound edges produced by
        // Phase 1c-seq. These are compound edges whose wordId wasn't present in
        // the lattice from the candidate provider, so the compound's score has
        // to come from the synth + batch-fetched JmDictWord. Skip rules whose
        // wordId didn't resolve in the fetch (e.g. private/old seq).
        foreach (var (cs, cl, cw) in synthCompoundSeeds)
        {
            if (!wordCache.ContainsKey(cw)) continue;
            if (cs >= text.Length) continue;
            var clist = edgesByStart[cs];
            int cidx = clist.FindIndex(e => e.Length == cl);
            if (cidx < 0)
            {
                clist.Add((cl, new SurfaceCandidate[] { new(cw, 0, null, sentenceCache.GetSurface(cs, cl)) }));
            }
            else if (!clist[cidx].Cands.Any(c => c.WordId == cw))
            {
                var existingC = clist[cidx].Cands;
                var mergedC = new SurfaceCandidate[existingC.Count + 1];
                for (int i = 0; i < existingC.Count; i++) mergedC[i] = existingC[i];
                mergedC[existingC.Count] = new SurfaceCandidate(cw, 0, null, sentenceCache.GetSurface(cs, cl));
                clist[cidx] = (cl, mergedC);
            }
        }

        // Phase 2c: inject split-rule piece edges. The compound edge for the
        // split's parent wordId is not removed — it remains as an alternative.
        var splitBonusByKey = new Dictionary<(int Start, int Len, int WordId), int>();
        foreach (var (sps, spl, spw, sbonus, isFirst) in splitSeeds)
        {
            if (sps >= text.Length) continue;
            var list2 = edgesByStart[sps];
            int idx = list2.FindIndex(e => e.Length == spl);
            if (idx < 0)
            {
                list2.Add((spl, new SurfaceCandidate[] { new(spw, 0, null, sentenceCache.GetSurface(sps, spl)) }));
            }
            else if (!list2[idx].Cands.Any(c => c.WordId == spw))
            {
                var existing2 = list2[idx].Cands;
                var merged2 = new SurfaceCandidate[existing2.Count + 1];
                for (int i = 0; i < existing2.Count; i++) merged2[i] = existing2[i];
                merged2[existing2.Count] = new SurfaceCandidate(spw, 0, null, sentenceCache.GetSurface(sps, spl));
                list2[idx] = (spl, merged2);
            }
            if (isFirst && sbonus != 0)
                splitBonusByKey[(sps, spl, spw)] = sbonus;
        }

        // Suffix rules: two modes live side-by-side.
        //   (a) Adjacency-level signal (BonusFor + SuffixAdjacencyBonus) — always on.
        //       When A is a rule stem and B is one of the attached words, the A→B
        //       transition earns rule.Score. Directed, small positive bias.
        //   (b) Phase 2d compound synthesis (Ichiran mode only) — synthesizes a single
        //       edge at (start, stem+attached) carrying the stem's wordId. Phase 3
        //       scores it with useLen=total + Ichiran's apply-score-mod (§10.9):
        //           score = prop × coeff(baseLen, class)
        //                 + prop × tail_coeff(extra)         ← §10.9 use-length bonus
        //                 + score_mod × prop × extra         ← §10.9 apply-score-mod
        //       This gives compounds a multiplicative edge over the split path.
        // At writeback, suffix-compound wins are DECOMPOSED into stem + attached
        // WordInfos — the compound was a scoring device, not a semantic merge, so
        // downstream lemma stats still see two distinct tokens.
        var suffixCompounds = new Dictionary<(int Start, int Len, int WordId), SuffixCompoundInfo>();
        if (UseIchiranScoring && Jiten.Parser.Resolution.Suffixes.All.Count > 0)
        {
            // Map Sudachi token-start position → max length starting there. In hybrid mode
            // this drives the synthesis guard: if a Sudachi token at midPos is longer than
            // our attached edge, we'd be breaking Sudachi's segmentation to force a
            // compound — skip. In pure-Ichiran mode the map is left empty so the guard
            // becomes a no-op and synthesis is driven by suffix rules alone.
            var sudachiStartLengths = new Dictionary<int, int>();
            if (!PureIchiran)
            {
                foreach (var (_, pos, len) in sentence.Words)
                    if (!sudachiStartLengths.TryGetValue(pos, out var cur) || len > cur)
                        sudachiStartLengths[pos] = len;
            }
            BuildSuffixCompoundEdges(text, sentenceCache, edgesByStart, wordCache, sudachiStartLengths, suffixCompounds, lookups, candidateProvider);

            // Register split rules for suffix-synth compounds. These edges live
            // in suffixCompounds but not in edgesByStart, so the edge-driven
            // Phase 1c loop never sees them. Ichiran's get-split (dict-split.lisp)
            // uses seq-identity, cascading the split to any conjugation of the
            // rule's seq — so e.g. the 2007500 (落ちこぼれる) split also fires on
            // 落ちこぼれている (suffix-synth compound at (0, 8, 2007500)).
            // Conj-match piece resolution mirrors Phase 1c, with an extra
            // fallback: if the tail isn't in the ConjugationTable / Lookups it
            // may itself be a suffix-synth compound (e.g. こぼれている), so look
            // there too.
            foreach (var (scKey, _) in suffixCompounds)
            {
                var (scStart, scLen, scWid) = scKey;
                if (compoundSplitRules.ContainsKey((scStart, scLen, scWid))) continue;
                if (!Jiten.Parser.Resolution.Splits.TryGet(scWid, out var rule)) continue;
                if (rule.PieceTexts.Length < 2) continue;
                if (rule.Mode == Jiten.Parser.Resolution.SplitMode.FirstPieceBonus) continue;

                int nonFinalLen = 0;
                for (int i = 0; i < rule.PieceTexts.Length - 1; i++)
                    nonFinalLen += rule.PieceTexts[i].Length;
                if (scLen <= nonFinalLen || rule.Text.Length < nonFinalLen) continue;
                if (string.CompareOrdinal(rule.Text, 0, text, scStart, nonFinalLen) != 0) continue;
                // Guard against applying the split to aggressively conjugated
                // compounds where the tail piece grows far beyond the rule's
                // dict form. Ichiran's get-split matches by seq-identity but the
                // PART pieces are resolved in Ichiran via its own conjugation
                // table, which handles this cleanly; we approximate via surface
                // match and the tail can balloon. Require the compound span to
                // be within 3 chars of the rule's dict form.
                if (scLen > rule.Text.Length + 4) continue;

                int off = 0;
                bool ok = true;
                var pieceRanges = new (int Start, int Len, int? WordId)[rule.PieceTexts.Length];
                for (int pi = 0; pi < rule.PieceTexts.Length; pi++)
                {
                    int pieceLen;
                    int? ruleWid = null;
                    int pieceWid;
                    if (pi == rule.PieceTexts.Length - 1)
                    {
                        pieceLen = scLen - off;
                        if (rule.PieceWordIds != null && pi < rule.PieceWordIds.Length
                            && rule.PieceWordIds[pi].HasValue)
                        {
                            pieceWid = rule.PieceWordIds[pi]!.Value;
                            ruleWid = pieceWid;
                        }
                        else
                        {
                            var tailSurface = sentenceCache.GetSurface(scStart + off, pieceLen);
                            var tailCands = candidateProvider.GetCandidates(tailSurface);
                            if (tailCands.Count > 0)
                            {
                                pieceWid = tailCands[0].WordId;
                            }
                            else
                            {
                                int? fallback = null;
                                foreach (var (ck, _) in suffixCompounds)
                                {
                                    if (ck.Start == scStart + off && ck.Len == pieceLen)
                                    {
                                        fallback = ck.WordId;
                                        break;
                                    }
                                }
                                if (fallback == null) { ok = false; break; }
                                pieceWid = fallback.Value;
                            }
                        }
                    }
                    else
                    {
                        pieceLen = rule.PieceTexts[pi].Length;
                        if (rule.PieceWordIds != null && pi < rule.PieceWordIds.Length
                            && rule.PieceWordIds[pi].HasValue)
                        {
                            pieceWid = rule.PieceWordIds[pi]!.Value;
                            ruleWid = pieceWid;
                        }
                        else if (lookups.TryGetValue(rule.PieceTexts[pi], out var pwids) && pwids.Count > 0)
                        {
                            pieceWid = pwids[0];
                        }
                        else
                        {
                            var pcands = candidateProvider.GetCandidates(rule.PieceTexts[pi]);
                            if (pcands.Count == 0) { ok = false; break; }
                            pieceWid = pcands[0].WordId;
                        }
                    }
                    allWordIds.Add(pieceWid);
                    pieceRanges[pi] = (scStart + off, pieceLen, ruleWid);
                    off += pieceLen;
                }
                if (!ok) continue;
                compoundSplitRules[(scStart, scLen, scWid)] = (rule.Score, rule.Mode, pieceRanges);
            }
        }

        if (ProfileBeam)
        {
            injectMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // Phase 3: build representative FormCandidate + node score per edge.
        // The interner is process-wide — trait IDs are deterministic per
        // (wordId, formText, conjChain), so cross-sentence reuse is safe and
        // avoids rebuilding ~200 traits per sentence.
        var transitionInterner = _sharedTransitionInterner;
        var nodes = new Dictionary<(int Start, int Len, int WordId), ScoredNode>();
        for (int start = 0; start < text.Length; start++)
        {
            foreach (var (len, cands) in edgesByStart[start])
            {
                int end = start + len;
                var surface = sentenceCache.GetSurface(start, len);
                FormScoringContext ctx = default;
                if (!UseIchiranScoring)
                    ctx = FormScoringContext.Create(
                        surface, dictionaryForm: null, normalizedForm: null,
                        isNameContext: false, sudachiReading: null,
                        isArchaicSentence: false,
                        isSentenceInitial: start == 0,
                        isSentenceFinal: end == text.Length);

                foreach (var sc in cands)
                {
                    if (beamProfile != null) beamProfile.NodeCandidatesSeen++;
                    var key = (start, len, sc.WordId);
                    if (nodes.ContainsKey(key))
                    {
                        if (beamProfile != null) beamProfile.NodeDuplicateKeys++;
                        continue;
                    }
                    if (!wordCache.TryGetValue(sc.WordId, out var jmWord))
                    {
                        if (beamProfile != null) beamProfile.NodeMissingWord++;
                        continue;
                    }

                    // Particle-inflection filter: particles don't inflect. If our
                    // deconjugator resolves a long surface to a particle wordId through
                    // a multi-step chain (e.g. "がいなきゃ" → が via [contracted,
                    // provisional, teiru, ...]), the candidate is spurious. Ichiran's
                    // deconjugator doesn't generate these. Skip unless it's the trivial
                    // direct-surface match (chain empty or null).
                    if (UseIchiranScoring
                        && sc.ConjugationChain != null && sc.ConjugationChain.Count > 0
                        && jmWord.PartsOfSpeech.Contains("prt"))
                    {
                        if (beamProfile != null) beamProfile.NodeFilteredBeforePick++;
                        continue;
                    }


                    // Gairaigo-hiragana mismatch filter: our Lookups table expands long-
                    // vowel marks (カール → かある) and can spuriously match hiragana text
                    // to katakana-only gairaigo entries. Skip when the surface is all
                    // hiragana but the entry's forms are exclusively katakana. Ichiran
                    // treats script mismatches strictly.
                    if (UseIchiranScoring && IsAllHiragana(text, start, len)
                        && IsKatakanaOnlyEntry(jmWord))
                    {
                        if (beamProfile != null) beamProfile.NodeFilteredBeforePick++;
                        continue;
                    }

                    // Short-kana JMnedict-only entries (proper nouns from the name
                    // dictionary that happen to be written as 2-3 hiragana characters)
                    // are not present in Ichiran's lattice and almost always beat
                    // better verb/noun paths when enumerated. Example: わかるう parses
                    // as Waka(5008059)+Ruu(5007903) because both are name-fem entries.
                    // Kanji / katakana / ≥4-char name entries stay — tests legitimately
                    // expect proper-noun matches like 加藤紀子, ラムシャーリー, etc.
                    if (UseIchiranScoring && IsPureNameEntry(jmWord)
                        && len <= 3 && IsAllHiragana(text, start, len))
                    {
                        if (beamProfile != null) beamProfile.NodeFilteredBeforePick++;
                        continue;
                    }

                    // Single-char kana conjugated stems (chain-derived, not a direct
                    // dictionary-form entry) are absent from Ichiran's lattice. Our
                    // deconjugator produces こ ← 来る "(infinitive)" and similar 1-char
                    // verb-stem proxies that pollute short-text parses (もうこころ →
                    // もう+こ+ころ beats the correct もう+こころ). Ichiran's conjugation
                    // system doesn't surface these as standalone segments. Drop them
                    // in Ichiran mode; non-chain (identity) 1-char kana dict entries
                    // (particles, copulas, ん) stay — they're real standalone words.
                    if (UseIchiranScoring && len == 1 && JapaneseTextHelper.IsKana(text[start])
                        && sc.ConjugationChain != null && sc.ConjugationChain.Count > 0)
                    {
                        if (beamProfile != null) beamProfile.NodeFilteredBeforePick++;
                        continue;
                    }

                    // The negative a-stem (mizenkei before ない/れる/せる) is never a valid
                    // standalone segment — it only forms the left half of negative/passive/causative
                    // compounds. Including it as a standalone node produces high-scoring spurious
                    // lattice entries (からさ as 枯らす a-stem, score 275, beating any correct split).
                    if (UseIchiranScoring
                        && sc.ConjugationChain?.Count == 1
                        && sc.ConjugationChain[0] == "('a' stem)")
                    {
                        if (beamProfile != null) beamProfile.NodeFilteredBeforePick++;
                        continue;
                    }

                    // Ichiran calc-score zero-outs — check before PickFormCandidate
                    // to avoid the pick + prop cost for always-zero candidates.
                    bool isFinalPosition = start + len == text.Length;
                    if (UseIchiranScoring &&
                        (SkipWords.Contains(sc.WordId)
                         || (!isFinalPosition && FinalPrtSeqs.Contains(sc.WordId))))
                    {
                        if (beamProfile != null) beamProfile.NodeFilteredBeforePick++;
                        continue;
                    }

                    FormCandidate? formCand;
                    if (beamProfile != null)
                    {
                        beamProfile.NodePickCalls++;
                        long nodePickStart = Stopwatch.GetTimestamp();
                        formCand = PickFormCandidate(jmWord, surface, sc);
                        beamProfile.NodePickTicks += Stopwatch.GetTimestamp() - nodePickStart;
                    }
                    else
                    {
                        formCand = PickFormCandidate(jmWord, surface, sc);
                    }
                    if (formCand == null)
                    {
                        if (beamProfile != null) beamProfile.NodeNoForm++;
                        continue;
                    }

                    FormScoreTrace trace = default;
                    int freqBonus = 0, hintBonus = 0, kanaPenalty = 0;
                    if (!UseIchiranScoring)
                    {
                        if (beamProfile != null)
                        {
                            beamProfile.NodeScoreCalls++;
                            long nodeScoreStart = Stopwatch.GetTimestamp();
                            trace = FormCandidateScorer.Score(formCand, ctx, Parser.ArchaicPosTypes);
                            beamProfile.NodeScoreTicks += Stopwatch.GetTimestamp() - nodeScoreStart;
                        }
                        else
                        {
                            trace = FormCandidateScorer.Score(formCand, ctx, Parser.ArchaicPosTypes);
                        }
                        formCand.SetScoreTrace(trace);
                        int freqRank = frequencyRanks.TryGetValue(formCand.Word.WordId, out int fr) ? fr : int.MaxValue;
                        freqBonus = FreqBonusFromRank(freqRank);
                        hintBonus = sudachiHints.TryGetValue((start, len), out var _hb) ? _hb : 0;
                        if (len == 1 && JapaneseTextHelper.IsKana(text[start]))
                            kanaPenalty = IsFunctionalKanaPos(jmWord) ? SingleCharFunctionalKanaPenalty : SingleCharKanaPenalty;
                    }

                    bool isStrong = HasKanjiOrKatakana(text, start, len);
                    int lenBonus = LengthMultiplier(len, isStrong);
                    int splitBonus = splitBonusByKey.TryGetValue((start, len, sc.WordId), out var sb) ? sb : 0;

                    int? useLen = null;
                    int suffixScoreMod = 0;
                    bool suffixScoreIsConstant = false;
                    string? scoreBaseSurface = null;
                    if (UseIchiranScoring && suffixCompounds.TryGetValue((start, len, sc.WordId), out var sfxInfo))
                    {
                        useLen = IchiranPropScorer.CountMora(surface);
                        suffixScoreMod = sfxInfo.ScoreMod;
                        suffixScoreIsConstant = sfxInfo.ScoreIsConstant;
                        scoreBaseSurface = sfxInfo.ScoreBaseSurface;
                    }

                    IchiranPropScore prop;
                    if (beamProfile != null)
                    {
                        beamProfile.NodePropCalls++;
                        long nodePropStart = Stopwatch.GetTimestamp();
                        prop = IchiranPropScorer.Compute(
                            jmWord, formCand.Form, surface, sc.ConjugationChain,
                            isSentenceFinal: isFinalPosition,
                            useLength: useLen,
                            scoreBaseText: scoreBaseSurface);
                        beamProfile.NodePropTicks += Stopwatch.GetTimestamp() - nodePropStart;
                    }
                    else
                    {
                        prop = IchiranPropScorer.Compute(
                            jmWord, formCand.Form, surface, sc.ConjugationChain,
                            isSentenceFinal: isFinalPosition,
                            useLength: useLen,
                            scoreBaseText: scoreBaseSurface);
                    }

                    int nodeScore;
                    if (UseIchiranScoring)
                    {
                        // §10.8: score = prop × length_multiplier_coeff(len, class)
                        //        where len is the scored text's length (base for compounds,
                        //        surface for simples).
                        var cls = IchiranPropScorer.ClassFor(prop.Flags.KanjiP, prop.KatakanaP);
                        int coeff = IchiranPropScorer.LengthMultiplierCoeff(prop.Len, cls);
                        coeff = IchiranPropScorer.ApplyNKanjiBonus(coeff, prop.NKanji);
                        int baseScore = prop.Score * coeff;

                        // §10.9: use-length bonus for compound-text.
                        //   tail-class = (baseLen > 3 && strong) ? :ltail : :tail
                        //   bonus = prop × coeff(extra, tail-class) + apply-score-mod(SM, prop, extra)
                        //   apply-score-mod (integer SM) = SM × prop × extra
                        int useBonus = 0;
                        if (useLen.HasValue && useLen.Value > prop.Len)
                        {
                            int extra = useLen.Value - prop.Len;
                            var tailCls = (prop.Len > 3 && (prop.Flags.KanjiP || prop.KatakanaP))
                                ? IchiranPropScorer.LengthClass.Ltail
                                : IchiranPropScorer.LengthClass.Tail;
                            int tailCoeff = IchiranPropScorer.LengthMultiplierCoeff(extra, tailCls);
                            useBonus = prop.Score * tailCoeff;
                            if (suffixScoreMod != 0)
                            {
                                // Ichiran apply-score-mod (dict.lisp §10.9):
                                //   integer SM        → SM × prop × extra   (multiplicative)
                                //   function SM       → SM(prop)            (e.g. `constantly N` → just N)
                                // ScoreIsConstant signals the `(constantly N)` case — add N directly.
                                useBonus += suffixScoreIsConstant
                                    ? suffixScoreMod
                                    : suffixScoreMod * prop.Score * extra;
                            }
                        }

                        // splitBonus = score-mod from splits.json; additive per §10.10 `:score` mode.
                        // No ad-hoc kana penalty in Ichiran mode — Ichiran relies on
                        // *score-cutoff* (Phase F below) to drop weak segments. Keeping the
                        // kana penalty would double-punish 1-char particles that already
                        // score low under prop × weak[1]=1.
                        nodeScore = baseScore + useBonus + splitBonus;

                        // §10.11 kanji-break-penalty (Ichiran dict.lisp:702). An edge is
                        // penalised when its start or end boundary aligns with a kanji-break
                        // position in the sentence (two adjacent kanji, or a forced break
                        // from *force-kanji-break*). The penalty halves the score and adds a
                        // small POS-specific bonus — num at start (+5), suf/n-suf at start
                        // (+10), pref at end (+12) — matching Ichiran exactly. Words in
                        // *no-kanji-break-penalty* are fully exempt. Ichiran also skips the
                        // "second word of で/す break" — when the break lands at the edge
                        // start and the edge starts with す, no penalty (the first half
                        // already paid).
                        bool breakAtBeg = kanjiBreaks.Contains(start);
                        bool breakAtEnd = kanjiBreaks.Contains(start + len);
                        if ((breakAtBeg || breakAtEnd) && !NoKanjiBreakPenaltyWordIds.Contains(sc.WordId))
                        {
                            bool suSkip = breakAtBeg && text[start] == 'す';
                            if (!suSkip)
                            {
                                var pos = jmWord.CachedPOS;
                                int kbBonus = 0;
                                if (breakAtBeg && pos.Contains(PartOfSpeech.Numeral)) kbBonus = 5;
                                else if (breakAtBeg && (pos.Contains(PartOfSpeech.Suffix)
                                                     || pos.Contains(PartOfSpeech.NounSuffix))) kbBonus = 10;
                                else if (breakAtEnd && pos.Contains(PartOfSpeech.Prefix)) kbBonus = 12;
                                nodeScore = Math.Max(IchiranScoreCutoff, (nodeScore + 1) / 2 + kbBonus);
                            }
                        }
                    }
                    else
                    {
                        nodeScore = freqBonus + lenBonus + hintBonus - kanaPenalty + splitBonus;
                    }

                    // Phase F: *score-cutoff* = 5 (§9.5). Drop weak edges before they enter
                    // the lattice. Only applied in Ichiran mode — the additive path has its
                    // own tuning. Kept Sudachi-seeded edges exempt so the baseline path
                    // stays reachable even when its edges score below the cutoff.
                    if (UseIchiranScoring && nodeScore < IchiranScoreCutoff)
                    {
                        if (!sudachiSeedSet.Contains((start, len, sc.WordId)))
                        {
                            if (beamProfile != null) beamProfile.NodeCutoffRejected++;
                            continue;
                        }
                    }
                    int transitionTraitId = transitionInterner.GetOrAddTrait(formCand, sc.ConjugationChain);
                    int attachedSuffixPenalty = 0;
                    if (UseIchiranScoring && prop.CommonBonus > 0
                        && jmWord.PartsOfSpeech.Contains("suf")
                        && BeamTransitionInterner.HasConjugableVerbPos(jmWord.PartsOfSpeech))
                    {
                        var sfxCls = IchiranPropScorer.ClassFor(prop.Flags.KanjiP, prop.KatakanaP);
                        int sfxCoeff = IchiranPropScorer.LengthMultiplierCoeff(prop.Len, sfxCls);
                        sfxCoeff = IchiranPropScorer.ApplyNKanjiBonus(sfxCoeff, prop.NKanji);
                        attachedSuffixPenalty = prop.CommonBonus * sfxCoeff;
                    }
                    nodes[key] = new ScoredNode(formCand, nodeScore, sc.ConjugationChain,
                        formCand.TotalScore, freqBonus, lenBonus, hintBonus, kanaPenalty,
                        prop.Score, prop.Flags, prop.KatakanaP, transitionTraitId,
                        attachedSuffixPenalty);
                    if (beamProfile != null) beamProfile.NodeBuilt++;
                }
            }
        }
        if (ProfileBeam)
        {
            nodeMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // Phase 3b: §10.10 split dispatch for non-FirstPieceBonus modes. Compound edges
        // matching a split rule get their nodeScore revised per the rule's mode, using
        // piece nodeScores already computed in Phase 3. Runs before Phase 4 so the DP
        // sees the revised compound scores.
        //   • Score   → compound.nodeScore += scoreMod.
        //   • PScore  → new-prop = max(1, prop + scoreMod);
        //               nodeScore = ceil(nodeScore × new-prop / old-prop).
        //   • Replace → nodeScore = scoreMod + sum(piece.nodeScore).
        // When any required piece is missing from `nodes` (piece score was culled by
        // score-cutoff or deduplicated out), Replace falls back to a no-op — the
        // original compound score stands. Avoids pathological "compound=scoreMod
        // because pieces got dropped".
        // Fast index: (start, len) → best NodeScore across all wordIds at that range.
        // Used by Replace mode to pick whichever wordId the lattice produced for each
        // piece, since the split loader's lookups[text][0] often disagrees with the
        // candidate provider's actual choice.
        // Ichiran's conj-of cascade (dict-split.lisp:69 get-split*): if the
        // matched reading's own seq has no split rule, check each ancestor in
        // conj-of — a rule on any ancestor applies to the current reading.
        // Conj-of ancestors come from Ichiran's CONJUGATION rows, created at
        // build time by exact-surface matching between a verb's paradigm output
        // and other JMdict entries' readings.
        //
        // We approximate this at parse time: when a rule-bearing node (start,
        // len, V) with a masu-stem chain is co-located with a chain-less node
        // (start, len, M) whose dict form IS the span surface, M is a deverbal
        // noun genuinely derived from V — cascade. The dict-form-equals-surface
        // check is the key discriminator: 1609430 (落ちこぼれ) has KanjiForm
        // 落ちこぼれ which equals the span; 1555830 (隣 / となり) has KanjiForm
        // 隣 which does NOT equal the span surface となり, so no cascade fires.
        {
            var cascades = new List<((int, int, int) Key, (int, Jiten.Parser.Resolution.SplitMode, (int, int, int?)[]) Rule)>();
            foreach (var (ckey, ruleData) in compoundSplitRules)
            {
                if (!nodes.TryGetValue(ckey, out var src)) continue;
                if (src.ConjugationChain == null || src.ConjugationChain.Count == 0) continue;
                bool srcIsMasuStem = false;
                foreach (var t in src.ConjugationChain)
                    if (t == "(infinitive)" || t == "masu-stem") { srcIsMasuStem = true; break; }
                if (!srcIsMasuStem) continue;

                var spanSurface = sentenceCache.GetSurface(ckey.Start, ckey.Len);
                foreach (var (otherKey, otherNode) in nodes)
                {
                    if (otherKey.Start != ckey.Start || otherKey.Len != ckey.Len) continue;
                    if (otherKey.WordId == ckey.WordId) continue;
                    if (compoundSplitRules.ContainsKey(otherKey)) continue;
                    if (otherNode.ConjugationChain != null && otherNode.ConjugationChain.Count > 0) continue;

                    // Target must be a deverbal-noun-like entry whose dict form
                    // IS the span surface — i.e. this surface IS M's primary
                    // lexical identity. Rules out 1555830 (隣/となり) where the
                    // kanji form 隣 doesn't match the span's kana surface.
                    var pos = otherNode.Cand.Word.PartsOfSpeech;
                    if (!(pos.Contains("n") || pos.Contains("adj-no"))) continue;

                    bool surfaceIsDictForm = false;
                    foreach (var form in otherNode.Cand.Word.Forms)
                    {
                        if (form.FormType == Jiten.Core.Data.JMDict.JmDictFormType.KanjiForm
                            && form.Text == spanSurface)
                        {
                            surfaceIsDictForm = true;
                            break;
                        }
                    }
                    if (!surfaceIsDictForm) continue;

                    cascades.Add((otherKey, ruleData));
                }
            }
            foreach (var (k, v) in cascades)
                compoundSplitRules.TryAdd(k, v);
        }

        // Phase 3b: split dispatch for non-FirstPieceBonus modes. Compound edges
        // matching a split rule get their nodeScore revised per the rule's mode,
        // using piece nodeScores already computed in Phase 3.
        Dictionary<(int Start, int Len), int>? bestNodeScoreByRange = null;
        foreach (var (ckey, (scoreMod, mode, pieceRanges)) in compoundSplitRules)
        {
            if (!nodes.TryGetValue(ckey, out var compound)) continue;
            int adjusted = compound.NodeScore;
            switch (mode)
            {
                case Jiten.Parser.Resolution.SplitMode.Score:
                    adjusted = compound.NodeScore + scoreMod;
                    break;
                case Jiten.Parser.Resolution.SplitMode.PScore:
                    if (compound.PropScore > 0)
                    {
                        int newProp = Math.Max(1, compound.PropScore + scoreMod);
                        adjusted = (int)Math.Ceiling((double)compound.NodeScore * newProp / compound.PropScore);
                    }
                    break;
                case Jiten.Parser.Resolution.SplitMode.Replace:
                    if (bestNodeScoreByRange == null)
                    {
                        bestNodeScoreByRange = new Dictionary<(int, int), int>(nodes.Count);
                        foreach (var ((ns, nl, _), node) in nodes)
                        {
                            var rng = (ns, nl);
                            if (!bestNodeScoreByRange.TryGetValue(rng, out var cur) || node.NodeScore > cur)
                                bestNodeScoreByRange[rng] = node.NodeScore;
                        }
                    }
                    int sum = 0;
                    bool allPresent = true;
                    foreach (var pr in pieceRanges)
                    {
                        // When the rule specifies an explicit wordId for this piece
                        // (Ichiran's `(text seq)` form, e.g. split-hairikomeru's 入り = 1465590),
                        // anchor to that node's score rather than picking the best-scoring
                        // wordId at the range. Matches Ichiran's calc-score which resolves
                        // the piece via the rule-specified seq, not a generic lookup.
                        if (pr.WordId.HasValue
                            && nodes.TryGetValue((pr.Start, pr.Len, pr.WordId.Value), out var ruleNode))
                        {
                            sum += ruleNode.NodeScore;
                        }
                        else if (bestNodeScoreByRange.TryGetValue((pr.Start, pr.Len), out var pns))
                        {
                            sum += pns;
                        }
                        else { allPresent = false; break; }
                    }
                    if (allPresent) adjusted = scoreMod + sum;
                    break;
            }
            if (adjusted != compound.NodeScore)
                nodes[ckey] = compound with { NodeScore = adjusted };
        }

        // Phase 3c: port of Ichiran's `cull-segments` (dict.lisp:1020-1036).
        // Within each (start, end) span, drop candidates whose nodeScore falls below
        // maxScore × *identical-word-score-cutoff* (1/2). Ichiran runs this per span
        // before the DP so tie-heavy spans don't pollute the beam with near-duplicate
        // candidates that all score within noise of each other — the weakest ones
        // would survive cutoff=5 but still alter pairing dynamics in find-best-path.
        // We preserve the integer-math equivalent of `>= max × 1/2` via `2 * score >= max`.
        // Only active under Ichiran scoring; the additive path keeps all candidates.
        if (UseIchiranScoring && nodes.Count > 1)
        {
            var maxByRange = new Dictionary<(int Start, int Len), int>();
            foreach (var (k, n) in nodes)
            {
                var rng = (k.Start, k.Len);
                if (!maxByRange.TryGetValue(rng, out var cur) || n.NodeScore > cur)
                    maxByRange[rng] = n.NodeScore;
            }
            var toCull = new List<(int Start, int Len, int WordId)>();
            foreach (var (k, n) in nodes)
            {
                int max = maxByRange[(k.Start, k.Len)];
                if (max > 0 && 2 * n.NodeScore < max) toCull.Add(k);
            }
            foreach (var k in toCull) nodes.Remove(k);
        }

        // Phase 4: DP beam — either position-anchored (default) or segment-anchored
        // (Ichiran-style). The segment path is selected via JITEN_BEAM_SEGMENT_DP=1
        // and produces a `terminal` list shaped like the position DP so all
        // downstream logic (logging, writeback, diagnostics) remains unchanged.
        var bonusCache = _sharedBonusCache;
        List<BeamState> terminal;
        if (UseSegmentDP)
        {
            terminal = RunSegmentDP(text, nodes, bonusCache, transitionInterner, diagnostics != null, beamProfile);
            if (terminal.Count == 0) return false;
            goto phase4Done;
        }

        // Phase 4 (position-DP): DP beam over end-position.
        // State: (pending last candidate, accumulated score, gap chars, segs)
        var beamByPos = new Dictionary<int, List<BeamState>>();
        beamByPos[0] = new List<BeamState> { BeamState.Empty() };

        for (int pos = 0; pos < text.Length; pos++)
        {
            if (!beamByPos.TryGetValue(pos, out var states) || states.Count == 0)
                continue;

            foreach (var (len, cands) in edgesByStart[pos])
            {
                int nextPos = pos + len;
                foreach (var sc in cands)
                {
                    if (!nodes.TryGetValue((pos, len, sc.WordId), out var node)) continue;

                    foreach (var state in states)
                    {
                        int finalizedPrevBonus = state.PendingTransitionTraitId != 0
                            ? BonusForCached(
                                bonusCache,
                                transitionInterner,
                                state.PendingTransitionTraitId,
                                node.TransitionTraitId,
                                serial: state.PendingGapChars == 0,
                                profile: beamProfile)
                            : 0;

                        // Ichiran's apply-segfilters runs BEFORE get-synergies/get-penalties
                        // (dict.lisp:1175-1178) — segfilter rejections HARD-PRUNE the pair
                        // from the DP, never adding it as a path. Our per-pair BonusFor
                        // returns -10000 for filter matches; without this check, the
                        // max-clause below (pairFloor = 1 + score) recovers from the
                        // penalty and lets invalid pairs enter the beam anyway.
                        if (UseIchiranScoring && state.PendingCand != null
                            && finalizedPrevBonus <= SegFilterRejectPenalty / 2)
                            continue;

                        // Phase D: Ichiran's find-best-path max clause (§9.7).
                        //   pair = max(score(left) + synergy + score(right), 1+score(left), 1+score(right))
                        // Replaces the raw sum contribution when the pair would otherwise let
                        // weak synergy-free chains out-score single strong segments. Enabled
                        // alongside Ichiran multiplicative scoring; the additive path keeps
                        // the traditional sum.
                        int newScore;
                        if (UseIchiranScoring && state.PendingCand != null)
                        {
                            int pendingNode = state.PendingNodeScore;
                            int pairSum = pendingNode + finalizedPrevBonus + node.NodeScore;
                            int pairFloorLeft = 1 + pendingNode;
                            int pairFloorRight = 1 + node.NodeScore;
                            int pair = Math.Max(pairSum, Math.Max(pairFloorLeft, pairFloorRight));
                            newScore = state.Score - pendingNode + pair;
                        }
                        else
                        {
                            newScore = state.Score + finalizedPrevBonus + node.NodeScore;
                        }

                        var newSegs = new List<PathSegment>(state.Segs.Count + 1);
                        newSegs.AddRange(state.Segs);
                        newSegs.Add(new PathSegment(pos, len, node.Cand, node.ConjugationChain));

                        var newState = new BeamState(
                            PendingCand: node.Cand,
                            PendingConjChain: node.ConjugationChain,
                            PendingNodeScore: node.NodeScore,
                            PendingTransitionTraitId: node.TransitionTraitId,
                            PendingGapChars: 0,
                            Score: newScore,
                            GapChars: state.GapChars,
                            Segs: newSegs);

                        Push(beamByPos, nextPos, newState);
                    }
                }
            }

            // Gap edge — advance 1 char as uncovered.
            // Ichiran (dict.lisp:1165-1169) allows arbitrary gap spans with linear
            // -500/char penalty; our per-char rate matches. The `MaxGapChars = 1`
            // cap is deliberately stricter because our DP has a failure mode
            // Ichiran's doesn't: per-position states mean a path can interleave
            // short real segments with gap advances, and will freely skip
            // sticky-adjacent small kana (ょ/ッ/ー) when the lexical completion is
            // missing from JMDict. Ichiran's segment-list DP scores whole segments
            // and never emits "gap between two 1-char fragments" paths. Empirically
            // any relaxation (cap=3 or no cap) regresses 15+ tests (1 win, 15+
            // losses) — the regression is architectural, not tunable. Closing it
            // requires porting the segment-list DP form; left as future work.
            foreach (var state in states)
            {
                if (state.GapChars >= MaxGapChars) continue;
                int nextPos = pos + 1;
                var newSegs = new List<PathSegment>(state.Segs.Count + 1);
                newSegs.AddRange(state.Segs);
                newSegs.Add(new PathSegment(pos, 1, null, null));

                var newState = new BeamState(
                    PendingCand: state.PendingCand,
                    PendingConjChain: state.PendingConjChain,
                    PendingNodeScore: state.PendingNodeScore,
                    PendingTransitionTraitId: state.PendingTransitionTraitId,
                    PendingGapChars: state.PendingGapChars + 1,
                    Score: state.Score - Constants.UncoveredCharPenalty,
                    GapChars: state.GapChars + 1,
                    Segs: newSegs);

                Push(beamByPos, nextPos, newState);
            }

            // Prune
            foreach (var (_, bucket) in beamByPos)
                if (bucket.Count > BeamWidth)
                {
                    bucket.Sort((a, b) => b.Score.CompareTo(a.Score));
                    bucket.RemoveRange(BeamWidth, bucket.Count - BeamWidth);
                }
        }

        if (!beamByPos.TryGetValue(text.Length, out terminal!) || terminal.Count == 0)
            return false;

        // Ichiran scores only adjacent segment pairs; there is no terminal
        // "next = nil" transition bonus.
        foreach (var s in terminal)
            s.FinalScore = s.Score;

        phase4Done:;
        if (ProfileBeam)
        {
            dpMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }

        List<BeamState> sortedTerminal;
        BeamState best;
        if (diagnostics != null)
        {
            sortedTerminal = terminal.OrderByDescending(s => s.FinalScore).ToList();
            best = sortedTerminal[0];
        }
        else
        {
            best = terminal[0];
            sortedTerminal = new List<BeamState>(1) { best };
        }
        EnsureSegmentsMaterialized(best, text.Length);

        // Sudachi-aligned baseline: score the path that matches existing Sudachi tokens as-is.
        // In Ichiran mode we DO NOT gate on this score — full-Ichiran semantics means the
        // beam's own top path always wins segmentation; Sudachi still provides WordInfo
        // reuse for boundaries that happen to align (see writeback below). The comparison
        // stays computed for diagnostics/debug. JITEN_BEAM_THRESHOLD remains respected for
        // threshold sweeps when deliberately overriding via env var.
        int sudachiAlignedScore = ScoreSudachiPath(
            sentence,
            sentenceCache,
            wordCache,
            nodes,
            frequencyRanks,
            sudachiHints,
            resolvedWordIdLookup,
            bonusCache,
            transitionInterner);
        if (ProfileBeam)
        {
            baselineMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            phaseStart = Stopwatch.GetTimestamp();
        }
        // Full-Ichiran mode: beam's own top path wins segmentation. Sudachi still provides
        // WordInfo reuse for boundaries that align in writeback, but doesn't veto scores.
        // JITEN_BEAM_THRESHOLD remains respected for threshold sweeps during debugging.
        bool thresholdSpecified = int.TryParse(Environment.GetEnvironmentVariable("JITEN_BEAM_THRESHOLD"), out _);
        bool beamWins = UseIchiranScoring && !thresholdSpecified
            ? true
            : best.FinalScore - sudachiAlignedScore >= SudachiFallbackThreshold;

        diagnostics?.LogResegmentationPath(
            text, best.Segs.Count, best.GapChars, best.GapChars * Constants.UncoveredCharPenalty,
            best.FinalScore, accepted: beamWins, "Beam");

        if (diagnostics != null)
        {
            var topPaths = new List<BeamPathEntry>();
            int take = Math.Min(20, sortedTerminal.Count);
            for (int r = 0; r < take; r++)
            {
                EnsureSegmentsMaterialized(sortedTerminal[r], text.Length);
                topPaths.Add(BuildPathEntry(sortedTerminal[r], r + 1, nodes, text));
            }

            diagnostics.LogBeamSentence(new BeamSentenceAnalysis(
                SentenceText: text,
                SudachiBaselineScore: sudachiAlignedScore,
                BestBeamScore: best.FinalScore,
                ThresholdUsed: SudachiFallbackThreshold,
                BeamApplied: beamWins,
                TopPaths: topPaths,
                SudachiPath: null));
        }

        if (!beamWins) return false;

        // Phase 5: write back. Refuse all-gap paths or paths that don't begin at 0.
        if (best.Segs.Count == 0 || best.Segs[0].Start != 0) return false;
        if (!best.Segs.Any(s => s.Cand != null)) return false;

        // If every beam segment aligns exactly with an existing Sudachi token boundary,
        // the beam chose identical segmentation to Sudachi — no point rewriting (and we'd
        // lose Sudachi's reading/morphology info by replacing with synthetic WordInfos).
        //
        // Skipped in pure-Ichiran mode — `sudachiHints` is empty there so the check is
        // vacuously true and every beam win would get discarded. Also, in pure mode we
        // WANT the beam to always replace the Sudachi path even on "apparent agreement"
        // (equivalent boundaries), so the downstream `ProcessWordsInBatches` re-resolves
        // from our chosen WordIds rather than inheriting Sudachi's upstream combines.
        if (!PureIchiran)
        {
            bool allAligned = best.Segs.All(s => s.Cand == null || sudachiHints.ContainsKey((s.Start, s.Len)));
            if (allAligned) return false;
        }

        // Mixed: reuse the original Sudachi WordInfo for aligned segments (preserves
        // reading/morphology) and emit synthetic WordInfos only for genuinely-new segments.
        // In pure-Ichiran mode the map is left empty so every segment gets a synthetic
        // WordInfo built from the beam's own candidate — we don't want CombineVerbDependants
        // decisions from the pipeline leaking into writeback. `ProcessWordsInBatches` later
        // re-resolves reading/form for the synthetic ones.
        var sudachiByBoundary = new Dictionary<(int, int), (WordInfo word, int pos, int len)>();
        if (!PureIchiran)
        {
            foreach (var tuple in sentence.Words)
                sudachiByBoundary[(tuple.position, tuple.length)] = tuple;
        }

        var replacements = new List<(WordInfo word, int position, int length)>(best.Segs.Count);
        foreach (var seg in best.Segs)
        {
            if (seg.Cand == null) continue; // gap — drop; downstream treats missing span as unresolved

            // Suffix-synth compounds are kept as a single WordInfo in writeback.
            // Tests expect 任せてくれ / 投下しました / 約束してくれる / 外出して to be one token
            // tied to the stem's wordId — even vs-noun+suru which is semantically
            // separable. Decomposition has been removed per the preserve-compound
            // expectation. The synth compound was a scoring device in the lattice;
            // preserving it at writeback matches the expected token list.

            if (sudachiByBoundary.TryGetValue((seg.Start, seg.Len), out var existing))
            {
                replacements.Add(existing);
                continue;
            }

            var segText = sentenceCache.GetSurface(seg.Start, seg.Len);
            var pos = seg.Cand.Word.CachedPOS.FirstOrDefault(
                p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown), PartOfSpeech.Noun);
            var dictForm = seg.Cand.Form.Text;
            var wi = new WordInfo
            {
                Text                       = segText,
                DictionaryForm             = dictForm,
                NormalizedForm             = dictForm,
                PartOfSpeech               = pos,
                Reading                    = segText.All(JapaneseTextHelper.IsKana) ? KanaConverter.ToHiragana(segText) : string.Empty,
                PreMatchedWordId           = seg.Cand.Word.WordId,
                PreMatchedCandidateWordIds = new List<int> { seg.Cand.Word.WordId },
            };
            replacements.Add((wi, seg.Start, seg.Len));
        }

        sentence.Words = replacements;
        if (ProfileBeam)
        {
            writebackMs = Stopwatch.GetElapsedTime(phaseStart).TotalMilliseconds;
            int edgeCount = 0;
            for (int i = 0; i < edgesByStart.Length; i++) edgeCount += edgesByStart[i].Count;
            var totalMs = Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds;
            double avgNodesPerSpan = beamProfile == null || beamProfile.DpSegmentLists == 0
                ? 0
                : (double)beamProfile.DpSpanNodeTotal / beamProfile.DpSegmentLists;
            double bonusHitRate = beamProfile == null || beamProfile.BonusLookups == 0
                ? 0
                : 100.0 * beamProfile.BonusCacheHits / beamProfile.BonusLookups;
            Console.Error.WriteLine(
                $"[beam-profile] len={text.Length} edges={edgeCount} words={allWordIds.Count} splitSeeds={splitSeeds.Count} splitRules={compoundSplitRules.Count} suffix={suffixCompounds.Count} nodes={nodes.Count} terminals={terminal.Count} " +
                $"enum={enumMs:F1} split={splitMs:F1} fetch={fetchMs:F1} inject={injectMs:F1} node={nodeMs:F1} dp={dpMs:F1} baseline={baselineMs:F1} writeback={writebackMs:F1} total={totalMs:F1}");
            if (beamProfile != null)
            {
                Console.Error.WriteLine(
                    $"[beam-node] seen={beamProfile.NodeCandidatesSeen} built={beamProfile.NodeBuilt} dup={beamProfile.NodeDuplicateKeys} filter={beamProfile.NodeFilteredBeforePick} " +
                    $"missingWord={beamProfile.NodeMissingWord} noForm={beamProfile.NodeNoForm} cutoff={beamProfile.NodeCutoffRejected} " +
                    $"pick={ProfileMs(beamProfile.NodePickTicks):F1} score={ProfileMs(beamProfile.NodeScoreTicks):F1} prop={ProfileMs(beamProfile.NodePropTicks):F1}");
                Console.Error.WriteLine(
                    $"[beam-dp] spans={beamProfile.DpSegmentLists} avgNodes={avgNodesPerSpan:F2} maxNodes={beamProfile.DpMaxNodesPerSpan} pairs={beamProfile.DpCompatibleSpanPairs} " +
                    $"seed={beamProfile.DpSeedPaths} tries={beamProfile.DpTransitionAttempts} expanded={beamProfile.DpTransitionAccepted} dominated={beamProfile.DpDominatedRejects} segReject={beamProfile.DpSegfilterRejects} " +
                    $"paths={beamProfile.DpPathStatesAllocated} top={beamProfile.DpTopAccepted}/{beamProfile.DpTopRejected}/{beamProfile.DpTopEvicted} " +
                    $"bonus={beamProfile.BonusCacheHits}/{beamProfile.BonusLookups}({bonusHitRate:F0}%) missEval={ProfileMs(beamProfile.BonusMissTicks):F1} " +
                    $"prep={ProfileMs(beamProfile.DpPrepTicks):F1} seedMs={ProfileMs(beamProfile.DpSeedTicks):F1} extend={ProfileMs(beamProfile.DpExtendTicks):F1} final={ProfileMs(beamProfile.DpFinalTicks):F1} cache=shared");
            }
        }
        return true;
    }

    // --------- helpers ---------

    // Synthesized suffix-compound edge metadata. Records the rule's score_mod plus the
    // (stem, attached) decomposition needed at writeback to emit two separate WordInfos.
    // EdgeCoveredByLattice: true when the conjugation table or direct lookup already
    // emitted an edge with the same (start, len, wordId) before Phase 2d ran. The
    // score_mod bonus still applies in Phase 3 (so the compound can outscore split
    // paths), but writeback skips decomposition — the lattice-provided edge is a real
    // dictionary form (e.g. 決まっている as a teiru-form of 決まる) and should stay as one
    // token. VsNoun rules are exempt — VsNoun+する pairs are semantically separable
    // even when conj-table covers the compound (`通用しない` → [通用, しない]).
    private readonly record struct SuffixCompoundInfo(
        int ScoreMod,
        int StemLen,
        int StemWordId,
        int AttachedWordId,
        bool EdgeCoveredByLattice,
        bool ScoreIsConstant = false,
        bool IsEndpointCompound = false,
        bool IsFromStemStrip = false,
        string? ScoreBaseSurface = null);

    [Flags]
    private enum SuffixStemMask
    {
        None     = 0,
        TeForm   = 1 << 0,
        VsNoun   = 1 << 1,
        MasuStem = 1 << 2,
        NegForm  = 1 << 3,
        AdjStem  = 1 << 4,
        AdvForm  = 1 << 5,
        SouBase  = 1 << 6,
        PastForm = 1 << 7,
        Pronoun  = 1 << 8,
    }

    private readonly record struct SuffixAttachedEntry(
        int Length,
        SurfaceCandidate Candidate,
        IReadOnlyList<Jiten.Parser.Resolution.SuffixRule> Rules,
        bool IsPolite);

    private readonly record struct SuffixStemCandidateInfo(
        JmDictWord? Word,
        bool HasConjugablePos,
        SuffixStemMask Mask);

    // Phase 2d: scan adjacent edge pairs and synthesize compound edges for suffix-rule
    // matches. For each (stem at posA, attached at posA+lenA) pair that satisfies a
    // rule, inject a compound edge at (posA, lenA+lenB) carrying the stem's wordId
    // plus the rule's score_mod (consumed by Phase 3's use-length bonus per §10.9).
    // Iterates over a SNAPSHOT of edge counts so the injected compound edges don't
    // feed back into synthesis (chained compounds would need deliberate design).
    private static void BuildSuffixCompoundEdges(
        string text,
        SentenceSurfaceCache sentenceCache,
        List<(int Length, IReadOnlyList<SurfaceCandidate> Cands)>[] edgesByStart,
        WordCacheView wordCache,
        IReadOnlyDictionary<int, int> sudachiStartLengths,
        Dictionary<(int Start, int Len, int WordId), SuffixCompoundInfo> suffixCompounds,
        Dictionary<string, List<int>> lookups,
        ICandidateProvider candidateProvider)
    {
        var rules = Jiten.Parser.Resolution.Suffixes.All;

        // Index attached-word-id → rules for quick dispatch.
        var attachedIndex = new Dictionary<int, List<Jiten.Parser.Resolution.SuffixRule>>();
        foreach (var rule in rules)
            foreach (int wid in rule.AttachedWordIds)
            {
                if (!attachedIndex.TryGetValue(wid, out var list))
                    attachedIndex[wid] = list = new List<Jiten.Parser.Resolution.SuffixRule>();
                list.Add(rule);
            }

        // Ichiran-style `:stem N` suffix synth. For suffixes where the stem's
        // conjugated form overlaps the attached surface (chau: て+しまう → ちゃう,
        // じゃう/ちまう/じまう), the stem appears in the INPUT truncated by N chars
        // from its conjugation surface. Reconstruct the virtual stem by appending
        // the conjugation-end character ('て' or 'で'), look up candidates, and
        // synth a compound edge covering stem-input + attached length.
        BuildStemStripCompoundEdges(text, sentenceCache, edgesByStart, wordCache, suffixCompounds,
            candidateProvider, attachedIndex);

        // Ichiran load-kf with :text override (dict-grammar.lisp:253):
        //   (load-kf :rou (get-kana-form 1928670 "だろう") :text "ろう")
        // registers a kana form of だろう under the surface "ろう" inside the
        // suffix-cache. The surface "ろう" by itself isn't in JMDict's kana
        // forms for 1928670, so normal lattice lookup won't yield that wordId
        // at pos P when input has "ろう" there. Inject a virtual lattice edge
        // so the main 2-pass loop can pair it via the rou suffix rule.
        InjectVirtualAttachedEdge(text, "ろう", 1928670, edgesByStart, wordCache);

        // Ichiran suffix-sugiru apply-patch for なさ/無さ roots (dict-grammar.lisp:467-479):
        // ない-adjective + sugiru contracts through さ — 知らない + すぎる surfaces as
        // 知らなさすぎる. The stem `知らなさ` is not a real masu-stem; Ichiran's patch
        // replaces the last "さ" with "い" to recover `知らない`, then looks up as a
        // negative conjugation. Emit a synth compound edge with the recovered stem's
        // wordId while the display surface stays 知らなさ.
        BuildSugiruApplyPatchEdges(text, sentenceCache, edgesByStart, wordCache, suffixCompounds, candidateProvider);

        // Ichiran suffix-sou apply-patch for なさ roots (dict-grammar.lisp:438-446):
        // Same pattern as sugiru — 食べない + そう → 食べなさそう; patch strips the
        // trailing さ and substitutes い to recover the base negative, then fires
        // suffix-sou with score_mod=70.
        BuildSouApplyPatchEdges(text, sentenceCache, edgesByStart, wordCache, suffixCompounds, candidateProvider);

        // Ichiran *suffix-unique-only*: for rules flagged UniqueOnly, the compound
        // synth is suppressed when a plain (non-suffix) edge already covers the same
        // (start, totalLen) span. Snapshot pre-synth edge lengths per start so the
        // filter checks the ORIGINAL lattice state, not partial synths.
        var preSynthLengths = new HashSet<int>[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            var set = new HashSet<int>();
            foreach (var (len, _) in edgesByStart[i]) set.Add(len);
            preSynthLengths[i] = set;
        }

        // Snapshot the original (pre-synth) candidate count per (start, len). The
        // attached side of the suffix-synth (`scB`) must only consider ORIGINAL
        // lattice candidates — never synth-added ones — because allowing a
        // synthesized compound to be ATTACHED in another rule produces nonsense
        // chains. Concrete bug it prevents: pass 0's `iadj-me` rule treats `た`
        // (たい adj-stem) + `め` as a (start, 2) compound for たい (2017560);
        // pass 1's `tai` rule then sees that synthesized 2017560 candidate at
        // (start, 2) = `ため` and chains it with stem `い` (居る masu-stem),
        // producing `いため` = 居る+ため — semantically junk that out-scores the
        // real な+い+ため segmentation. Nested passes are intended for STEM
        // re-use only (e.g. 離れすぎて → +いる); attached candidates must be
        // immutable per-edge.
        var originalCandCount = new Dictionary<(int Start, int Len), int>();
        for (int i = 0; i < text.Length; i++)
        {
            foreach (var (len, cands) in edgesByStart[i])
                originalCandCount[(i, len)] = cands.Count;
        }

        // Flatten the original attached-side candidates once. The hot synthesis loop
        // needs only candidates whose wordId participates in some suffix rule.
        var attachedEntriesByStart = new List<SuffixAttachedEntry>?[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            var edges = edgesByStart[i];
            for (int ei = 0; ei < edges.Count; ei++)
            {
                var (len, cands) = edges[ei];
                int limit = originalCandCount.TryGetValue((i, len), out var origCount)
                    ? Math.Min(origCount, cands.Count)
                    : cands.Count;
                for (int ci = 0; ci < limit; ci++)
                {
                    var cand = cands[ci];
                    if (!attachedIndex.TryGetValue(cand.WordId, out var matching)) continue;
                    var bucket = attachedEntriesByStart[i];
                    if (bucket == null)
                        attachedEntriesByStart[i] = bucket = new List<SuffixAttachedEntry>();
                    bucket.Add(new SuffixAttachedEntry(
                        len,
                        cand,
                        matching,
                        HasPoliteChain(cand.ConjugationChain)));
                }
            }
        }

        // Length -> index lookup for each start position. The suffix synth mutates
        // edgesByStart in its hottest loop; O(1) access avoids repeated FindIndex scans.
        var edgeIndexByStart = new Dictionary<int, int>[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            var index = new Dictionary<int, int>();
            var edges = edgesByStart[i];
            for (int ei = 0; ei < edges.Count; ei++)
                index[edges[ei].Length] = ei;
            edgeIndexByStart[i] = index;
        }

        var stemInfoCache = new Dictionary<SurfaceCandidate, SuffixStemCandidateInfo>();
        var saSynthEdges = new HashSet<(int Start, int Len)>();

        // Multiple passes to support nested suffix compounds (離れ+すぎて+いる →
        // 離れすぎている as a single 7-char edge). Pass 1 synthesizes first-
        // layer compounds (e.g. 離れすぎて = 離れ+すぎ+て-form). Pass 2 treats
        // those synths as stems for another rule (+ いる → teiru compound).
        // Pass 3 handles three-level nesting (伸びてこなさそう = te+kuru+sou-nai).
        // EffectiveCompoundChain propagates the attached's chain onto the
        // synth edge so subsequent passes' StemMatches sees the right form.
        for (int pass = 0; pass < 3; pass++)
        for (int startA = 0; startA < text.Length; startA++)
        {
            var edgesA = edgesByStart[startA];
            int edgesACount = edgesA.Count; // snapshot before mutations this pass
            for (int ai = 0; ai < edgesACount; ai++)
            {
                var (lenA, candsA) = edgesA[ai];
                int midPos = startA + lenA;
                if (midPos >= text.Length) continue;
                if (text[startA] is 'っ' or 'ッ') continue; // sokuon never starts a valid stem
                int maxTotal = Math.Min(MaxEdgeLength, text.Length - startA);

                var attachedEntries = attachedEntriesByStart[midPos];
                if (attachedEntries == null || attachedEntries.Count == 0) continue;
                // Sudachi-alignment guard: if a Sudachi token starting at midPos is longer
                // than our attached candidate would be, don't synthesize — we'd be breaking
                // a unit Sudachi deliberately kept whole (e.g., ですか (3) shouldn't be
                // fractured to force a 知れないです+か compound).
                int sudachiLenAtMid = sudachiStartLengths.TryGetValue(midPos, out var _sl) ? _sl : 0;
                for (int aiCand = 0; aiCand < candsA.Count; aiCand++)
                {
                    var scA = candsA[aiCand];
                    if (!stemInfoCache.TryGetValue(scA, out var stemInfo))
                    {
                        stemInfo = BuildSuffixStemCandidateInfo(scA, wordCache);
                        stemInfoCache[scA] = stemInfo;
                    }
                    if (stemInfo.Word == null) continue;
                    if (suffixCompounds.TryGetValue((startA, lenA, scA.WordId), out var existingSfx)
                        && existingSfx.IsEndpointCompound)
                        continue;

                    for (int bi = 0; bi < attachedEntries.Count; bi++)
                    {
                        var attached = attachedEntries[bi];
                        int lenB = attached.Length;
                        int totalLen = lenA + lenB;
                        if (totalLen > maxTotal) continue;
                        if (sudachiLenAtMid > lenB) continue;

                        var scB = attached.Candidate;
                        foreach (var rule in attached.Rules)
                        {
                            // Polite-attached MasuStem guard, now rule-level. Default-blocks
                            // polite-bundled candidates (たいです/たいでしょう) for MasuStem rules
                            // because most fixtures expect the split (観たい|です, not 観たいです).
                            // Per-rule opt-in via AllowPoliteAttached lets specific rules accept
                            // them after audit confirmation. The upstream IsAdjIPoliteSurface gate
                            // in TableCandidateProvider often makes this unreachable anyway —
                            // keep both layers so future provider relaxation doesn't silently
                            // open the floodgates.
                            if (attached.IsPolite
                                && rule.Stem == Jiten.Parser.Resolution.Suffixes.StemType.MasuStem
                                && !rule.AllowPoliteAttached)
                                continue;
                            // Ichiran load-kf :text restriction — rule only fires when
                            // the attached candidate's surface matches exactly.
                            if (rule.AttachedSurface != null && scB.MatchedSurface != rule.AttachedSurface)
                                continue;
                            // Endpoint rules (kuru, iku, oru, etc.) must not fire when the
                            // attached candidate is itself a synthesized compound — prevents
                            // 近づいて(orig) + 来ている(synth-teiru) → 近づいて来ている via kuru.
                            // Stem-strip compounds (IsFromStemStrip=true, e.g. くれてる) are exempt
                            // because they represent real contracted forms, not chained synthesis.
                            if (rule.NoFurtherChain
                                && suffixCompounds.TryGetValue((midPos, lenB, scB.WordId), out var scBSfx)
                                && !scBSfx.IsFromStemStrip)
                                continue;
                            // Stem and attached should be different words — guards against
                            // cases like し (= する, wordId 1157170) pairing with してない
                            // (deconjugates back to wordId 1157170), which would nonsensically
                            // synthesize a する+する compound.
                            if (scA.WordId == scB.WordId) continue;
                            if (!MatchesSuffixStem(rule.Stem, stemInfo)) continue;
                            // Conjugation-based rules (TeForm / MasuStem / NegForm) additionally
                            // require the stem to be a conjugable verb or i-adjective. Without
                            // this the deconjugator can chain spurious tags onto adverbs/nouns
                            // (e.g. なんにもし → 1613730 なんにも[adv] with an "(infinitive)" tag
                            // on the chain), which then wrongly satisfy the MasuStem rule.
                            if (rule.Stem != Jiten.Parser.Resolution.Suffixes.StemType.VsNoun
                                && rule.Stem != Jiten.Parser.Resolution.Suffixes.StemType.Pronoun
                                && !stemInfo.HasConjugablePos)
                                continue;

                            // Ichiran-style compound-existence gate. For rules flagged
                            // with RequiresCompoundInLookup (e.g. gatai), the synth only
                            // fires when stemSurface + attached's primary dict form
                            // resolves via JMdict lookups — i.e. it's a known lexicalised
                            // compound. Prevents false-positives like 楽しみ+がたく →
                            // [楽しみがたく, さん] over 楽しみ+が+たくさん.
                            if (rule.RequiresCompoundInLookup
                                && !CompoundExistsInLookup(text, sentenceCache, startA, lenA, scB.WordId, wordCache, lookups))
                                continue;

                            // Ichiran *suffix-unique-only*: suppress the synth when any
                            // plain (pre-synth) edge of the same totalLen already covers
                            // this start — let the plain word win. Only applies to rules
                            // flagged with UniqueOnly (e.g. nikui, gai).
                            if (rule.UniqueOnly && preSynthLengths[startA].Contains(totalLen))
                                continue;

                            // Ichiran *suffix-unique-only* custom predicate for :desu/:desho:
                            // reject when the stem is じゃない (seq 2755350) or another
                            // blacklisted word — the desu compound should not absorb
                            // idiomatic negative copulas like じゃないですか.
                            if (rule.StemBlacklistWordIds != null
                                && rule.StemBlacklistWordIds.Contains(scA.WordId))
                                continue;

                            var key = (startA, totalLen, scA.WordId);
                            // Synthesize the compound edge in edgesByStart[startA] if not
                            // already present (either by the candidate provider or a prior
                            // suffix-rule synthesis for a different attached candidate).
                            // If the edge with this wordId ALREADY exists (conj-table hit or
                            // direct lookup), it's a legitimate compound form, not a synthesis —
                            // skip suffixCompounds registration so writeback doesn't decompose
                            // a real word into stem + attached halves. Fixes the teiru cluster:
                            // 決まっている resolves to wordId 1591420 (決まる) via the conj table;
                            // without this guard, writeback split it back to [決まって, いる]
                            // because the synth key matched.
                            bool edgeAlreadyCovered = false;
                            var edgeIndex = edgeIndexByStart[startA];
                            if (!edgeIndex.TryGetValue(totalLen, out var existingIdx))
                            {
                                if (rule.BridgeOnly)
                                {
                                    // BridgeOnly: don't expose synthetic edge as standalone.
                                    // Still register in suffixCompounds below for deeper chaining.
                                }
                                else
                                {
                                    edgesA.Add((totalLen, new SurfaceCandidate[]
                                    {
                                        new(scA.WordId, 0, EffectiveCompoundChain(scA, scB), sentenceCache.GetSurface(startA, totalLen))
                                    }));
                                    edgeIndex[totalLen] = edgesA.Count - 1;
                                    if (rule.Name == "sa")
                                        saSynthEdges.Add((startA, totalLen));
                                }
                            }
                            else
                            {
                                var existing = edgesA[existingIdx].Cands;
                                if (existing.Any(c => c.WordId == scA.WordId))
                                {
                                    edgeAlreadyCovered = true;
                                }
                                else if (!rule.BridgeOnly)
                                {
                                    var merged = new SurfaceCandidate[existing.Count + 1];
                                    for (int i = 0; i < existing.Count; i++) merged[i] = existing[i];
                                    merged[existing.Count] = new SurfaceCandidate(
                                        scA.WordId, 0, scA.ConjugationChain, sentenceCache.GetSurface(startA, totalLen));
                                    edgesA[existingIdx] = (totalLen, merged);
                                }
                            }

                            // VsNoun+する patterns are semantically separable (noun stands
                            // alone) — always decompose at writeback even if conj-table
                            // provides the compound edge. Tests like `通用しない` /
                            // `お答えする` encode this two-token expectation. For non-VsNoun
                            // rules (TeForm/MasuStem/NegForm), the compound IS a
                            // legitimate dictionary form when conj-table covers it
                            // (e.g. 決まっている as teiru-of-決まる); keep the score_mod
                            // bonus so the compound can outscore split paths, but mark
                            // EdgeCoveredByLattice so writeback skips decomposition.
                            bool skipDecomposition = edgeAlreadyCovered &&
                                rule.Stem != Jiten.Parser.Resolution.Suffixes.StemType.VsNoun;

                            // When multiple rules apply to the same (start, total, wordId),
                            // keep the biggest score_mod — matches Ichiran's per-rule
                            // composition where a stronger rule dominates.
                            int effectiveScore = rule.Score;
                            if (rule.StemScoreOverrides != null)
                            {
                                var stemSurface = sentenceCache.GetSurface(startA, lenA);
                                if (rule.StemScoreOverrides.TryGetValue(stemSurface, out var ov))
                                    effectiveScore = ov;
                            }
                            if (!suffixCompounds.TryGetValue(key, out var cur) || effectiveScore > cur.ScoreMod)
                            {
                                suffixCompounds[key] = new SuffixCompoundInfo(
                                    ScoreMod: effectiveScore,
                                    StemLen: lenA,
                                    StemWordId: scA.WordId,
                                    AttachedWordId: scB.WordId,
                                    EdgeCoveredByLattice: skipDecomposition,
                                    ScoreIsConstant: rule.ScoreIsConstant,
                                    IsEndpointCompound: rule.NoFurtherChain);
                            }
                        }
                    }
                }
            }
        }

        // Post-pass: suppress sa-synthesized standalone edges that are intermediate
        // stems (feeding into longer compounds). A sa-synth at (start, len) is
        // intermediate if ANY longer edge exists at the same start — whether from
        // suffix synthesis (suffixCompounds) or direct lookup/table (edgesByStart).
        // Terminal sa forms like 楽しさ have no longer edge and stay visible.
        // Intermediate forms like 申し訳なさ (when 申し訳なさそう exists from JMDict)
        // are removed so they can't outscore the deeper compound.
        if (saSynthEdges.Count > 0)
        {
            var saBridged = new HashSet<(int, int)>();
            foreach (var (saStart, saLen) in saSynthEdges)
            {
                foreach (var (edgeLen, _) in edgesByStart[saStart])
                {
                    if (edgeLen > saLen) { saBridged.Add((saStart, saLen)); break; }
                }
            }
            foreach (var (start, len) in saBridged)
            {
                if (preSynthLengths[start].Contains(len)) continue;
                var edges = edgesByStart[start];
                int idx = edges.FindIndex(e => e.Length == len);
                if (idx >= 0) edges.RemoveAt(idx);
            }
        }
    }

    // Ichiran `:stem 1` suffix synth (dict-grammar.lisp:def-simple-suffix suffix-chau).
    // For the chau-family contractions, the INPUT shows stem-truncated-by-1 + attached;
    // the stem's logical conjugation form ends in て or で (one more char than appears
    // in input). We reconstruct the virtual stem surface, query the candidate provider,
    // and if it resolves to a verb whose te-form matches, synth a compound edge covering
    // stem-input + attached with the stem's wordId.
    //
    // The attached surface is located by direct text search (not lattice iteration)
    // because the chau/ちまう/じゃう/じまう surfaces are *skip-words* in Ichiran — they
    // score 0 in prop-scoring and get cut by Phase F. So their lattice edges may be
    // absent even though the suffix synthesis should still fire.
    //
    // Example: `分かっちゃう` (6 chars). Attached `ちゃう` at pos 3-5. Stem in input is
    // `分かっ` (pos 0-2, 3 chars). Virtual stem form = `分かっ` + `て` = `分かって` (4 chars),
    // which maps to wordId 1606560 (分かる) via the conjugation table's te-form entry.
    // Synth edge at (0, 6, 1606560).
    // Each entry: (attached surface as it appears in input, char to append to the stem-input
    // to reconstruct the logical stem (て for ち/と, で for じ/ど), attached WordId, suffix
    // rule index into Suffixes.All for the score / flags).
    //
    // ちゃう/ちまう/じゃう/じまう cover the chau family (suffix-chau, score 5).
    // とく covers the ておく contraction (suffix-to, score 0).
    // Inject a virtual lattice edge for an attached surface that isn't covered
    // by the candidate provider's kana forms. Ichiran's suffix-cache does this
    // via (load-kf :class (get-kana-form SEQ "FULL") :text "SHORT") to register
    // short kana-surface variants for attached auxiliaries.
    private static void InjectVirtualAttachedEdge(
        string text,
        string attachedSurface,
        int attachedWordId,
        List<(int Length, IReadOnlyList<SurfaceCandidate> Cands)>[] edgesByStart,
        WordCacheView wordCache)
    {
        if (!wordCache.ContainsKey(attachedWordId)) return;
        int attachedLen = attachedSurface.Length;
        int pos = 0;
        while (pos < text.Length)
        {
            int found = text.IndexOf(attachedSurface, pos, StringComparison.Ordinal);
            if (found < 0) break;
            pos = found + 1;

            var edges = edgesByStart[found];
            int existingIdx = edges.FindIndex(e => e.Length == attachedLen);
            var virt = new SurfaceCandidate(attachedWordId, 0, null, attachedSurface);
            if (existingIdx < 0)
            {
                edges.Add((attachedLen, new SurfaceCandidate[] { virt }));
            }
            else
            {
                var existing = edges[existingIdx].Cands;
                if (existing.Any(c => c.WordId == attachedWordId)) continue;
                var merged = new SurfaceCandidate[existing.Count + 1];
                for (int i = 0; i < existing.Count; i++) merged[i] = existing[i];
                merged[existing.Count] = virt;
                edges[existingIdx] = (attachedLen, merged);
            }
        }
    }

    private static readonly (string Surface, char Append, int WordId, string RuleName)[] StemStripAttachedMap =
    {
        // :chau suffix — ちゃう/ちまう/じゃう/じまう are v5u verbs; Ichiran matches any
        // conjugation via its conjugation engine. We enumerate the common conjugations.
        ("ちゃう",              'て', 2013800, "chau"),
        ("ちゃった",            'て', 2013800, "chau"),
        ("ちゃって",            'て', 2013800, "chau"),
        ("ちゃえ",              'て', 2013800, "chau"),
        ("ちゃえば",            'て', 2013800, "chau"),
        ("ちゃおう",            'て', 2013800, "chau"),
        ("ちゃわない",          'て', 2013800, "chau"),
        ("ちゃわなかった",      'て', 2013800, "chau"),
        ("ちゃいます",          'て', 2013800, "chau"),
        ("ちゃいました",        'て', 2013800, "chau"),
        ("ちゃいません",        'て', 2013800, "chau"),
        ("ちゃいませんでした",  'て', 2013800, "chau"),
        ("ちゃえる",            'て', 2013800, "chau"),
        ("ちゃえない",          'て', 2013800, "chau"),
        ("ちゃったら",          'て', 2013800, "chau"),
        ("ちゃいたい",          'て', 2013800, "chau"),
        // :chau + :teiru double-contraction — ちゃって (te-form of ちゃう) + いる → ちゃってる
        ("ちゃってる",          'て', 2013800, "chau"),
        ("ちゃってた",          'て', 2013800, "chau"),
        ("ちゃってない",        'て', 2013800, "chau"),
        ("ちゃってなかった",    'て', 2013800, "chau"),
        ("ちゃってます",        'て', 2013800, "chau"),
        ("ちゃってました",      'て', 2013800, "chau"),
        ("ちゃってません",      'て', 2013800, "chau"),
        // じゃう (dakuten variant for mu/nu/bu/gu ending verbs; stem's で absorbed)
        ("じゃう",              'で', 2013800, "chau"),
        ("じゃった",            'で', 2013800, "chau"),
        ("じゃって",            'で', 2013800, "chau"),
        ("じゃえ",              'で', 2013800, "chau"),
        ("じゃえば",            'で', 2013800, "chau"),
        ("じゃおう",            'で', 2013800, "chau"),
        ("じゃわない",          'で', 2013800, "chau"),
        ("じゃわなかった",      'で', 2013800, "chau"),
        ("じゃいます",          'で', 2013800, "chau"),
        ("じゃいました",        'で', 2013800, "chau"),
        ("じゃいません",        'で', 2013800, "chau"),
        ("じゃいませんでした",  'で', 2013800, "chau"),
        ("じゃえる",            'で', 2013800, "chau"),
        ("じゃえない",          'で', 2013800, "chau"),
        ("じゃったら",          'で', 2013800, "chau"),
        ("じゃってる",          'で', 2013800, "chau"),
        ("じゃってた",          'で', 2013800, "chau"),
        ("じゃってない",        'で', 2013800, "chau"),
        ("じゃってなかった",    'で', 2013800, "chau"),
        ("じゃってます",        'で', 2013800, "chau"),
        ("じゃってました",      'で', 2013800, "chau"),
        ("じゃってません",      'で', 2013800, "chau"),
        // ちまう/じまう — less common but same paradigm
        ("ちまう",              'て', 2210750, "chau"),
        ("ちまった",            'て', 2210750, "chau"),
        ("ちまって",            'て', 2210750, "chau"),
        ("ちまえ",              'て', 2210750, "chau"),
        ("ちまえば",            'て', 2210750, "chau"),
        ("ちまおう",            'て', 2210750, "chau"),
        ("じまう",              'で', 2210750, "chau"),
        ("じまった",            'で', 2210750, "chau"),
        ("じまって",            'で', 2210750, "chau"),
        ("じまえ",              'で', 2210750, "chau"),
        ("じまえば",            'で', 2210750, "chau"),
        ("じまおう",            'で', 2210750, "chau"),
        ("とく",              'て', 2108590, "toku"),
        ("といた",            'て', 2108590, "toku"),
        ("といて",            'て', 2108590, "toku"),
        ("とかない",          'て', 2108590, "toku"),
        ("とかなかった",      'て', 2108590, "toku"),
        ("とけ",              'て', 2108590, "toku"),
        ("とけば",            'て', 2108590, "toku"),
        ("とこう",            'て', 2108590, "toku"),
        ("ときます",          'て', 2108590, "toku"),
        ("ときました",        'て', 2108590, "toku"),
        ("ときません",        'て', 2108590, "toku"),
        ("とかなきゃ",        'て', 2108590, "toku"),
        ("てく",  'て', 1578850, "teiku-ct"),
        ("でく",  'で', 1578850, "teiku-ct"),
        // Ichiran :ha class under the :chau handler: ちゃ/じゃ as te+は contractions.
        // `食べちゃ` = `食べて` + は. Stem's て/で is absorbed. WordId 2028920 is は particle.
        ("ちゃ",  'て', 2028920, "chau"),
        ("じゃ",  'で', 2028920, "chau"),
        // Teiru contraction (Ichiran :teiru class — 1-char-stripped kana forms of いる).
        // Stem is te-form; the shared て/で sits in the attached surface. Virtual stem =
        // stemInput + 'て'/'で' reconstructs the real te-form verb text for lookup.
        // WordId 1577980 = いる. Covers the high-frequency conjugated endings of いる:
        // る/た/ない/なかった/ます/ました/ません/ませんでした/れば/なさい.
        ("てる",           'て', 1577980, "teiru"),
        ("でる",           'で', 1577980, "teiru"),
        ("てた",           'て', 1577980, "teiru"),
        ("でた",           'で', 1577980, "teiru"),
        ("てない",          'て', 1577980, "teiru"),
        ("でない",          'で', 1577980, "teiru"),
        ("てなかった",       'て', 1577980, "teiru"),
        ("でなかった",       'で', 1577980, "teiru"),
        ("てます",          'て', 1577980, "teiru"),
        ("でます",          'で', 1577980, "teiru"),
        ("てました",        'て', 1577980, "teiru"),
        ("でました",        'で', 1577980, "teiru"),
        ("てません",        'て', 1577980, "teiru"),
        ("でません",        'で', 1577980, "teiru"),
        ("てませんでした",   'て', 1577980, "teiru"),
        ("でませんでした",   'で', 1577980, "teiru"),
        ("てれば",          'て', 1577980, "teiru"),
        ("でれば",          'で', 1577980, "teiru"),
        ("てなさい",        'て', 1577980, "teiru"),
        ("でなさい",        'で', 1577980, "teiru"),
        // Contracted te-form of いる itself (いて → て with い stripped).
        // Covers てて (= していて → してて), でて (= 読んでいて → 読んでて).
        ("てて",            'て', 1577980, "teiru"),
        ("でて",            'で', 1577980, "teiru"),
        // Ichiran (load-abbr :nai "ねえ" / "ねぇ" / "ねー"): slang contractions of ない.
        // ている + ねえ = て + いない contracted → てねえ. Applied to te-form stem.
        ("てねえ",          'て', 1577980, "teiru"),
        ("でねえ",          'で', 1577980, "teiru"),
        ("てねぇ",          'て', 1577980, "teiru"),
        ("でねぇ",          'で', 1577980, "teiru"),
        ("てねー",          'て', 1577980, "teiru"),
        ("でねー",          'で', 1577980, "teiru"),
    };

    // Pre-pass: for each chau-class attached surface in the input, collect wordIds
    // of virtual-stem candidates (input-stem + て/で) so Phase 2's batch fetch loads
    // them into wordCache before BuildStemStripCompoundEdges needs them.
    private static void CollectStemStripVirtualStemWordIds(
        string text,
        SentenceSurfaceCache sentenceCache,
        ICandidateProvider candidateProvider,
        HashSet<int> allWordIds)
    {
        foreach (var (attachedSurface, appendChar, _, _) in StemStripAttachedMap)
        {
            int attachedLen = attachedSurface.Length;
            int pos = 0;
            while (pos < text.Length)
            {
                int found = text.IndexOf(attachedSurface, pos, StringComparison.Ordinal);
                if (found < 0) break;
                pos = found + 1;
                if (found == 0) continue;
                int maxStemLen = Math.Min(found, MaxEdgeLength - attachedLen);
                for (int stemInputLen = 1; stemInputLen <= maxStemLen; stemInputLen++)
                {
                    int startA = found - stemInputLen;
                    string virtualStem = sentenceCache.GetSurface(startA, stemInputLen) + appendChar;
                    var virtCands = candidateProvider.GetCandidates(virtualStem);
                    if (virtCands == null) continue;
                    foreach (var c in virtCands) allWordIds.Add(c.WordId);
                }
            }
        }
    }

    private static void BuildStemStripCompoundEdges(
        string text,
        SentenceSurfaceCache sentenceCache,
        List<(int Length, IReadOnlyList<SurfaceCandidate> Cands)>[] edgesByStart,
        WordCacheView wordCache,
        Dictionary<(int Start, int Len, int WordId), SuffixCompoundInfo> suffixCompounds,
        ICandidateProvider candidateProvider,
        Dictionary<int, List<Jiten.Parser.Resolution.SuffixRule>> attachedIndex)
    {
        // Build a name → rule lookup of stemStrip rules (so each attached surface fetches
        // the correct score for its corresponding suffix-class, e.g. chau=5 vs toku=0).
        var stemStripRules = new Dictionary<string, Jiten.Parser.Resolution.SuffixRule>(StringComparer.Ordinal);
        foreach (var rule in Jiten.Parser.Resolution.Suffixes.All)
        {
            if (rule.StemStrip > 0 && rule.Stem == Jiten.Parser.Resolution.Suffixes.StemType.TeForm)
                stemStripRules[rule.Name] = rule;
        }
        if (stemStripRules.Count == 0) return;

        foreach (var (attachedSurface, appendChar, attachedWordId, ruleName) in StemStripAttachedMap)
        {
            if (!stemStripRules.TryGetValue(ruleName, out var rule0)) continue;
            int attachedLen = attachedSurface.Length;
            int pos = 0;
            while (pos < text.Length)
            {
                int found = text.IndexOf(attachedSurface, pos, StringComparison.Ordinal);
                if (found < 0) break;
                pos = found + 1;
                int attachedStart = found;
                if (attachedStart == 0) continue; // no room for stem

                int maxStemInputLen = Math.Min(attachedStart, MaxEdgeLength - attachedLen);
                for (int stemInputLen = 1; stemInputLen <= maxStemInputLen; stemInputLen++)
                {
                    int startA = attachedStart - stemInputLen;
                    int totalLen = stemInputLen + attachedLen;

                    string virtualStem = sentenceCache.GetSurface(startA, stemInputLen) + appendChar;
                    var virtCands = candidateProvider.GetCandidates(virtualStem);
                    if (virtCands == null || virtCands.Count == 0) continue;

                    foreach (var stemCand in virtCands)
                    {
                        if (stemCand.WordId == attachedWordId) continue;
                        if (!wordCache.TryGetValue(stemCand.WordId, out var stemWord)) continue;
                        if (!HasConjugablePos(stemWord)) continue;
                        if (!StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.TeForm,
                                stemWord, stemCand.ConjugationChain, stemCand.MatchedSurface))
                            continue;

                        var edgesA = edgesByStart[startA];
                        int existingIdx = edgesA.FindIndex(e => e.Length == totalLen);
                        bool edgeAlreadyCovered = false;
                        if (existingIdx < 0)
                        {
                            edgesA.Add((totalLen, new SurfaceCandidate[]
                            {
                                new(stemCand.WordId, 0, stemCand.ConjugationChain,
                                    sentenceCache.GetSurface(startA, totalLen))
                            }));
                        }
                        else
                        {
                            var existing = edgesA[existingIdx].Cands;
                            if (existing.Any(c => c.WordId == stemCand.WordId))
                            {
                                edgeAlreadyCovered = true;
                            }
                            else
                            {
                                var merged = new SurfaceCandidate[existing.Count + 1];
                                for (int i = 0; i < existing.Count; i++) merged[i] = existing[i];
                                merged[existing.Count] = new SurfaceCandidate(
                                    stemCand.WordId, 0, stemCand.ConjugationChain,
                                    sentenceCache.GetSurface(startA, totalLen));
                                edgesA[existingIdx] = (totalLen, merged);
                            }
                        }

                        var key = (startA, totalLen, stemCand.WordId);
                        // Stem-strip path uses Ichiran's :teiru / :chau / etc. (the
                        // 1-char-stripped attached surface); the full-form regular path
                        // uses :teiru+ which scores higher. StemStripScore = the lower
                        // value (defaults to Score when no differential is configured).
                        int stripScore = rule0.StemStripScore;
                        if (!suffixCompounds.TryGetValue(key, out var cur) || stripScore > cur.ScoreMod)
                        {
                            suffixCompounds[key] = new SuffixCompoundInfo(
                                ScoreMod: stripScore,
                                StemLen: stemInputLen,
                                StemWordId: stemCand.WordId,
                                AttachedWordId: attachedWordId,
                                EdgeCoveredByLattice: edgeAlreadyCovered,
                                ScoreIsConstant: rule0.ScoreIsConstant,
                                IsFromStemStrip: true,
                                ScoreBaseSurface: virtualStem);
                        }
                    }
                }
            }
        }
    }

    // Ichiran suffix-sugiru apply-patch (dict-grammar.lisp:467-479). For input text
    // containing `なさすぎる` or `無さすぎる`, reconstruct the stem by replacing the
    // last `さ` with `い` (→ ない / 無い) and look up that virtual stem as a negative
    // conjugation. The attached surface is すぎる (wordId 1195970). Display surface
    // stays as `〜なさすぎる`; lookup uses the patched form.
    private const int SugiruAttachedWordId = 1195970;
    private static readonly (string Abbr, string Patched)[] SugiruApplyPatchStemEnds =
    {
        ("なさ", "ない"),
        ("無さ", "無い"),
    };

    private static void BuildSugiruApplyPatchEdges(
        string text,
        SentenceSurfaceCache sentenceCache,
        List<(int Length, IReadOnlyList<SurfaceCandidate> Cands)>[] edgesByStart,
        WordCacheView wordCache,
        Dictionary<(int Start, int Len, int WordId), SuffixCompoundInfo> suffixCompounds,
        ICandidateProvider candidateProvider)
    {
        const string Attached = "すぎる";
        int attachedLen = Attached.Length;
        int pos = 0;
        while (pos < text.Length)
        {
            int found = text.IndexOf(Attached, pos, StringComparison.Ordinal);
            if (found < 0) break;
            pos = found + 1;
            if (found < 3) continue; // need at least 1 root char + 2-char な/無+さ

            foreach (var (abbr, patched) in SugiruApplyPatchStemEnds)
            {
                int abbrLen = abbr.Length;
                if (found < abbrLen + 1) continue;
                int abbrStart = found - abbrLen;
                if (!string.Equals(sentenceCache.GetSurface(abbrStart, abbrLen), abbr, StringComparison.Ordinal)) continue;

                // Reconstruct longer virtual stems by walking back from abbrStart.
                int maxRootLen = Math.Min(abbrStart, MaxEdgeLength - attachedLen - abbrLen);
                for (int rootLen = 1; rootLen <= maxRootLen; rootLen++)
                {
                    int startA = abbrStart - rootLen;
                    int stemInputLen = rootLen + abbrLen;
                    int totalLen = stemInputLen + attachedLen;
                    string virtualStem = sentenceCache.GetSurface(startA, rootLen) + patched;
                    var virtCands = candidateProvider.GetCandidates(virtualStem);
                    if (virtCands == null || virtCands.Count == 0) continue;

                    foreach (var stemCand in virtCands)
                    {
                        if (stemCand.WordId == SugiruAttachedWordId) continue;
                        if (!wordCache.TryGetValue(stemCand.WordId, out var stemWord)) continue;
                        // Patched form must look like a negative conjugation — ない
                        // endings on a verb deconjugation. Cheap check: chain contains
                        // a negative tag.
                        if (stemCand.ConjugationChain == null || stemCand.ConjugationChain.Count == 0) continue;
                        bool hasNeg = false;
                        foreach (var tag in stemCand.ConjugationChain)
                            if (tag.Contains("negative", StringComparison.Ordinal)) { hasNeg = true; break; }
                        if (!hasNeg) continue;

                        string displaySurface = sentenceCache.GetSurface(startA, totalLen);
                        var edgesA = edgesByStart[startA];
                        int existingIdx = edgesA.FindIndex(e => e.Length == totalLen);
                        bool edgeAlreadyCovered = false;
                        if (existingIdx < 0)
                        {
                            edgesA.Add((totalLen, new SurfaceCandidate[]
                            {
                                new(stemCand.WordId, 0, stemCand.ConjugationChain, displaySurface)
                            }));
                        }
                        else
                        {
                            var existing = edgesA[existingIdx].Cands;
                            if (existing.Any(c => c.WordId == stemCand.WordId))
                            {
                                edgeAlreadyCovered = true;
                            }
                            else
                            {
                                var merged = new SurfaceCandidate[existing.Count + 1];
                                for (int i = 0; i < existing.Count; i++) merged[i] = existing[i];
                                merged[existing.Count] = new SurfaceCandidate(
                                    stemCand.WordId, 0, stemCand.ConjugationChain, displaySurface);
                                edgesA[existingIdx] = (totalLen, merged);
                            }
                        }

                        var key = (startA, totalLen, stemCand.WordId);
                        if (!suffixCompounds.TryGetValue(key, out var cur) || 5 > cur.ScoreMod)
                        {
                            suffixCompounds[key] = new SuffixCompoundInfo(
                                ScoreMod: 5,
                                StemLen: stemInputLen,
                                StemWordId: stemCand.WordId,
                                AttachedWordId: SugiruAttachedWordId,
                                EdgeCoveredByLattice: edgeAlreadyCovered);
                        }
                    }
                }
            }
        }
    }

    private const int SouAttachedWordId = 1006610;
    private static readonly (string Abbr, string Patched)[] SouApplyPatchStemEnds =
    {
        ("なさ", "ない"),
    };

    private static void BuildSouApplyPatchEdges(
        string text,
        SentenceSurfaceCache sentenceCache,
        List<(int Length, IReadOnlyList<SurfaceCandidate> Cands)>[] edgesByStart,
        WordCacheView wordCache,
        Dictionary<(int Start, int Len, int WordId), SuffixCompoundInfo> suffixCompounds,
        ICandidateProvider candidateProvider)
    {
        const string Attached = "そう";
        int attachedLen = Attached.Length;
        int pos = 0;
        while (pos < text.Length)
        {
            int found = text.IndexOf(Attached, pos, StringComparison.Ordinal);
            if (found < 0) break;
            pos = found + 1;
            if (found < 3) continue; // need at least 1 root char + 2-char な+さ

            foreach (var (abbr, patched) in SouApplyPatchStemEnds)
            {
                int abbrLen = abbr.Length;
                if (found < abbrLen + 1) continue;
                int abbrStart = found - abbrLen;
                if (!string.Equals(sentenceCache.GetSurface(abbrStart, abbrLen), abbr, StringComparison.Ordinal)) continue;

                int maxRootLen = Math.Min(abbrStart, MaxEdgeLength - attachedLen - abbrLen);
                for (int rootLen = 1; rootLen <= maxRootLen; rootLen++)
                {
                    int startA = abbrStart - rootLen;
                    int stemInputLen = rootLen + abbrLen;
                    int totalLen = stemInputLen + attachedLen;
                    string virtualStem = sentenceCache.GetSurface(startA, rootLen) + patched;
                    var virtCands = candidateProvider.GetCandidates(virtualStem);
                    if (virtCands == null || virtCands.Count == 0) continue;

                    foreach (var stemCand in virtCands)
                    {
                        if (stemCand.WordId == SouAttachedWordId) continue;
                        if (!wordCache.TryGetValue(stemCand.WordId, out var stemWord)) continue;
                        if (stemCand.ConjugationChain == null || stemCand.ConjugationChain.Count == 0) continue;
                        bool hasNeg = false;
                        foreach (var tag in stemCand.ConjugationChain)
                            if (tag.Contains("negative", StringComparison.Ordinal)) { hasNeg = true; break; }
                        if (!hasNeg) continue;

                        string displaySurface = sentenceCache.GetSurface(startA, totalLen);
                        var edgesA = edgesByStart[startA];
                        int existingIdx = edgesA.FindIndex(e => e.Length == totalLen);
                        bool edgeAlreadyCovered = false;
                        if (existingIdx < 0)
                        {
                            edgesA.Add((totalLen, new SurfaceCandidate[]
                            {
                                new(stemCand.WordId, 0, stemCand.ConjugationChain, displaySurface)
                            }));
                        }
                        else
                        {
                            var existing = edgesA[existingIdx].Cands;
                            if (existing.Any(c => c.WordId == stemCand.WordId))
                            {
                                edgeAlreadyCovered = true;
                            }
                            else
                            {
                                var merged = new SurfaceCandidate[existing.Count + 1];
                                for (int i = 0; i < existing.Count; i++) merged[i] = existing[i];
                                merged[existing.Count] = new SurfaceCandidate(
                                    stemCand.WordId, 0, stemCand.ConjugationChain, displaySurface);
                                edgesA[existingIdx] = (totalLen, merged);
                            }
                        }

                        var key = (startA, totalLen, stemCand.WordId);
                        if (!suffixCompounds.TryGetValue(key, out var cur) || 70 > cur.ScoreMod)
                        {
                            suffixCompounds[key] = new SuffixCompoundInfo(
                                ScoreMod: 70,
                                StemLen: stemInputLen,
                                StemWordId: stemCand.WordId,
                                AttachedWordId: SouAttachedWordId,
                                EdgeCoveredByLattice: edgeAlreadyCovered);
                        }
                    }
                }
            }
        }
    }

    // Split a suffix-compound segment back into stem + attached WordInfos at writeback.
    // Reuses the original Sudachi WordInfo for either half when boundaries align so we
    // preserve reading/morphology info; otherwise builds synthetic WordInfos from the
    // candidate data.
    private static void EmitSuffixCompoundDecomposition(
        PathSegment seg,
        SuffixCompoundInfo sfx,
        string text,
        Dictionary<(int, int), (WordInfo word, int pos, int len)> sudachiByBoundary,
        WordCacheView wordCache,
        List<(WordInfo word, int position, int length)> replacements)
    {
        int stemStart = seg.Start;
        int stemLen   = sfx.StemLen;
        int attStart  = stemStart + stemLen;
        int attLen    = seg.Len - stemLen;

        // Stem half
        if (sudachiByBoundary.TryGetValue((stemStart, stemLen), out var existingStem))
        {
            replacements.Add(existingStem);
        }
        else
        {
            var stemText = text.Substring(stemStart, stemLen);
            var stemPos = seg.Cand!.Word.CachedPOS.FirstOrDefault(
                p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown), PartOfSpeech.Noun);
            var stemWi = new WordInfo
            {
                Text                       = stemText,
                DictionaryForm             = seg.Cand.Form.Text,
                NormalizedForm             = seg.Cand.Form.Text,
                PartOfSpeech               = stemPos,
                Reading                    = stemText.All(JapaneseTextHelper.IsKana) ? KanaConverter.ToHiragana(stemText) : string.Empty,
                PreMatchedWordId           = sfx.StemWordId,
                PreMatchedCandidateWordIds = new List<int> { sfx.StemWordId },
            };
            replacements.Add((stemWi, stemStart, stemLen));
        }

        // Attached half
        if (sudachiByBoundary.TryGetValue((attStart, attLen), out var existingAtt))
        {
            replacements.Add(existingAtt);
        }
        else
        {
            var attText = text.Substring(attStart, attLen);
            var attPos = PartOfSpeech.Unknown;
            string attDictForm = attText;
            if (wordCache.TryGetValue(sfx.AttachedWordId, out var attWord))
            {
                attPos = attWord.CachedPOS.FirstOrDefault(
                    p => p is not (PartOfSpeech.Name or PartOfSpeech.Unknown), PartOfSpeech.Noun);
                var primary = attWord.Forms.FirstOrDefault();
                if (primary != null && !string.IsNullOrEmpty(primary.Text)) attDictForm = primary.Text;
            }
            var attWi = new WordInfo
            {
                Text                       = attText,
                DictionaryForm             = attDictForm,
                NormalizedForm             = attDictForm,
                PartOfSpeech               = attPos,
                Reading                    = attText.All(JapaneseTextHelper.IsKana) ? KanaConverter.ToHiragana(attText) : string.Empty,
                PreMatchedWordId           = sfx.AttachedWordId,
                PreMatchedCandidateWordIds = new List<int> { sfx.AttachedWordId },
            };
            replacements.Add((attWi, attStart, attLen));
        }
    }

    private static readonly ConcurrentDictionary<(int WordId, string Surface, string MatchedSurface), FormCandidate?>
        _pickFormCache = new();

    private static FormCandidate? PickFormCandidate(JmDictWord word, string surface, SurfaceCandidate sc)
    {
        var cacheKey = (word.WordId, surface, sc.MatchedSurface ?? surface);
        if (_pickFormCache.TryGetValue(cacheKey, out var cached)) return cached;

        var result = PickFormCandidateCore(word, surface, sc);
        _pickFormCache.TryAdd(cacheKey, result);
        return result;
    }

    private static FormCandidate? PickFormCandidateCore(JmDictWord word, string surface, SurfaceCandidate sc)
    {
        var targetHiragana = KanaConverter.ToHiragana(surface, convertLongVowelMark: false);
        var forms = FormCandidateFactory.EnumerateCandidateForms(word, targetHiragana, allowLooseLvmMatch: true, surface: surface);
        if (forms.Count > 0) return forms[0];

        JmDictWordForm? exact = word.Forms.FirstOrDefault(f => f.Text == sc.MatchedSurface);
        if (exact == null && IsAllKana(surface))
            exact = word.Forms.FirstOrDefault(f => f.FormType == JmDictFormType.KanaForm);
        exact ??= word.Forms.FirstOrDefault();
        if (exact == null) return null;
        int ri = exact.ReadingIndex < 0 ? 0 : (exact.ReadingIndex > 255 ? 255 : (int)exact.ReadingIndex);
        return new FormCandidate(word, exact, (byte)ri, targetHiragana);
    }

    // Verb / i-adjective POS gate for suffix-compound synthesis. Kept separate from
    // StemMatches so the adjacency-level suffix bonus (which has different regression
    // sensitivity) isn't affected.
    private static bool HasConjugablePos(JmDictWord word)
    {
        foreach (var p in word.PartsOfSpeech)
            if (p.StartsWith("v") || p == "adj-i" || p == "adj-ix") return true;
        return false;
    }

    // True when the attached edge's chain contains a polite tag (です/ます/でしょう
    // conjugations). Used to block suffix-compound synthesis where the attached
    // auxiliary already bundles the polite tail — the です/ます belongs on its own
    // lattice edge so the split path can win, matching Ichiran's separation of
    // each suffix attachment into its own pass.
    // The synth compound's "form" is dictated by the rightmost (attached)
    // element's conjugation. scA=食べて + scB=いない → compound is te+iru in
    // negative form. scA=離れ + scB=すぎて → compound is sugiru in te-form.
    // Using scA's chain here (masu-stem / te-form of the STEM's base verb)
    // would block nested suffix synthesis: a second-pass teiru rule scanning
    // 離れすぎて checks for "(te form)" and would find scA's "(infinitive)"
    // instead. The attached's chain is what rules higher up the stack need.
    private static IReadOnlyList<string>? EffectiveCompoundChain(SurfaceCandidate scA, SurfaceCandidate scB)
        => scB.ConjugationChain;

    private static bool HasPoliteChain(IReadOnlyList<string>? chain)
    {
        if (chain == null) return false;
        for (int i = 0; i < chain.Count; i++)
        {
            var t = chain[i];
            if (t == "polite" || t == "polite volitional") return true;
        }
        return false;
    }

    private static SuffixStemCandidateInfo BuildSuffixStemCandidateInfo(
        SurfaceCandidate candidate,
        WordCacheView wordCache)
    {
        if (!wordCache.TryGetValue(candidate.WordId, out var word))
            return new SuffixStemCandidateInfo(null, false, SuffixStemMask.None);

        bool hasConjugablePos = HasConjugablePos(word);
        return new SuffixStemCandidateInfo(
            word,
            hasConjugablePos,
            ComputeSuffixStemMask(word, candidate));
    }

    private static SuffixStemMask ComputeSuffixStemMask(JmDictWord word, SurfaceCandidate candidate)
    {
        var mask = SuffixStemMask.None;
        var chain = candidate.ConjugationChain;
        var surface = candidate.MatchedSurface;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.TeForm, word, chain, surface))
            mask |= SuffixStemMask.TeForm;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.VsNoun, word, chain, surface))
            mask |= SuffixStemMask.VsNoun;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.MasuStem, word, chain, surface))
            mask |= SuffixStemMask.MasuStem;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.NegForm, word, chain, surface))
            mask |= SuffixStemMask.NegForm;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.AdjStem, word, chain, surface))
            mask |= SuffixStemMask.AdjStem;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.AdvForm, word, chain, surface))
            mask |= SuffixStemMask.AdvForm;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.SouBase, word, chain, surface))
            mask |= SuffixStemMask.SouBase;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.PastForm, word, chain, surface))
            mask |= SuffixStemMask.PastForm;
        if (StemMatches(Jiten.Parser.Resolution.Suffixes.StemType.Pronoun, word, chain, surface))
            mask |= SuffixStemMask.Pronoun;
        return mask;
    }

    private static bool MatchesSuffixStem(
        Jiten.Parser.Resolution.Suffixes.StemType type,
        SuffixStemCandidateInfo stemInfo)
    {
        var needed = type switch
        {
            Jiten.Parser.Resolution.Suffixes.StemType.TeForm   => SuffixStemMask.TeForm,
            Jiten.Parser.Resolution.Suffixes.StemType.VsNoun   => SuffixStemMask.VsNoun,
            Jiten.Parser.Resolution.Suffixes.StemType.MasuStem => SuffixStemMask.MasuStem,
            Jiten.Parser.Resolution.Suffixes.StemType.NegForm  => SuffixStemMask.NegForm,
            Jiten.Parser.Resolution.Suffixes.StemType.AdjStem  => SuffixStemMask.AdjStem,
            Jiten.Parser.Resolution.Suffixes.StemType.AdvForm  => SuffixStemMask.AdvForm,
            Jiten.Parser.Resolution.Suffixes.StemType.SouBase  => SuffixStemMask.SouBase,
            Jiten.Parser.Resolution.Suffixes.StemType.PastForm => SuffixStemMask.PastForm,
            Jiten.Parser.Resolution.Suffixes.StemType.Pronoun  => SuffixStemMask.Pronoun,
            _ => SuffixStemMask.None,
        };
        return (stemInfo.Mask & needed) != 0;
    }

    // True when every char is hiragana or katakana (no kanji). Used by the
    // kana-form preference in PickFormCandidate's fallback path.
    private static bool IsAllKana(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (char c in s)
        {
            bool hira = c >= 0x3040 && c <= 0x309F;
            bool kata = c >= 0x30A0 && c <= 0x30FF;
            if (!hira && !kata) return false;
        }
        return true;
    }

    // Hiragana-only surface check — used by the gairaigo-hiragana script filter.
    private static bool IsAllHiragana(string text, int start, int len)
    {
        bool sawHiragana = false;
        for (int i = start; i < start + len && i < text.Length; i++)
        {
            char c = text[i];
            // ー (U+30FC) is script-neutral; accept it when accompanied by hiragana
            // so that surfaces like るー correctly reject katakana-only entries (ルー).
            if (c == 'ー') continue;
            if (c < 0x3040 || c > 0x309F) return false;
            sawHiragana = true;
        }
        return sawHiragana;
    }

    // True when every form of the word is katakana-only (no kanji, no hiragana).
    // Gairaigo entries typically have a single katakana form; when hiragana text
    // matches via LVM-expansion (カール → かある) we want to reject.
    private static bool IsKatakanaOnlyEntry(JmDictWord word)
    {
        if (word.Forms == null || word.Forms.Count == 0) return false;
        foreach (var f in word.Forms)
        {
            var t = f.Text;
            if (string.IsNullOrEmpty(t)) continue;
            foreach (char c in t)
            {
                // kanji or hiragana present → not katakana-only
                if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3040 && c <= 0x309F))
                    return false;
            }
        }
        return true;
    }

    // JMnedict-only detection: every POS tag is a proper-noun / name-type. JMdict
    // entries carry real grammatical POS (v5r, adj-i, n, prt, …); JMnedict tags all
    // start with "name-" or belong to a small set mapped to PartOfSpeechSection.Name
    // (see Jiten.Core.Data.PartOfSpeech.cs:169-171). Pure-name entries are the ones
    // Ichiran doesn't have in its lattice at all.
    private static bool IsPureNameEntry(JmDictWord word)
    {
        if (word.PartsOfSpeech == null || word.PartsOfSpeech.Count == 0) return false;
        foreach (var p in word.PartsOfSpeech)
        {
            if (!IsNameTag(p)) return false;
        }
        return true;
    }

    private static bool IsNameTag(string pos) =>
        pos.StartsWith("name-", StringComparison.Ordinal)
        || pos is "name" or "surname" or "given" or "place" or "person" or "product"
               or "ship" or "station" or "company" or "group" or "organization"
               or "unclass" or "char" or "creat" or "dei" or "doc" or "ev"
               or "fem" or "fict" or "leg" or "masc" or "myth" or "obj"
               or "oth" or "relig" or "serv" or "work" or "unc";

    // Ichiran's `find-word-with-conj-type root N` body: the suffix rule only fires
    // when the root has a specific conj-type (3=te-form, 13=ren'youkei, etc.). That
    // filters out deconjugator-artefact chains where the stem's base word doesn't
    // actually support the required conjugation class. We approximate conj-type
    // requirements with JMdict POS:
    //   conj-type 3  (te-form)     ↔ v*/adj-i/adj-ix
    //   conj-type 13 (ren'youkei)  ↔ v*/adj-i/adj-ix  (masu-stem of verb or
    //                                                   adv-form of adj-i)
    //   neg-form                   ↔ v*/adj-i/adj-ix
    private static bool HasVerbOrIAdjPos(JmDictWord stem)
    {
        foreach (var p in stem.PartsOfSpeech)
        {
            if (p.Length >= 1 && p[0] == 'v') return true; // v1, v5*, vk, vs-i, vz, …
            if (p == "adj-i" || p == "adj-ix") return true;
        }
        return false;
    }

    private static bool StemMatches(
        Jiten.Parser.Resolution.Suffixes.StemType type,
        JmDictWord stem,
        IReadOnlyList<string>? conjChain,
        string? matchedSurface = null)
    {
        switch (type)
        {
            case Jiten.Parser.Resolution.Suffixes.StemType.TeForm:
                if (conjChain == null) return false;
                if (!HasVerbOrIAdjPos(stem)) return false; // Ichiran te-check: root has conj-type 3
                foreach (var tag in conjChain)
                    if (tag != null && tag.Contains("(te form)", StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            case Jiten.Parser.Resolution.Suffixes.StemType.VsNoun:
                // Must be a NOUN that takes する — not する itself (vs-i alone) or another verb.
                // The suru suffix rule is for noun+する compounds (安心 + する, 勉強 + する).
                var pos = stem.PartsOfSpeech;
                bool isNoun = pos.Contains("n") || pos.Contains("n-suf") || pos.Contains("n-pref") || pos.Contains("adj-na");
                bool takesSuru = pos.Contains("vs") || pos.Contains("vs-s") || pos.Contains("vs-c");
                return isNoun && takesSuru;
            case Jiten.Parser.Resolution.Suffixes.StemType.MasuStem:
                // Masu-stem / ren'youkei: deconjugator tags it as "(infinitive)" exactly.
                // te-form chains carry "(unstressed infinitive)" as an intermediate, so
                // matching the plain tag (without "unstressed") keeps the classes disjoint.
                // Ichiran also requires root be verb-like (conj-type 13) — enforced via
                // HasVerbOrIAdjPos to reject deconjugator artefacts where the base word
                // isn't actually conjugable (e.g. pronouns or adverbs picking up an
                // infinitive tag through contracted chains).
                if (conjChain == null) return false;
                if (!HasVerbOrIAdjPos(stem)) return false;
                foreach (var tag in conjChain)
                    if (tag == "(infinitive)") return true;
                return false;
            case Jiten.Parser.Resolution.Suffixes.StemType.NegForm:
                if (conjChain == null) return false;
                if (!HasVerbOrIAdjPos(stem)) return false;
                foreach (var tag in conjChain)
                    if (tag != null && tag.Contains("negative", StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            case Jiten.Parser.Resolution.Suffixes.StemType.AdjStem:
                // Ichiran's adj-stem class: the i-less stem of an adj-i word (楽し of 楽しい,
                // 高 of 高い). Our deconjugator tags this as `(stem)` when the chain strips
                // the final い from an adj-i base. Gate on the adj-i POS so we don't confuse
                // with verb stems or other "(stem)" producers.
                if (!IsAdjI(stem)) return false;
                if (conjChain != null)
                {
                    foreach (var tag in conjChain)
                        if (tag == "(stem)") return true;
                }
                // Approach B: deconjugator-independent fallback. Accept when the matched
                // surface plus い equals some Form.Text on the adj-i base — this covers
                // cases where the surface hits via direct lookup of the i-less stem (e.g.
                // 大き of 大きい, 小さ of 小さい) and the deconjugator never emitted a
                // (stem) tag. Gate on HasKanji to avoid false-positives on short kana
                // prefixes shared with non-adj contexts (e.g. から shared with 辛い's
                // kana form while also being a particle reading).
                if (IsStrictAdjStemPrefix(stem, matchedSurface)) return true;
                return false;
            case Jiten.Parser.Resolution.Suffixes.StemType.SouBase:
                // Ichiran's suffix-sou-base: accept masu-stem (conj-type 13), adj-stem,
                // or adverbial-stem paths. Hard reject when matched surface is one of
                // {な, よ, よさ, に, き} — these over-fire on 食べなさそう-style paths
                // where the deconjugator tags a non-sou-eligible piece as infinitive.
                if (matchedSurface != null)
                {
                    switch (matchedSurface)
                    {
                        case "な":
                        case "よ":
                        case "よさ":
                        case "に":
                        case "き":
                            return false;
                    }
                }
                if (conjChain != null)
                {
                    foreach (var tag in conjChain)
                    {
                        if (tag == "(infinitive)" && HasVerbOrIAdjPos(stem)) return true;
                        if (tag == "(stem)" && IsAdjI(stem)) return true;
                        if (tag == "(adverbial stem)" && IsAdjI(stem)) return true;
                    }
                }
                // Prefix fallback for adj-stem path (mirrors AdjStem approach B).
                if (IsStrictAdjStemPrefix(stem, matchedSurface)) return true;
                return false;
            case Jiten.Parser.Resolution.Suffixes.StemType.Pronoun:
                // Ichiran suffix-ra: root POS is "pn" (pronoun) OR seq 1580640 (人).
                // Unless root ends with ら (to prevent double-ら like やつらら).
                if (matchedSurface != null && matchedSurface.EndsWith('ら')) return false;
                return stem.PartsOfSpeech.Contains("pn") || stem.WordId == 1580640;
            case Jiten.Parser.Resolution.Suffixes.StemType.PastForm:
                // Ichiran suffix-rou: (find-word-with-conj-type root 2) — past tense.
                // Our deconjugator tags past as "past".
                if (conjChain == null) return false;
                if (!HasVerbOrIAdjPos(stem)) return false;
                foreach (var tag in conjChain)
                    if (tag == "past") return true;
                return false;
            case Jiten.Parser.Resolution.Suffixes.StemType.AdvForm:
                // Ichiran's adv-form class: adj-i adverbial (高く ← 高い, 楽しく ← 楽しい).
                // Deconjugator tags as `(adverbial stem)`. Used by suffix-naru (become).
                if (!IsAdjI(stem)) return false;
                if (conjChain == null) return false;
                foreach (var tag in conjChain)
                    if (tag == "(adverbial stem)") return true;
                return false;
            default:
                return false;
        }
    }

    private static bool IsAdjI(JmDictWord stem)
    {
        foreach (var p in stem.PartsOfSpeech)
            if (p == "adj-i" || p == "adj-ix") return true;
        return false;
    }

    // Strict adj-stem prefix test: surface + い is one of the adj-i base's Form.Text
    // entries, AND surface contains kanji. Without the kanji gate the fallback fires
    // on hiragana bigrams that happen to be the kana reading of an adj-i stem
    // (e.g. から as 辛's kana form) where the context is actually a particle.
    private static bool IsStrictAdjStemPrefix(JmDictWord stem, string? matchedSurface)
    {
        if (!IsAdjI(stem)) return false;
        if (matchedSurface == null || matchedSurface.Length == 0) return false;
        // Gate on kanji OR pure-katakana: filter out hiragana-only surfaces that
        // tend to also be particle/inflection readings (e.g. から as 辛's kana form
        // but also the particle "because"). Kanji stems like 大き / 小さ and
        // katakana stems like エロ are unambiguous enough to accept.
        if (!ContainsKanji(matchedSurface) && !IsAllKatakana(matchedSurface)) return false;
        foreach (var f in stem.Forms)
        {
            var ft = f.Text;
            if (string.IsNullOrEmpty(ft)) continue;
            if (ft.Length == matchedSurface.Length + 1
                && ft.EndsWith("い", StringComparison.Ordinal)
                && ft.StartsWith(matchedSurface, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static bool ContainsKanji(string s)
    {
        foreach (var ch in s)
            if ((ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF)) return true;
        return false;
    }

    // Compound-existence gate. True if stemSurface + any form text of the attached
    // word appears in the lookups dict (i.e. it's a recognised dict compound).
    private static bool CompoundExistsInLookup(
        string text,
        SentenceSurfaceCache sentenceCache,
        int stemStart,
        int stemLen,
        int attachedWordId,
        WordCacheView wordCache,
        Dictionary<string, List<int>> lookups)
    {
        if (stemLen <= 0 || stemStart + stemLen > text.Length) return false;
        var stemSurface = sentenceCache.GetSurface(stemStart, stemLen);
        if (!wordCache.TryGetValue(attachedWordId, out var attached)) return false;
        foreach (var f in attached.Forms)
        {
            if (string.IsNullOrEmpty(f.Text)) continue;
            var key = stemSurface + f.Text;
            if (lookups.TryGetValue(key, out var ids) && ids.Count > 0) return true;
        }
        return false;
    }

    private static bool IsAllKatakana(string s)
    {
        if (s.Length == 0) return false;
        foreach (var ch in s)
            if (ch < 0x30A0 || ch > 0x30FF) return false;
        return true;
    }

    // Suffix-rule adjacency bonus. Fires when `cand` (the pending segment) is a stem of
    // some rule's class AND `nextCand` (the incoming segment) is one of the rule's
    // attached words. Operates purely at the segmentation-preference layer — the
    // deconjugator already owns surface → lemma morphology; this signal biases the
    // beam toward keeping [stem | aux] pairs as cohesive path steps.
    private static int SuffixAdjacencyBonus(
        FormCandidate cand,
        IReadOnlyList<string>? candConjChain,
        FormCandidate nextCand)
    {
        int total = 0;
        foreach (var rule in Jiten.Parser.Resolution.Suffixes.All)
        {
            if (!rule.AttachedWordIds.Contains(nextCand.Word.WordId)) continue;
            if (!StemMatches(rule.Stem, cand.Word, candConjChain)) continue;
            total += rule.Score;
        }
        return total;
    }

    // Seg-filter: hard-prune invalid adjacencies (Ichiran defsegfilter). If the pair
    // (prev=cand, next=nextCand) matches a rejection rule, return a large negative
    // penalty so the beam drops that path. The penalty is large enough to dominate
    // any plausible positive score accumulation from elsewhere in the sentence.
    private const int SegFilterRejectPenalty = -10000;
    private static int SegFilterPenalty(FormCandidate cand, IReadOnlyList<string>? candConjChain, FormCandidate nextCand, IReadOnlyList<string>? nextConjChain = null)
    {
        int prevId = cand.Word.WordId;
        int nextId = nextCand.Word.WordId;
        string prevText = cand.Form.Text ?? string.Empty;
        string nextText = nextCand.Form.Text ?? string.Empty;
        var compoundEnds = Jiten.Parser.Resolution.Splits.CompoundEndTexts;
        var compoundSeqs = Jiten.Parser.Resolution.Splits.CompoundSeqSets;
        foreach (var rule in Jiten.Parser.Resolution.SegFilters.All)
        {
            // Right-side gate: if set, must match; otherwise rule doesn't apply.
            if (rule.TargetWordIds != null && !rule.TargetWordIds.Contains(nextId)) continue;
            if (rule.RightSurfaceStartsWith != null && !StartsWithAny(nextText, rule.RightSurfaceStartsWith)) continue;
            if (rule.RightCompoundEndText != null && !CompoundEndMatches(nextId, rule.RightCompoundEndText, compoundEnds)) continue;
            // Left-side: all specified conditions must match for rejection (AND).
            if (rule.LeftIs != null && !rule.LeftIs.Contains(prevId)) continue;
            if (rule.LeftIsNot != null && rule.LeftIsNot.Contains(prevId)) continue;
            if (rule.LeftSurfaceEndsWith != null && !EndsWithAny(prevText, rule.LeftSurfaceEndsWith)) continue;
            if (rule.LeftCompoundEndText != null && !CompoundEndMatches(prevId, rule.LeftCompoundEndText, compoundEnds)) continue;
            // Ichiran filter-in-seq-set port: left must be a compound whose piece-set
            // contains ALL wordIds in LeftCompoundSeqIncludes; AND it must contain
            // NONE of the wordIds in LeftCompoundSeqExcludes. Non-compound lefts
            // (no entry in CompoundSeqSets) fail an Includes gate silently — the
            // rule only bites on compound wordIds.
            if (rule.LeftCompoundSeqIncludes != null || rule.LeftCompoundSeqExcludes != null)
            {
                if (!compoundSeqs.TryGetValue(prevId, out var prevSet)) continue;
                if (rule.LeftCompoundSeqIncludes != null)
                {
                    bool allIn = true;
                    foreach (var id in rule.LeftCompoundSeqIncludes)
                        if (!prevSet.Contains(id)) { allIn = false; break; }
                    if (!allIn) continue;
                }
                if (rule.LeftCompoundSeqExcludes != null)
                {
                    bool anyExcluded = false;
                    foreach (var id in rule.LeftCompoundSeqExcludes)
                        if (prevSet.Contains(id)) { anyExcluded = true; break; }
                    if (anyExcluded) continue;
                }
            }
            // LeftStemType's reject trigger is "stem does NOT match", so under AND semantics we skip when it DOES match.
            if (rule.LeftStemType.HasValue && StemMatches(rule.LeftStemType.Value, cand.Word, candConjChain)) continue;
            // Ichiran conj-type gates (see IchiranConjType). Reject only when the specified
            // side's conjugation chain matches the integer conj-type.
            if (rule.LeftConjType.HasValue
                && !Jiten.Parser.Resolution.IchiranConjType.ChainContains(candConjChain, rule.LeftConjType.Value))
                continue;
            if (rule.RightConjType.HasValue
                && !Jiten.Parser.Resolution.IchiranConjType.ChainContains(nextConjChain, rule.RightConjType.Value))
                continue;
            // Structured neg/fml gates (ConjChainAnalysis). Only evaluate if the rule
            // specifies a side's flag — the chain analysis is cheap but not free.
            if (rule.LeftHasNegative.HasValue || rule.LeftHasFormal.HasValue)
            {
                var leftAnalysis = Jiten.Parser.Resolution.ConjChainAnalysis.From(candConjChain);
                if (rule.LeftHasNegative.HasValue && rule.LeftHasNegative.Value != leftAnalysis.HasNegative) continue;
                if (rule.LeftHasFormal.HasValue && rule.LeftHasFormal.Value != leftAnalysis.HasFormal) continue;
            }
            if (rule.RightHasNegative.HasValue || rule.RightHasFormal.HasValue)
            {
                var rightAnalysis = Jiten.Parser.Resolution.ConjChainAnalysis.From(nextConjChain);
                if (rule.RightHasNegative.HasValue && rule.RightHasNegative.Value != rightAnalysis.HasNegative) continue;
                if (rule.RightHasFormal.HasValue && rule.RightHasFormal.Value != rightAnalysis.HasFormal) continue;
            }
            return SegFilterRejectPenalty;
        }
        return 0;
    }

    private static bool CompoundEndMatches(int wordId, string[] endTexts, Dictionary<int, string> compoundEnds)
    {
        if (!compoundEnds.TryGetValue(wordId, out var lastPiece)) return false;
        foreach (var t in endTexts) if (lastPiece == t) return true;
        return false;
    }

    private static bool StartsWithAny(string s, string[] prefixes)
    {
        foreach (var p in prefixes) if (s.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool EndsWithAny(string s, string[] suffixes)
    {
        foreach (var p in suffixes) if (s.EndsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static int EvaluateBeamPairBonusFast(
        BeamTransitionTrait left,
        BeamTransitionTrait right,
        bool serial)
    {
        int segPrune = SegFilterPenaltyFast(left, right);
        int ichiran = EvaluateIchiranPairBonusFast(left, right, serial);
        return segPrune + ichiran;
    }

    private static int EvaluateIchiranPairBonusFast(
        BeamTransitionTrait left,
        BeamTransitionTrait right,
        bool serial)
    {
        int bonus = 0;

        // Ichiran's penalty-short is explicitly non-serial.
        if (left.IsShortKanaNotTo && right.IsShortKanaNotTo)
            bonus -= 9;

        if (!serial) return bonus;

        if (right.IsBuriSuffix && left.IsSubstantiveNoun) bonus += 40;
        if (right.IsToori && left.IsNoParticle) bonus += 50;
        if (right.IsOki && left.IsCounter) bonus += 20;
        if (right.IsTachiSuffix && left.IsSubstantiveNoun) bonus += 10;
        if (right.IsChuSuffix && left.IsSubstantiveNoun) bonus += 12;
        if (right.IsSeiSuffix && left.IsSubstantiveNoun) bonus += 12;
        if (left.IsOPrefix && right.IsOPrefixEligibleNoun) bonus += 10;
        if (left.IsNegationKanjiPrefix && right.IsNounLike) bonus += 15;
        if (left.IsNoOrNnoParticle && right.IsDaDesuDaroo) bonus += 15;
        if (left.IsAdjNo && right.IsNoParticle) bonus += 15;
        if (left.IsNaAdjForIchiran && right.IsNaAdjConnector) bonus += 15;
        if (left.IsShikaParticle && right.IsNegativeConjugation) bonus += 50;
        if (left.EndsWithHa && right.IsShichaIkenai) bonus += 50;
        if (left.IsSemiFinalParticle && !(left.IsShortKanaNotTo && right.IsShortKanaNotTo)) bonus -= 15;
        if (left.IsSubstantiveNounKanjiOrLong && right.IsDaCopula) bonus += 10;
        if (left.IsSou && right.IsNanda) bonus += 50;

        if (left.IsAdverbTo && right.IsToParticleExact)
            bonus += 10 + 10 * left.Text.Length;

        if (left.IsSubstantiveNoun && right.IsIchiranCompoundNounParticle)
            bonus += 10 + 4 * right.Text.Length;

        return bonus;
    }

    private static int SegFilterPenaltyFast(BeamTransitionTrait left, BeamTransitionTrait right)
    {
        foreach (var rule in Jiten.Parser.Resolution.SegFilters.All)
        {
            if (rule.TargetWordIds != null && !rule.TargetWordIds.Contains(right.WordId)) continue;
            if (rule.RightSurfaceStartsWith != null && !StartsWithAny(right.Text, rule.RightSurfaceStartsWith)) continue;
            if (rule.RightCompoundEndText != null && !CompoundEndMatchesFast(right.CompoundEndText, rule.RightCompoundEndText)) continue;
            if (rule.LeftIs != null && !rule.LeftIs.Contains(left.WordId)) continue;
            if (rule.LeftIsNot != null && rule.LeftIsNot.Contains(left.WordId)) continue;
            if (rule.LeftSurfaceEndsWith != null && !EndsWithAny(left.Text, rule.LeftSurfaceEndsWith)) continue;
            if (rule.LeftCompoundEndText != null && !CompoundEndMatchesFast(left.CompoundEndText, rule.LeftCompoundEndText)) continue;

            if (rule.LeftCompoundSeqIncludes != null || rule.LeftCompoundSeqExcludes != null)
            {
                var seqSet = left.CompoundSeqSet;
                if (seqSet == null) continue;

                if (rule.LeftCompoundSeqIncludes != null)
                {
                    bool allIn = true;
                    foreach (var id in rule.LeftCompoundSeqIncludes)
                        if (!seqSet.Contains(id)) { allIn = false; break; }
                    if (!allIn) continue;
                }

                if (rule.LeftCompoundSeqExcludes != null && seqSet.Overlaps(rule.LeftCompoundSeqExcludes))
                    continue;
            }

            if (rule.LeftStemType.HasValue && left.HasStemType(rule.LeftStemType.Value)) continue;
            if (rule.LeftConjType.HasValue && !left.HasConjType(rule.LeftConjType.Value)) continue;
            if (rule.RightConjType.HasValue && !right.HasConjType(rule.RightConjType.Value)) continue;
            if (rule.LeftHasNegative.HasValue && rule.LeftHasNegative.Value != left.HasNegative) continue;
            if (rule.LeftHasFormal.HasValue && rule.LeftHasFormal.Value != left.HasFormal) continue;
            if (rule.RightHasNegative.HasValue && rule.RightHasNegative.Value != right.HasNegative) continue;
            if (rule.RightHasFormal.HasValue && rule.RightHasFormal.Value != right.HasFormal) continue;

            return SegFilterRejectPenalty;
        }

        return 0;
    }

    private static bool CompoundEndMatchesFast(string? compoundEndText, string[] endTexts)
    {
        if (compoundEndText == null) return false;
        foreach (var text in endTexts)
            if (compoundEndText == text)
                return true;
        return false;
    }

    private static int BonusFor(
        FormCandidate cand,
        IReadOnlyList<string>? candConjChain,
        List<PartOfSpeech>? prevPOS,
        string? prevText,
        FormCandidate? nextCand,
        IReadOnlyList<string>? nextConjChain = null)
    {
        var ctx = new AdjacentWordScorer.AdjacentContext(
            PrevResolvedPOS: prevPOS,
            NextResolvedPOS: nextCand?.Word.CachedPOS,
            PrevText: prevText,
            NextText: nextCand?.Form.Text,
            NextConjChain: nextConjChain);
        // SoftRules are Jiten's pre-Ichiran Sudachi-mode adjacency tuning. Disabled
        // in pure-Ichiran mode per the parity directive — they aren't in Ichiran's
        // dict-grammar.lisp, they double-count against IchiranSynergies on shared
        // pairs (noun-particle, na-adj-connector, noun-copula, etc.), and keeping
        // them as a "floor" violates the Ichiran parity goal regardless of any
        // short-term pass-count benefit.
        int halved = 0;
        if (!UseIchiranScoring)
        {
            var (bonus, _) = AdjacentWordScorer.CalculateContextBonus(cand, ctx);
            halved = bonus / 2;
        }
        // Suffix rules in Ichiran mode live entirely as synth compound edges (Phase 2d +
        // score_mod via apply-score-mod). The adjacency bonus is a non-Ichiran fallback
        // tiebreaker for the additive path — gating here prevents double-counting and
        // avoids the score_mod-as-adjacency problem that originally forced the 24×
        // reduction in continuation-6.
        int suffix = (nextCand != null && !UseIchiranScoring)
            ? SuffixAdjacencyBonus(cand, candConjChain, nextCand) : 0;
        int segPrune = nextCand != null ? SegFilterPenalty(cand, candConjChain, nextCand, nextConjChain) : 0;
        // Ichiran-mode §11 synergies — raw additive (no halving) on a distinct channel
        // so the Sudachi-tuned SoftRules stay untouched. Applied only when Ichiran
        // scoring is on; the multiplicative node scale (prop × length-multiplier,
        // ~200-800 per edge) is sized to absorb the small-signal +10/+15/+40 values
        // without being dominated.
        int ichiran = UseIchiranScoring ? AdjacentWordScorer.CalculateIchiranSynergies(cand, ctx) : 0;
        return halved + suffix + segPrune + ichiran;
    }

    private static int BonusForCached(
        BonusCacheTable cache,
        BeamTransitionInterner transitionInterner,
        int leftTraitId,
        int rightTraitId,
        bool serial,
        BeamProfileStats? profile = null)
    {
        if (leftTraitId == 0 || rightTraitId == 0)
            return 0;

        ulong key = transitionInterner.PackKey(leftTraitId, rightTraitId, serial);
        if (profile != null) profile.BonusLookups++;
        if (cache.TryGet(key, out var hit))
        {
            if (profile != null) profile.BonusCacheHits++;
            return hit;
        }
        if (profile != null) profile.BonusCacheMisses++;
        long missStart = profile != null ? Stopwatch.GetTimestamp() : 0;
        var left = transitionInterner.GetTrait(leftTraitId);
        var right = transitionInterner.GetTrait(rightTraitId);
        var value = EvaluateBeamPairBonusFast(left, right, serial);
        if (profile != null) profile.BonusMissTicks += Stopwatch.GetTimestamp() - missStart;
        cache.Set(key, value);
        return value;
    }

    private static int BestFreqBonus(IReadOnlyList<SurfaceCandidate> cands, Dictionary<int, int> frequencyRanks)
    {
        int bestRank = int.MaxValue;
        foreach (var c in cands)
            if (frequencyRanks.TryGetValue(c.WordId, out int r) && r < bestRank) bestRank = r;
        return FreqBonusFromRank(bestRank);
    }

    // Linear length bonus so long compound matches stay competitive with their
    // substring decomposition. Tiered form plateaued at +40 for any segment >=5
    // chars — a 14-char JMDict expression got the same bonus as a 5-char word,
    // while a 4-segment split covering the same chars could each claim +40. Long
    // expressions then lose to their own decomposition. Linear mirrors Ichiran's
    // calc-score multiplying base score by segment length.
    // Ichiran's length-multiplier-coeff table (dict.lisp). Indexed by mora length.
    // :strong (kanji/katakana): 1, 8, 24, 40, 60, then linear ×15
    // :weak   (hiragana):       1, 4, 9, 16, 25, 36, then linear ×7
    // We use the surface text characters as a mora approximation (good enough for
    // segmentation scoring where exact mora count doesn't matter much).
    private static readonly int[] LengthCoeffStrong = { 0, 1, 8, 24, 40, 60 };
    private static readonly int[] LengthCoeffWeak   = { 0, 1, 4, 9, 16, 25, 36 };

    private static int LengthMultiplier(int length, bool kanjiOrKatakana)
    {
        // Zero out len ≤ 2 in additive context — short segments shouldn't earn
        // length bonus, otherwise micro-fragmentation paths accumulate too much.
        if (length <= 2) return 0;
        var table = kanjiOrKatakana ? LengthCoeffStrong : LengthCoeffWeak;
        if (length < table.Length) return table[length];
        int last = table[^1];
        int step = last / (table.Length - 1);
        return length * step;
    }

    private static bool HasKanjiOrKatakana(string text, int start, int len)
    {
        for (int i = start; i < start + len && i < text.Length; i++)
        {
            char c = text[i];
            // Katakana U+30A0–U+30FF, CJK Unified Ideographs U+4E00–U+9FFF
            if ((c >= 0x30A0 && c <= 0x30FF) || (c >= 0x4E00 && c <= 0x9FFF)) return true;
        }
        return false;
    }

    // §10.11 kanji-break positions for a sentence. A position `p ∈ [1, text.Length−1]`
    // is a kanji-break when:
    //   • `text[p−1]` and `text[p]` are both kanji (Ichiran's sequential-kanji-positions
    //     regex `[々一-龯][々一-龯]`), OR
    //   • `p` sits inside an occurrence of a *force-kanji-break* surface.
    // Positions inside a *no-kanji-break* surface are removed even if they satisfy the
    // kanji-kanji rule. An edge incurs the penalty when its start or end boundary is in
    // this set.
    private static HashSet<int> ComputeKanjiBreakPositions(string text, IReadOnlyList<(int Start, int Len)>? parts = null)
    {
        var breaks = new HashSet<int>();
        // Ichiran applies `sequential-kanji-positions` PER PART (dict.lisp:1106), so
        // part boundaries are not breaks even when they sit between two kanji. Without
        // parts we fall back to whole-sentence mode (the pre-Sudachi-aware behaviour).
        if (parts != null && parts.Count > 0)
        {
            foreach (var (ps, pl) in parts)
            {
                int end = ps + pl;
                for (int i = ps; i + 1 < end; i++)
                {
                    if (IsKanjiChar(text[i]) && IsKanjiChar(text[i + 1]))
                        breaks.Add(i + 1);
                }
            }
        }
        else
        {
            for (int i = 0; i + 1 < text.Length; i++)
            {
                if (IsKanjiChar(text[i]) && IsKanjiChar(text[i + 1]))
                    breaks.Add(i + 1);
            }
        }
        foreach (var surf in ForceKanjiBreakSurfaces)
        {
            int idx = 0;
            while ((idx = text.IndexOf(surf, idx, StringComparison.Ordinal)) >= 0)
            {
                for (int j = 1; j < surf.Length; j++) breaks.Add(idx + j);
                idx += 1;
            }
        }
        foreach (var surf in NoKanjiBreakSurfaces)
        {
            int idx = 0;
            while ((idx = text.IndexOf(surf, idx, StringComparison.Ordinal)) >= 0)
            {
                for (int j = 1; j < surf.Length; j++) breaks.Remove(idx + j);
                idx += 1;
            }
        }
        return breaks;
    }

    // Ichiran's `[々一-龯]` regex covers the CJK Unified Ideographs block plus the
    // iteration mark 々. We approximate with the common CJK range; 々 is U+3005 which
    // is outside this range but rare enough in valid kanji-kanji contexts that the
    // approximation holds for current test coverage.
    private static bool IsKanjiChar(char c) => c >= 0x4E00 && c <= 0x9FFF;

    // Port of Ichiran's `find-sticky-positions` (dict.lisp:990).
    //   "words cannot start or end after sokuon and before yoon characters"
    // Rules:
    //   • char[p] is sokuon (っ/ッ), not last char, next char is kana → sticky p+1.
    //   • char[p] is a modifier (small kana ぁぃぅぇぉゃゅょゎ or ー) → sticky p,
    //     EXCEPT when p is the last index AND the modifier is either ー itself or a
    //     small vowel-kana that extends the preceding char's vowel (ね + ぇ → ねぇ).
    // Per-first-char max dictionary/conjugation-table prefix length. 64K slots
    // (U+0000..U+FFFF covers all Japanese text — surrogate-pair chars in names
    // are rare and lose at most MaxEdgeLength-1 candidate edges for those
    // positions). Built once on first use from both `_lookups` (direct JMDict
    // keys) and the ConjugationTable (all conjugated surfaces), then cached
    // for the process. Used by Phase 1 to cap substring enumeration before the
    // Substring/GetCandidates call.
    private static int[]? _maxLenByFirstCharCache;
    private static Dictionary<string, List<int>>? _maxLenCacheLookupsRef;
    private static readonly object _maxLenCacheLock = new();

    private static int[] GetMaxLenByFirstChar(Dictionary<string, List<int>> lookups)
    {
        var cache = _maxLenByFirstCharCache;
        if (cache != null && ReferenceEquals(_maxLenCacheLookupsRef, lookups))
            return cache;

        lock (_maxLenCacheLock)
        {
            cache = _maxLenByFirstCharCache;
            if (cache != null && ReferenceEquals(_maxLenCacheLookupsRef, lookups))
                return cache;

            // Start from the ConjugationTable's pre-computed array (built during
            // binary file parsing at zero extra cost), then merge in lookups.
            // Avoids iterating the 26M-key table index on first beam call (~370ms).
            var ct = Jiten.Parser.Resolution.ConjugationTableLoader.Table;
            int[] t;
            if (ct != null)
            {
                var src = ct.MaxLenByFirstChar;
                t = new int[65536];
                Array.Copy(src, t, 65536);
            }
            else
            {
                t = new int[65536];
            }
            foreach (var key in lookups.Keys)
            {
                if (key.Length == 0) continue;
                int keyLen = key.Length > MaxEdgeLength ? MaxEdgeLength : key.Length;
                char fc = key[0];
                if (keyLen > t[fc]) t[fc] = keyLen;
            }
            // Katakana→hiragana fallback: the candidate provider normalises
            // katakana-only surfaces to their hiragana form for a second
            // lookup. Propagate each hiragana-starter's cap to its katakana
            // counterpart so katakana-starting substrings aren't skipped.
            //   Hiragana U+3041–U+3096 ⟷ Katakana U+30A1–U+30F6  (+0x60)
            for (char h = (char)0x3041; h <= 0x3096; h++)
            {
                char k = (char)(h + 0x60);
                if (t[h] > t[k]) t[k] = t[h];
                if (t[k] > t[h]) t[h] = t[k];
            }
            _maxLenCacheLookupsRef = lookups;
            _maxLenByFirstCharCache = t;
            return t;
        }
    }

    private static HashSet<int> FindStickyPositions(string text)
    {
        var sticky = new HashSet<int>();
        int len = text.Length;
        for (int pos = 0; pos < len; pos++)
        {
            char c = text[pos];
            if (IsSokuonChar(c))
            {
                if (pos + 1 < len && IsKanaChar(text[pos + 1]))
                    sticky.Add(pos + 1);
            }
            else if (IsModifierChar(c))
            {
                bool isLast = pos == len - 1;
                bool longVowelContext = isLast
                    && (c == 'ー' || (pos > 0 && IsLongVowelModifier(c, text[pos - 1])));
                if (!longVowelContext)
                    sticky.Add(pos);
            }
        }
        return sticky;
    }

    private static bool IsSokuonChar(char c) => c == 'っ' || c == 'ッ';

    // Hiragana (U+3040–U+309F) or Katakana (U+30A0–U+30FF). Matches Ichiran's
    // *kana-characters* coverage for the sokuon-plus-kana test.
    private static bool IsKanaChar(char c)
        => (c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF);

    // Ichiran's *modifier-characters*: small vowel kana, small ya/yu/yo, small wa,
    // and the chōonpu (ー). Sokuon is handled separately.
    private static bool IsModifierChar(char c)
        => c is 'ぁ' or 'ァ' or 'ぃ' or 'ィ' or 'ぅ' or 'ゥ' or 'ぇ' or 'ェ' or 'ぉ' or 'ォ'
            or 'ゃ' or 'ャ' or 'ゅ' or 'ュ' or 'ょ' or 'ョ' or 'ゎ' or 'ヮ' or 'ー';

    // Port of `long-vowel-modifier-p` (characters.lisp:47). Small vowel-kana acts as
    // a long-vowel extension when placed after a kana whose vowel matches its own.
    private static bool IsLongVowelModifier(char modifier, char prev)
    {
        char targetVowel = modifier switch
        {
            'ぁ' or 'ァ' => 'a',
            'ぃ' or 'ィ' => 'i',
            'ぅ' or 'ゥ' => 'u',
            'ぇ' or 'ェ' => 'e',
            'ぉ' or 'ォ' => 'o',
            _ => '\0',
        };
        if (targetVowel == '\0') return false;
        return KanaVowel(prev) == targetVowel;
    }

    // Vowel row that the given kana belongs to. Returns '\0' for non-kana or
    // special kana (ん/ン/sokuon/modifiers). Covers voiced and handakuten variants.
    private static char KanaVowel(char c)
    {
        // Normalise basic katakana to hiragana for unified switch below.
        if (c >= 0x30A1 && c <= 0x30F6) c = (char)(c - 0x60);
        return c switch
        {
            'あ' or 'か' or 'が' or 'さ' or 'ざ' or 'た' or 'だ' or 'な'
                or 'は' or 'ば' or 'ぱ' or 'ま' or 'や' or 'ら' or 'わ' => 'a',
            'い' or 'き' or 'ぎ' or 'し' or 'じ' or 'ち' or 'ぢ' or 'に'
                or 'ひ' or 'び' or 'ぴ' or 'み' or 'り' or 'ゐ' => 'i',
            'う' or 'く' or 'ぐ' or 'す' or 'ず' or 'つ' or 'づ' or 'ぬ'
                or 'ふ' or 'ぶ' or 'ぷ' or 'む' or 'ゆ' or 'る' or 'ゔ' => 'u',
            'え' or 'け' or 'げ' or 'せ' or 'ぜ' or 'て' or 'で' or 'ね'
                or 'へ' or 'べ' or 'ぺ' or 'め' or 'れ' or 'ゑ' => 'e',
            'お' or 'こ' or 'ご' or 'そ' or 'ぞ' or 'と' or 'ど' or 'の'
                or 'ほ' or 'ぼ' or 'ぽ' or 'も' or 'よ' or 'ろ' or 'を' => 'o',
            _ => '\0',
        };
    }

    // Legacy linear length bonus (used in fallback paths). Main scoring uses
    // LengthMultiplier × propScore for Ichiran-style multiplicative behavior.
    private static int LengthBonus(int length) => length <= 2 ? 0 : 10 * (length - 2);

private static int ScoreSudachiPath(
        SentenceInfo sentence,
        SentenceSurfaceCache sentenceCache,
        WordCacheView wordCache,
        Dictionary<(int Start, int Len, int WordId), ScoredNode> nodes,
        Dictionary<int, int> frequencyRanks,
        IReadOnlyDictionary<(int Start, int Len), int> sudachiHints,
        Func<WordInfo, int?>? resolvedWordIdLookup,
        BonusCacheTable bonusCache,
        BeamTransitionInterner transitionInterner)
    {
        // Score the sentence with the existing Sudachi tokenisation as if it were a beam path,
        // using the same node + adjacency scoring machinery. If a Sudachi token's
        // (start, len, wordId) isn't in the lattice (its wordId wasn't generated by the
        // candidate provider for that substring), build an ad-hoc ScoredNode on the fly so
        // the baseline gets full FormCandidate + freqBonus + adjacency credit — apples-to-
        // apples comparison with the beam.
        var text = sentence.Text;
        int score = 0;
        int pendingTraitId = 0;
        for (int wi = 0; wi < sentence.Words.Count; wi++)
        {
            var (word, pos, len) = sentence.Words[wi];
            int id = word.PreMatchedWordId
                     ?? resolvedWordIdLookup?.Invoke(word)
                     ?? word.ResolvedWordId
                     ?? 0;
            FormCandidate? cand = null;
            IReadOnlyList<string>? candChain = null;
            int nodeScore;

            if (id != 0 && nodes.TryGetValue((pos, len, id), out var n))
            {
                cand = n.Cand;
                candChain = n.ConjugationChain;
                nodeScore = n.NodeScore;
            }
            else if (id != 0 && wordCache.TryGetValue(id, out var jmWord) && pos + len <= text.Length)
            {
                var surface = sentenceCache.GetSurface(pos, len);
                cand = PickFormCandidate(jmWord, surface, new SurfaceCandidate(id, 0, null, surface));
                if (cand != null)
                {
                    var ctx = FormScoringContext.Create(
                        surface, dictionaryForm: null, normalizedForm: null,
                        isNameContext: false, sudachiReading: null,
                        isArchaicSentence: false,
                        isSentenceInitial: pos == 0,
                        isSentenceFinal: pos + len == text.Length);
                    var trace = FormCandidateScorer.Score(cand, ctx, Parser.ArchaicPosTypes);
                    cand.SetScoreTrace(trace);
                    int kp = 0;
                    if (len == 1 && JapaneseTextHelper.IsKana(text[pos]))
                        kp = IsFunctionalKanaPos(jmWord) ? SingleCharFunctionalKanaPenalty : SingleCharKanaPenalty;
                    if (UseIchiranScoring)
                    {
                        var prop = IchiranPropScorer.Compute(jmWord, cand.Form, surface, conjChain: null,
                            isSentenceFinal: pos + len == text.Length, useLength: null);
                        var cls = IchiranPropScorer.ClassFor(prop.Flags.KanjiP, prop.KatakanaP);
                        int coeff = IchiranPropScorer.LengthMultiplierCoeff(prop.Len, cls);
                        coeff = IchiranPropScorer.ApplyNKanjiBonus(coeff, prop.NKanji);
                        // No kana penalty in Ichiran mode (matches Phase 3 behaviour).
                        nodeScore = prop.Score * coeff;
                    }
                    else
                    {
                        int fb = frequencyRanks.TryGetValue(id, out int r) ? FreqBonusFromRank(r) : 0;
                        int hb = sudachiHints.TryGetValue((pos, len), out var _hbS) ? _hbS : 0;
                        bool isStrong = HasKanjiOrKatakana(text, pos, len);
                        nodeScore = fb + LengthMultiplier(len, isStrong) + hb - kp;
                    }
                }
                else
                {
                    nodeScore = LengthMultiplier(len, HasKanjiOrKatakana(text, pos, len));
                }
            }
            else
            {
                nodeScore = LengthMultiplier(len, HasKanjiOrKatakana(text, pos, len));
            }

            int candTraitId = cand != null ? transitionInterner.GetOrAddTrait(cand, candChain) : 0;
            int finalized = pendingTraitId != 0 && candTraitId != 0
                ? BonusForCached(
                    bonusCache,
                    transitionInterner,
                    pendingTraitId,
                    candTraitId,
                    serial: true)
                : 0;
            score += nodeScore + finalized;
            if (candTraitId != 0)
                pendingTraitId = candTraitId;
        }
        return score;
    }

    private static int FreqBonusFromRank(int rank) => rank switch
    {
        <= 5000  => 10,
        <= 15000 => 5,
        <= 30000 => 2,
        _        => 0
    };

    private static void Push(Dictionary<int, List<BeamState>> beamByPos, int pos, BeamState s)
    {
        if (!beamByPos.TryGetValue(pos, out var bucket))
        {
            bucket = new List<BeamState>(BeamWidth * 2);
            beamByPos[pos] = bucket;
        }
        bucket.Add(s);
    }

    // Port of Ichiran's `find-best-path` (dict.lisp:1190) in segment-anchored form.
    //
    // Unlike the position DP (which advances one char at a time and emits explicit
    // gap edges), this DP iterates over segment-lists sorted by (start, end) and
    // chains non-overlapping segments. Gap spans between chosen segments are
    // priced linearly at -500/char at chaining time; an additional gap-to-end is
    // added when materialising terminal paths. No hard gap cap.
    //
    // State retained per path (see SegPath): the last segment's compact trait id,
    // node score, the accumulated score, and a parent pointer for reconstruction.
    //
    // Returns a terminal list of BeamState shaped exactly like the position DP's
    // terminal list — Segs include synthesized gap entries so downstream writeback
    // logic is unchanged.
    private static List<BeamState> RunSegmentDP(
        string text,
        Dictionary<(int Start, int Len, int WordId), ScoredNode> nodes,
        BonusCacheTable bonusCache,
        BeamTransitionInterner transitionInterner,
        bool collectAllTerminals,
        BeamProfileStats? profile)
    {
        long phaseStart = profile != null ? Stopwatch.GetTimestamp() : 0;
        // Build flat nodeArray (side table for writeback) and DpNode projections
        // grouped into segment-lists keyed by (start, end). The DP hot loop
        // iterates DpNode (value type: score + traitId + index) — no FormCandidate
        // references in the inner loop. Full ScoredNode data is recovered from
        // nodeArray at writeback time for winning paths only.
        var nodeArray = new ScoredNode[nodes.Count];
        var segmentLists = new Dictionary<(int Start, int End), List<DpNode>>();
        int nodeIdx = 0;
        foreach (var ((s, l, _), node) in nodes)
        {
            nodeArray[nodeIdx] = node;
            var dpNode = new DpNode(node.NodeScore, node.TransitionTraitId, nodeIdx, node.AttachedSuffixPenalty);
            nodeIdx++;
            var key = (s, s + l);
            if (!segmentLists.TryGetValue(key, out var list))
                segmentLists[key] = list = new List<DpNode>();
            list.Add(dpNode);
        }
        if (segmentLists.Count == 0) return new List<BeamState>();

        // Sort segment-lists by (start, end) so we process earlier spans first.
        var sortedSLs = new List<(int Start, int End)>(segmentLists.Keys);
        sortedSLs.Sort((a, b) =>
        {
            int c = a.Start.CompareTo(b.Start);
            return c != 0 ? c : a.End.CompareTo(b.End);
        });

        int slCount = sortedSLs.Count;
        var slStarts = new int[slCount];
        var slEnds = new int[slCount];
        var slNodes = new List<DpNode>[slCount];
        for (int i = 0; i < slCount; i++)
        {
            var sl = sortedSLs[i];
            slStarts[i] = sl.Start;
            slEnds[i] = sl.End;
            slNodes[i] = segmentLists[sl];
        }
        if (profile != null)
        {
            profile.DpSegmentLists = slCount;
            profile.DpSpanNodeTotal = nodes.Count;
            for (int i = 0; i < slCount; i++)
                if (slNodes[i].Count > profile.DpMaxNodesPerSpan)
                    profile.DpMaxNodesPerSpan = slNodes[i].Count;
            profile.DpPrepTicks += Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // Pre-compute max node score per segment-list for score-bound pruning.
        var maxNodeScores = new int[slCount];
        for (int i = 0; i < slCount; i++)
        {
            int max = int.MinValue;
            foreach (var node in slNodes[i])
                if (node.NodeScore > max) max = node.NodeScore;
            maxNodeScores[i] = max;
        }

        // Top-K paths ending at each segment-list.
        var topBySL = new SegPathTopArray?[slCount];
        var pathList = new List<SegPathEntry>(Math.Max(nodes.Count * 4, slCount * SegmentTopK));

        int gapPen = Constants.UncoveredCharPenalty;
        int gap(int from, int to) => -gapPen * (to - from);

        // Seed: initial paths for every segment-list (gap from 0 to SL.start + node score).
        for (int i = 0; i < slCount; i++)
        {
            int gapLeft = gap(0, slStarts[i]);
            int seedGapChars = slStarts[i];
            foreach (var node in slNodes[i])
            {
                int score = gapLeft + node.NodeScore;
                int pathIdx = pathList.Count;
                pathList.Add(new SegPathEntry
                {
                    LastNodeScore = node.NodeScore,
                    LastTransitionTraitId = node.TransitionTraitId,
                    LastNodeIndex = node.NodeIndex,
                    Score = score,
                    ParentIndex = -1,
                    SegStart = (short)slStarts[i],
                    SegLen = (short)(slEnds[i] - slStarts[i]),
                    GapChars = seedGapChars
                });
                if (profile != null)
                {
                    profile.DpSeedPaths++;
                    profile.DpPathStatesAllocated++;
                }
                AddToTop(topBySL, i, pathIdx, score, profile);
            }
        }
        if (profile != null)
        {
            profile.DpSeedTicks += Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // Extend: for each SL1 followed by every non-overlapping SL2, chain SL1's
        // top paths into new paths ending at SL2.
        for (int i = 0; i < slCount; i++)
        {
            var paths1 = topBySL[i];
            if (paths1 == null || paths1.Count == 0) continue;

            int sl1End = slEnds[i];
            int bestP1Score = pathList[paths1[0]].Score;
            int firstJ = LowerBoundStart(slStarts, sl1End, i + 1);
            for (int j = firstJ; j < slCount; j++)
            {
                int gapBetweenChars = slStarts[j] - sl1End;
                if (gapBetweenChars > MaxDpGapChars) break;
                if (profile != null) profile.DpCompatibleSpanPairs++;
                int gapBetween = -gapPen * gapBetweenChars;

                int optimistic = bestP1Score + maxNodeScores[j] + MaxSynergyBound + gapBetween;
                if (!WouldAcceptIntoTop(topBySL, j, optimistic))
                {
                    if (profile != null) profile.DpTopRejected++;
                    continue;
                }

                foreach (var node2 in slNodes[j])
                {
                    for (int pi = 0; pi < paths1.Count; pi++)
                    {
                        int p1Idx = paths1[pi];
                        ref var p1 = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pathList)[p1Idx];
                        if (profile != null) profile.DpTransitionAttempts++;
                        bool serial = gapBetweenChars == 0;
                        int synergy = BonusForCached(
                            bonusCache,
                            transitionInterner,
                            p1.LastTransitionTraitId,
                            node2.TransitionTraitId,
                            serial,
                            profile);

                        if (UseIchiranScoring && synergy <= SegFilterRejectPenalty / 2)
                        {
                            if (profile != null) profile.DpSegfilterRejects++;
                            continue;
                        }

                        int effectiveNode2Score = node2.NodeScore;

                        int newScore;
                        if (UseIchiranScoring)
                        {
                            int pairSum = p1.LastNodeScore + synergy + effectiveNode2Score;
                            int pairFloorLeft = 1 + p1.LastNodeScore;
                            int pairFloorRight = 1 + effectiveNode2Score;
                            int pair = Math.Max(pairSum, Math.Max(pairFloorLeft, pairFloorRight));
                            newScore = p1.Score - p1.LastNodeScore + pair + gapBetween;
                        }
                        else
                        {
                            newScore = p1.Score + gapBetween + synergy + effectiveNode2Score;
                        }

                        if (!WouldAcceptIntoTop(topBySL, j, newScore))
                        {
                            if (profile != null) profile.DpTopRejected++;
                            continue;
                        }

                        int newPathIdx = pathList.Count;
                        pathList.Add(new SegPathEntry
                        {
                            LastNodeScore = effectiveNode2Score,
                            LastTransitionTraitId = node2.TransitionTraitId,
                            LastNodeIndex = node2.NodeIndex,
                            Score = newScore,
                            ParentIndex = p1Idx,
                            SegStart = (short)slStarts[j],
                            SegLen = (short)(slEnds[j] - slStarts[j]),
                            GapChars = p1.GapChars + gapBetweenChars
                        });
                        if (profile != null)
                        {
                            profile.DpTransitionAccepted++;
                            profile.DpPathStatesAllocated++;
                        }
                        AddToTop(topBySL, j, newPathIdx, newScore, profile);
                    }
                }
            }
        }
        if (profile != null)
        {
            profile.DpExtendTicks += Stopwatch.GetTimestamp() - phaseStart;
            phaseStart = Stopwatch.GetTimestamp();
        }

        // Materialise terminals: each path's total score adds only the gap-to-end.
        // Materialise terminals: resolve compact SegPath back to full FormCandidate
        // via nodeArray, eagerly reconstruct segment lists so downstream code
        // (writeback, diagnostics) sees pre-populated Segs without needing nodeArray.
        //
        // When collectAllTerminals is false (production path), find the best score
        // first via a cheap arithmetic scan, then reconstruct only the winning path.
        // This avoids O(slCount × topK) ReconstructSegs calls — the dominant cost
        // in the final phase for large sentences.
        var terminals = new List<BeamState>(collectAllTerminals ? slCount : 1);
        BeamState? bestTerminal = null;
        if (collectAllTerminals)
        {
            for (int i = 0; i < slCount; i++)
            {
                var paths = topBySL[i];
                if (paths == null || paths.Count == 0) continue;
                int gapRightChars = text.Length - slEnds[i];
                int gapRight = -gapPen * gapRightChars;
                for (int pi = 0; pi < paths.Count; pi++)
                {
                    int pIdx = paths[pi];
                    ref var p = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pathList)[pIdx];
                    int finalScore = p.Score + gapRight;
                    var lastNode = nodeArray[p.LastNodeIndex];

                    var bs = new BeamState(
                        PendingCand: lastNode.Cand,
                        PendingConjChain: lastNode.ConjugationChain,
                        PendingNodeScore: p.LastNodeScore,
                        PendingTransitionTraitId: p.LastTransitionTraitId,
                        PendingGapChars: 0,
                        Score: finalScore,
                        GapChars: p.GapChars + gapRightChars,
                        Segs: ReconstructSegs(pIdx, pathList, text.Length, nodeArray));
                    bs.FinalScore = finalScore;
                    if (profile != null) profile.DpTerminalPaths++;
                    terminals.Add(bs);
                }
            }
        }
        else
        {
            int bestScore = int.MinValue;
            int bestPIdx = -1;
            int bestGapRightChars = 0;
            int bestLastNodeIdx = -1;
            int bestLastNodeScore = 0;
            int bestLastTraitId = 0;
            int bestGapChars = 0;
            for (int i = 0; i < slCount; i++)
            {
                var paths = topBySL[i];
                if (paths == null || paths.Count == 0) continue;
                int gapRightChars = text.Length - slEnds[i];
                int gapRight = -gapPen * gapRightChars;
                for (int pi = 0; pi < paths.Count; pi++)
                {
                    int pIdx = paths[pi];
                    ref var p = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pathList)[pIdx];
                    int finalScore = p.Score + gapRight;
                    if (finalScore > bestScore)
                    {
                        bestScore = finalScore;
                        bestPIdx = pIdx;
                        bestGapRightChars = gapRightChars;
                        bestLastNodeIdx = p.LastNodeIndex;
                        bestLastNodeScore = p.LastNodeScore;
                        bestLastTraitId = p.LastTransitionTraitId;
                        bestGapChars = p.GapChars;
                    }
                }
            }
            if (bestPIdx >= 0)
            {
                var lastNode = nodeArray[bestLastNodeIdx];
                bestTerminal = new BeamState(
                    PendingCand: lastNode.Cand,
                    PendingConjChain: lastNode.ConjugationChain,
                    PendingNodeScore: bestLastNodeScore,
                    PendingTransitionTraitId: bestLastTraitId,
                    PendingGapChars: 0,
                    Score: bestScore,
                    GapChars: bestGapChars + bestGapRightChars,
                    Segs: ReconstructSegs(bestPIdx, pathList, text.Length, nodeArray));
                bestTerminal.FinalScore = bestScore;
            }
        }
        if (!collectAllTerminals && bestTerminal != null)
        {
            if (profile != null) profile.DpTerminalPaths++;
            terminals.Add(bestTerminal);
        }
        if (profile != null)
            profile.DpFinalTicks += Stopwatch.GetTimestamp() - phaseStart;
        return terminals;
    }

    private static void AddToTop(
        SegPathTopArray?[] topBySL,
        int slIndex,
        int pathIndex,
        int score,
        BeamProfileStats? profile)
    {
        var bucket = topBySL[slIndex];
        if (bucket == null)
        {
            bucket = new SegPathTopArray(SegmentTopK);
            topBySL[slIndex] = bucket;
        }
        bucket.Add(pathIndex, score, profile);
    }

    private static bool WouldAcceptIntoTop(SegPathTopArray?[] topBySL, int slIndex, int score)
    {
        var bucket = topBySL[slIndex];
        return bucket == null || bucket.WouldAccept(score);
    }

    private static int LowerBoundStart(int[] starts, int value, int lo)
    {
        int hi = starts.Length;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (starts[mid] < value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static List<PathSegment> ReconstructSegs(int tailIdx, List<SegPathEntry> pathList, int textLength, ScoredNode[] nodeArray)
    {
        var rev = new List<PathSegment>();
        for (int idx = tailIdx; idx >= 0;)
        {
            ref var cur = ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pathList)[idx];
            var fullNode = nodeArray[cur.LastNodeIndex];
            rev.Add(new PathSegment(cur.SegStart, cur.SegLen, fullNode.Cand, fullNode.ConjugationChain));
            idx = cur.ParentIndex;
        }
        rev.Reverse();

        var result = new List<PathSegment>(rev.Count);
        int cursor = 0;
        foreach (var seg in rev)
        {
            while (cursor < seg.Start)
            {
                result.Add(new PathSegment(cursor, 1, null, null));
                cursor++;
            }
            result.Add(seg);
            cursor = seg.Start + seg.Len;
        }
        while (cursor < textLength)
        {
            result.Add(new PathSegment(cursor, 1, null, null));
            cursor++;
        }
        return result;
    }

    private static void EnsureSegmentsMaterialized(BeamState state, int textLength)
    {
        if (state.Segs.Count > 0) return;
    }

    private struct SegPathEntry
    {
        public int LastNodeScore;
        public int LastTransitionTraitId;
        public int LastNodeIndex;
        public int Score;
        public int ParentIndex;
        public short SegStart;
        public short SegLen;
        public int GapChars;
    }

    private sealed class SegPathTopArray
    {
        private readonly int[] _items;
        private readonly int[] _scores;
        private int _count;

        public SegPathTopArray(int limit)
        {
            _items = new int[Math.Max(1, limit)];
            _scores = new int[Math.Max(1, limit)];
        }

        public int Count => _count;

        public int this[int index] => _items[index];

        public bool WouldAccept(int score)
            => _count < _items.Length || _scores[_items.Length - 1] < score;

        public bool Add(int pathIndex, int score, BeamProfileStats? profile)
        {
            int limit = _items.Length;
            int idx = Math.Min(_count, limit);
            while (idx > 0)
            {
                if (_scores[idx - 1] >= score) break;
                if (idx < limit) { _items[idx] = _items[idx - 1]; _scores[idx] = _scores[idx - 1]; }
                idx--;
            }

            if (idx >= limit)
            {
                if (profile != null) profile.DpTopRejected++;
                return false;
            }
            bool evicted = _count == limit;
            _items[idx] = pathIndex;
            _scores[idx] = score;
            if (_count < limit) _count++;
            if (profile != null)
            {
                profile.DpTopAccepted++;
                if (evicted) profile.DpTopEvicted++;
            }
            return true;
        }
    }

    private readonly record struct TransitionTraitKey(int WordId, int TextId, int ConjChainId);
    private readonly record struct ConjChainKey(IReadOnlyList<string>? Chain);

    private sealed record BeamTransitionTrait(
        string Text,
        int WordId,
        ulong ConjTypeMask,
        int StemTypeMask,
        HashSet<int>? CompoundSeqSet,
        string? CompoundEndText,
        bool HasNegative,
        bool HasFormal,
        bool HasKanji,
        bool IsCounter,
        bool EndsWithHa,
        bool IsNounLike,
        bool IsSubstantiveNoun,
        bool IsSubstantiveNounKanjiOrLong,
        bool IsShortKanaNotTo,
        bool IsOPrefix,
        bool IsNegationKanjiPrefix,
        bool IsBuriSuffix,
        bool IsToori,
        bool IsOki,
        bool IsTachiSuffix,
        bool IsChuSuffix,
        bool IsSeiSuffix,
        bool IsNoOrNnoParticle,
        bool IsAdjNo,
        bool IsNaAdjForIchiran,
        bool IsShikaParticle,
        bool IsSemiFinalParticle,
        bool IsSou,
        bool IsNoun,
        bool IsNoParticle,
        bool IsDaCopula,
        bool IsDaDesuDaroo,
        bool IsNaAdjConnector,
        bool IsNegativeConjugation,
        bool IsShichaIkenai,
        bool IsIchiranCompoundNounParticle,
        bool IsToParticleExact,
        bool IsNanda,
        bool IsOPrefixEligibleNoun,
        bool IsAdverbTo,
        bool IsMixedSuffixVerb,
        bool IsParticle)
    {
        public bool HasConjType(int conjType) =>
            conjType >= 0 && conjType < 64 && ((ConjTypeMask >> conjType) & 1UL) != 0;

        public bool HasStemType(Jiten.Parser.Resolution.Suffixes.StemType stemType) =>
            (StemTypeMask & (1 << (int)stemType)) != 0;
    }

    private sealed class ConjChainKeyComparer : IEqualityComparer<ConjChainKey>
    {
        public bool Equals(ConjChainKey x, ConjChainKey y)
        {
            if (ReferenceEquals(x.Chain, y.Chain)) return true;
            var xc = x.Chain;
            var yc = y.Chain;
            if (xc == null || yc == null) return xc == yc;
            if (xc.Count != yc.Count) return false;
            for (int i = 0; i < xc.Count; i++)
                if (!string.Equals(xc[i], yc[i], StringComparison.Ordinal))
                    return false;
            return true;
        }

        public int GetHashCode(ConjChainKey obj)
        {
            var chain = obj.Chain;
            if (chain == null) return 0;
            var hash = new HashCode();
            for (int i = 0; i < chain.Count; i++)
                hash.Add(chain[i], StringComparer.Ordinal);
            return hash.ToHashCode();
        }
    }

    private sealed class BeamTransitionInterner
    {
        private static readonly uint NounLikePosMask = PosMask(
            PartOfSpeech.Noun,
            PartOfSpeech.CommonNoun,
            PartOfSpeech.NaAdjective,
            PartOfSpeech.Pronoun,
            PartOfSpeech.Name,
            PartOfSpeech.NominalAdjective);
        private static readonly uint SuffixLikePosMask = PosMask(PartOfSpeech.Suffix, PartOfSpeech.NounSuffix);
        private static readonly Jiten.Parser.Resolution.Suffixes.StemType[] TransitionStemTypes =
        [
            Jiten.Parser.Resolution.Suffixes.StemType.TeForm,
            Jiten.Parser.Resolution.Suffixes.StemType.VsNoun,
            Jiten.Parser.Resolution.Suffixes.StemType.MasuStem,
            Jiten.Parser.Resolution.Suffixes.StemType.NegForm,
            Jiten.Parser.Resolution.Suffixes.StemType.AdjStem,
            Jiten.Parser.Resolution.Suffixes.StemType.AdvForm,
            Jiten.Parser.Resolution.Suffixes.StemType.SouBase,
            Jiten.Parser.Resolution.Suffixes.StemType.PastForm,
            Jiten.Parser.Resolution.Suffixes.StemType.Pronoun,
        ];

        private readonly Dictionary<string, int> _textIds = new(StringComparer.Ordinal);
        private readonly Dictionary<ConjChainKey, int> _conjChainIds = new(new ConjChainKeyComparer());
        private readonly Dictionary<TransitionTraitKey, int> _traitIds = new();
        private readonly List<BeamTransitionTrait?> _traits = new() { null };

        public int GetOrAddTrait(FormCandidate cand, IReadOnlyList<string>? conjChain)
        {
            string text = cand.Form.Text;
            int textId = GetOrAddTextId(text);
            int conjChainId = GetOrAddConjChainId(conjChain);
            var key = new TransitionTraitKey(cand.Word.WordId, textId, conjChainId);
            if (_traitIds.TryGetValue(key, out var id))
                return id;

            int wordId = cand.Word.WordId;
            uint posMask = ToPosMask(cand.Word.CachedPOS);
            bool hasKanji = HasTransitionKanji(text);
            bool isNounLike = HasAnyPos(posMask, NounLikePosMask);
            bool isSubstantiveNoun = isNounLike && (hasKanji || text.Length >= 2);
            bool isSubstantiveNounKanjiOrLong = isNounLike && (hasKanji || text.Length >= 3);
            bool isSuffixLike = HasAnyPos(posMask, SuffixLikePosMask);
            bool isMixedSuffixVerb = isSuffixLike && HasConjugableVerbPos(cand.Word.PartsOfSpeech);
            bool isParticle = HasPos(posMask, PartOfSpeech.Particle);
            bool isAuxiliary = HasPos(posMask, PartOfSpeech.Auxiliary);
            bool isExpression = HasPos(posMask, PartOfSpeech.Expression);
            bool isPrefix = HasPos(posMask, PartOfSpeech.Prefix);
            bool isAdverbTo = HasPos(posMask, PartOfSpeech.AdverbTo);
            bool isCounter = HasPos(posMask, PartOfSpeech.Counter);
            bool isNoun = HasPos(posMask, PartOfSpeech.Noun);

            var wordPos = cand.Word.PartsOfSpeech;
            var analysis = Jiten.Parser.Resolution.ConjChainAnalysis.From(conjChain);
            ulong conjTypeMask = ToConjTypeMask(conjChain);
            int stemTypeMask = ComputeStemTypeMask(cand, conjChain);
            HashSet<int>? compoundSeqSet = Jiten.Parser.Resolution.Splits.CompoundSeqSets.TryGetValue(wordId, out var seqSet) ? seqSet : null;
            string? compoundEndText = Jiten.Parser.Resolution.Splits.CompoundEndTexts.TryGetValue(wordId, out var compoundEnd) ? compoundEnd : null;

            bool isAdjNo = wordPos.Contains("adj-no");
            bool isNaAdjForIchiran = wordPos.Contains("adj-na") || HasPos(posMask, PartOfSpeech.NaAdjective);
            bool isSemiFinalParticle = TransitionRuleSets.SemiFinalPrtSeqs.Contains(wordId)
                || (compoundSeqSet != null && compoundSeqSet.Overlaps(TransitionRuleSets.SemiFinalPrtSeqs));
            bool isNegativeConjugation = analysis.HasNegative || IsNegativeSurfaceText(text);
            bool isIchiranCompoundNounParticle = (isParticle || isAuxiliary || isExpression)
                && TransitionRuleSets.IchiranCompoundNounParticles.Contains(text);
            bool isNoParticle = text == "の" && isParticle;
            bool isDaCopula = text == "だ" && isAuxiliary;
            bool isToParticleExact = text == "と" && isParticle;

            id = _traits.Count;
            _traits.Add(new BeamTransitionTrait(
                Text: text,
                WordId: wordId,
                ConjTypeMask: conjTypeMask,
                StemTypeMask: stemTypeMask,
                CompoundSeqSet: compoundSeqSet,
                CompoundEndText: compoundEndText,
                HasNegative: analysis.HasNegative,
                HasFormal: analysis.HasFormal,
                HasKanji: hasKanji,
                IsCounter: isCounter,
                EndsWithHa: text is { Length: > 0 } h && h[^1] == 'は',
                IsNounLike: isNounLike,
                IsSubstantiveNoun: isSubstantiveNoun,
                IsSubstantiveNounKanjiOrLong: isSubstantiveNounKanjiOrLong,
                IsShortKanaNotTo: IsShortKanaNotToText(text),
                IsOPrefix: isPrefix && TransitionRuleSets.OPrefixes.Contains(text),
                IsNegationKanjiPrefix: isPrefix && TransitionRuleSets.NegationKanjiPrefixes.Contains(text),
                IsBuriSuffix: isSuffixLike && text == TransitionRuleSets.BuriSuffix,
                IsToori: text == "通り",
                IsOki: text is "おき" or "置き",
                IsTachiSuffix: isSuffixLike && (text == "たち" || text == "達"),
                IsChuSuffix: isSuffixLike && (text == "中" || text == "ちゅう"),
                IsSeiSuffix: isSuffixLike && text == "性",
                IsNoOrNnoParticle: isParticle && (text == "の" || text == "ん"),
                IsAdjNo: isAdjNo,
                IsNaAdjForIchiran: isNaAdjForIchiran,
                IsShikaParticle: isParticle && text == "しか",
                IsSemiFinalParticle: isSemiFinalParticle,
                IsSou: text == "そう",
                IsNoun: isNoun,
                IsNoParticle: isNoParticle,
                IsDaCopula: isDaCopula,
                IsDaDesuDaroo: TransitionRuleSets.NoDaCopulas.Contains(text),
                IsNaAdjConnector: TransitionRuleSets.NaAdjConnectors.Contains(text),
                IsNegativeConjugation: isNegativeConjugation,
                IsShichaIkenai: TransitionRuleSets.ShichaIkenaiRightTexts.Contains(text),
                IsIchiranCompoundNounParticle: isIchiranCompoundNounParticle,
                IsToParticleExact: isToParticleExact,
                IsNanda: text == "なんだ",
                IsOPrefixEligibleNoun: isNoun && (hasKanji || text.Length >= 4),
                IsAdverbTo: isAdverbTo,
                IsMixedSuffixVerb: isMixedSuffixVerb,
                IsParticle: isParticle));
            _traitIds[key] = id;
            return id;
        }

        public BeamTransitionTrait GetTrait(int id) => _traits[id]!;

        public ulong PackKey(int leftTraitId, int rightTraitId, bool serial)
        {
            if (leftTraitId < 0 || rightTraitId < 0)
                throw new InvalidOperationException("Beam transition ids must be non-negative.");

            return ((ulong)(serial ? 1 : 0) << 62)
                   | ((ulong)(uint)leftTraitId << 31)
                   | (uint)rightTraitId;
        }

        private int GetOrAddTextId(string? text)
        {
            if (text == null) return 0;
            if (_textIds.TryGetValue(text, out var id))
                return id;
            id = _textIds.Count + 1;
            _textIds[text] = id;
            return id;
        }

        private int GetOrAddConjChainId(IReadOnlyList<string>? chain)
        {
            if (chain == null || chain.Count == 0) return 0;

            var key = new ConjChainKey(chain);
            if (_conjChainIds.TryGetValue(key, out var id))
                return id;

            id = _conjChainIds.Count + 1;
            _conjChainIds[key] = id;
            return id;
        }

        private static uint ToPosMask(List<PartOfSpeech>? pos)
        {
            if (pos == null || pos.Count == 0) return 0;

            uint mask = 0;
            for (int i = 0; i < pos.Count; i++)
                mask |= 1u << (int)pos[i];
            return mask;
        }

        private static uint PosMask(params PartOfSpeech[] pos)
        {
            uint mask = 0;
            for (int i = 0; i < pos.Length; i++)
                mask |= 1u << (int)pos[i];
            return mask;
        }

        private static bool HasPos(uint mask, PartOfSpeech pos) => (mask & (1u << (int)pos)) != 0;

        private static bool HasAnyPos(uint mask, uint anyMask) => (mask & anyMask) != 0;

        public static bool HasConjugableVerbPos(IReadOnlyList<string> pos)
        {
            foreach (var p in pos)
                if (p.Length >= 2 && p[0] == 'v' && (p[1] == '1' || p[1] == '5'))
                    return true;
            return false;
        }

        private static bool HasTransitionKanji(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
                if (IsKanjiChar(text[i]))
                    return true;
            return false;
        }

        private static ulong ToConjTypeMask(IReadOnlyList<string>? chain)
        {
            if (chain == null || chain.Count == 0) return 0;

            ulong mask = 0;
            for (int i = 0; i < chain.Count; i++)
            {
                int? conjType = Jiten.Parser.Resolution.IchiranConjType.TryMap(chain[i]);
                if (conjType.HasValue && conjType.Value < 64)
                    mask |= 1UL << conjType.Value;
            }
            return mask;
        }

        private static int ComputeStemTypeMask(FormCandidate cand, IReadOnlyList<string>? conjChain)
        {
            int mask = 0;
            foreach (var stemType in TransitionStemTypes)
                if (StemMatches(stemType, cand.Word, conjChain))
                    mask |= 1 << (int)stemType;
            return mask;
        }

        private static bool IsShortKanaNotToText(string? text)
        {
            if (text is not { Length: 1 }) return false;
            char c = text[0];
            bool isKana = (c >= '\u3040' && c <= '\u309F') || (c >= '\u30A0' && c <= '\u30FF');
            return isKana && c != 'と' && c != 'ト';
        }

        private static bool IsNegativeSurfaceText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.EndsWith("ない", StringComparison.Ordinal)) return true;
            if (text.EndsWith("なかった", StringComparison.Ordinal)) return true;
            if (text.EndsWith("ねえ", StringComparison.Ordinal)) return true;
            if (text.EndsWith("ねぇ", StringComparison.Ordinal)) return true;
            if (text.EndsWith("ぬ", StringComparison.Ordinal)) return true;
            if (text.Length == 1 && text[0] == 'ん') return true;
            if (text.EndsWith("ません", StringComparison.Ordinal)) return true;
            if (text.EndsWith("ませんでした", StringComparison.Ordinal)) return true;
            return false;
        }
    }

    private sealed class BeamProfileStats
    {
        public long NodeCandidatesSeen;
        public long NodeDuplicateKeys;
        public long NodeMissingWord;
        public long NodeFilteredBeforePick;
        public long NodeNoForm;
        public long NodeCutoffRejected;
        public long NodeBuilt;
        public long NodePickCalls;
        public long NodePickTicks;
        public long NodeScoreCalls;
        public long NodeScoreTicks;
        public long NodePropCalls;
        public long NodePropTicks;

        public long BonusLookups;
        public long BonusCacheHits;
        public long BonusCacheMisses;
        public long BonusMissTicks;

        public long DpSegmentLists;
        public long DpSpanNodeTotal;
        public int DpMaxNodesPerSpan;
        public long DpCompatibleSpanPairs;
        public long DpSeedPaths;
        public long DpTransitionAttempts;
        public long DpTransitionAccepted;
        public long DpDominatedRejects = 0;
        public long DpSegfilterRejects;
        public long DpPathStatesAllocated;
        public long DpTopAccepted;
        public long DpTopRejected;
        public long DpTopEvicted;
        public long DpTerminalPaths;
        public long DpPrepTicks;
        public long DpSeedTicks;
        public long DpExtendTicks;
        public long DpFinalTicks;
    }

    private static double ProfileMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static BeamPathEntry BuildPathEntry(
        BeamState state,
        int rank,
        Dictionary<(int Start, int Len, int WordId), ScoredNode> nodes,
        string text)
    {
        var segs = state.Segs;
        var entries = new List<BeamPathSegmentEntry>(segs.Count);

        // First pass: precompute non-gap indices so we can pair each with its predecessor/successor.
        var nonGapIdx = new List<int>();
        var nonGapRankByIndex = new int[segs.Count];
        for (int i = 0; i < segs.Count; i++)
        {
            nonGapRankByIndex[i] = -1;
            if (segs[i].Cand != null)
            {
                nonGapRankByIndex[i] = nonGapIdx.Count;
                nonGapIdx.Add(i);
            }
        }

        int nodeSum = 0;
        int adjSum  = 0;
        int gapChars = 0;

        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            var surface = text.Substring(seg.Start, seg.Len);

            if (seg.Cand == null)
            {
                gapChars += seg.Len;
                entries.Add(new BeamPathSegmentEntry(
                    seg.Start, seg.Len, surface,
                    WordId: null, DictForm: null,
                    NodeScore: -Constants.UncoveredCharPenalty * seg.Len,
                    FormTotal: 0, FreqBonus: 0, LengthBonus: 0, HintBonus: 0, KanaPenalty: 0,
                    AdjacencyBonus: 0, ConjChain: null, IsGap: true));
                nodeSum -= Constants.UncoveredCharPenalty * seg.Len;
                continue;
            }

            bool hasNode = nodes.TryGetValue((seg.Start, seg.Len, seg.Cand.Word.WordId), out var node);
            int nodeScore = hasNode ? node.NodeScore : 0;

            int adj = 0;
            int idx = nonGapRankByIndex[i];
            var prev = idx > 0 ? segs[nonGapIdx[idx - 1]].Cand : null;
            var next = idx < nonGapIdx.Count - 1 ? segs[nonGapIdx[idx + 1]].Cand : null;
            var nextSeg = idx < nonGapIdx.Count - 1 ? segs[nonGapIdx[idx + 1]] : default;
            adj = BonusFor(
                seg.Cand,
                seg.ConjChain,
                prev?.Word.CachedPOS,
                prev?.Form.Text,
                next,
                next != null ? nextSeg.ConjChain : null);

            nodeSum += nodeScore;
            adjSum  += adj;

            entries.Add(new BeamPathSegmentEntry(
                seg.Start, seg.Len, surface,
                WordId: seg.Cand.Word.WordId,
                DictForm: seg.Cand.Form.Text,
                NodeScore: nodeScore,
                FormTotal: hasNode ? node.FormTotal : 0,
                FreqBonus: hasNode ? node.FreqBonus : 0,
                LengthBonus: hasNode ? node.LengthBonus : 0,
                HintBonus: hasNode ? node.HintBonus : 0,
                KanaPenalty: hasNode ? node.KanaPenalty : 0,
                AdjacencyBonus: adj,
                ConjChain: seg.ConjChain?.ToList(),
                IsGap: false));
        }

        return new BeamPathEntry(
            Rank: rank,
            TotalScore: state.FinalScore,
            NodeScoreSum: nodeSum,
            AdjacencyBonusSum: adjSum,
            GapCost: gapChars * Constants.UncoveredCharPenalty,
            GapChars: gapChars,
            Segments: entries);
    }

    private record struct ScoredNode(
        FormCandidate Cand,
        int NodeScore,
        IReadOnlyList<string>? ConjugationChain,
        int FormTotal,
        int FreqBonus,
        int LengthBonus,
        int HintBonus,
        int KanaPenalty,
        int PropScore,
        Kpcl Flags,
        bool KatakanaP,
        int TransitionTraitId,
        int AttachedSuffixPenalty = 0);

    private readonly struct DpNode(int nodeScore, int transitionTraitId, int nodeIndex, int attachedSuffixPenalty)
    {
        public readonly int NodeScore = nodeScore;
        public readonly int TransitionTraitId = transitionTraitId;
        public readonly int NodeIndex = nodeIndex;
        public readonly int AttachedSuffixPenalty = attachedSuffixPenalty;
    }

    private sealed record PathSegment(int Start, int Len, FormCandidate? Cand, IReadOnlyList<string>? ConjChain);

    private sealed class BeamState
    {
        public FormCandidate? PendingCand { get; }
        public IReadOnlyList<string>? PendingConjChain { get; }
        public int PendingNodeScore { get; }
        public int PendingTransitionTraitId { get; }
        public int PendingGapChars { get; }
        public int Score { get; }
        public int GapChars { get; }
        public List<PathSegment> Segs { get; set; }
        public int FinalScore { get; set; }

        public BeamState(
            FormCandidate? PendingCand,
            IReadOnlyList<string>? PendingConjChain,
            int PendingNodeScore,
            int PendingTransitionTraitId,
            int PendingGapChars,
            int Score,
            int GapChars,
            List<PathSegment> Segs)
        {
            this.PendingCand = PendingCand;
            this.PendingConjChain = PendingConjChain;
            this.PendingNodeScore = PendingNodeScore;
            this.PendingTransitionTraitId = PendingTransitionTraitId;
            this.PendingGapChars = PendingGapChars;
            this.Score = Score;
            this.GapChars = GapChars;
            this.Segs = Segs;
        }

        public static BeamState Empty() => new(null, null, 0, 0, 0, 0, 0, new List<PathSegment>());
    }

    internal sealed class BonusCacheTable
    {
        private const int Size = 32768;
        private const int Mask = Size - 1;
        private readonly ulong[] _keys = new ulong[Size];
        private readonly int[] _vals = new int[Size];

        public bool TryGet(ulong key, out int value)
        {
            int idx = (int)((key ^ (key >> 15)) & Mask);
            if (_keys[idx] == key && key != 0)
            {
                value = _vals[idx];
                return true;
            }
            value = 0;
            return false;
        }

        public void Set(ulong key, int value)
        {
            int idx = (int)((key ^ (key >> 15)) & Mask);
            _keys[idx] = key;
            _vals[idx] = value;
        }
    }

    internal readonly struct WordCacheView
    {
        private readonly JmDictWord?[]? _array;
        private readonly Dictionary<int, JmDictWord>? _overflow;

        public WordCacheView(JmDictWord?[]? array, Dictionary<int, JmDictWord>? overflow)
        {
            _array = array;
            _overflow = overflow;
        }

        public bool TryGetValue(int wordId, out JmDictWord? word)
        {
            var arr = _array;
            if (arr != null && (uint)wordId < (uint)arr.Length)
            {
                word = arr[wordId];
                if (word != null) return true;
            }
            if (_overflow != null && _overflow.TryGetValue(wordId, out word!)) return true;
            word = null;
            return false;
        }

        public bool ContainsKey(int wordId)
        {
            var arr = _array;
            if (arr != null && (uint)wordId < (uint)arr.Length && arr[wordId] != null) return true;
            return _overflow != null && _overflow.ContainsKey(wordId);
        }
    }
}
