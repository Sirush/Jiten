using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.JMDict;
using WanaKanaShaapu;

namespace Jiten.Parser.Misparse;

internal readonly record struct MisparseDecision(bool IsMisparsed, string? GateId = null);

internal readonly record struct MisparseGateContext(
    WordInfo Token,
    DeckWord SelectedWord,
    WordInfo? Prev,
    WordInfo? Next,
    bool IsUsuallyKana,
    bool HasKanjiSpelling,
    bool ReadingIsIchi,
    bool IsSentenceInitial = false,
    string SymbolsBefore = "",
    string SymbolsAfter = "",
    bool SurfaceAttestsListedForm = false,
    bool ShardBlobUnattested = false,
    bool PrevDroppedByGate = false,
    bool NextDroppedByGate = false);

internal static class MisparseGates
{
    private static readonly HashSet<PartOfSpeech> ExemptFromKanaGate =
    [
        PartOfSpeech.Particle, PartOfSpeech.Auxiliary, PartOfSpeech.Conjunction,
        PartOfSpeech.Adnominal, PartOfSpeech.Pronoun
    ];

    public static MisparseDecision Evaluate(in MisparseGateContext ctx)
    {
        if (IsShortKanaNameWithoutContext(in ctx))
            return new(true, "short-kana-name");

        if (IsRepeatedKanaStuttering(in ctx))
            return new(true, "repeated-kana-stutter");

        if (IsKanaStutterBeforeWord(in ctx))
            return new(true, "kana-stutter-before-word");

        if (IsShortKanaTokenWithoutJustification(in ctx))
            return new(true, "short-kana-unjustified");

        if (IsSfxMimeticFragment(in ctx))
            return new(true, "sfx-mimetic-fragment");

        return default;
    }

    // A hiragana small vowel after a full kana is expressive stretching (ぱぁん, うぅ) — real words
    // never spell it. Sokuon/chōonpu and katakana smalls are NOT evidence: they are ordinary
    // orthography in real words (おっさん, リッキー, ファン), so only a sokuon in the following
    // symbol gap (とう|っ) counts, via the clip check.
    private static readonly char[] ExpressiveSmallVowels = ['ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ'];
    private static readonly char[] GapMimeticChars = ['っ', 'ッ', 'ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ'];

    /// Two rules. (1) A fragment of an unsegmentable kana blob (ざ|くぅ, ず|がんっ) is phonetic
    /// material whatever it matched, unless it is the word as-written (usually-kana or an attested
    /// exclamatory sense). (2) Outside blobs, a plain NOUN/NUMERAL match on a punctuation-isolated
    /// fragment carrying a mimetic mark (ぱぁん！, とうっ！, ぱん！と) is a phonetic coincidence.
    /// Non-noun matches are removed only when text mutation invented them — rule (1) and the
    /// attestation checks — never when they genuinely analyse the surface; the rare real-analysis
    /// collision outside the noun class (うりゃ→売る) is accepted rather than widening the
    /// destructive scope.
    private static bool IsSfxMimeticFragment(in MisparseGateContext ctx)
    {
        string surface = ctx.SelectedWord.OriginalText.Length > 0
            ? ctx.SelectedWord.OriginalText
            : ctx.Token.Text;

        if (surface.Length == 0 || surface.Length > 4 || !WanaKana.IsKana(surface)) return false;

        // A neighbour already discarded as a misparse shred is part of the same burst; so is an
        // unresolved kana scrap. Both make the current token frame-transparent on that side.
        bool prevShard = IsKanaShardNeighbour(ctx.Prev) || ctx.PrevDroppedByGate;
        bool nextShard = IsKanaShardNeighbour(ctx.Next) || ctx.NextDroppedByGate;
        // Sokuon only: a trailing ー is ordinary colloquial elongation of a real word (おまえー).
        bool exclamatoryClip = ctx.SymbolsAfter.Length > 0 && ctx.SymbolsAfter[0] is 'っ' or 'ッ';
        // A sokuon clipped onto the token with only a kana scrap before it (ず|がんっ) marks the whole
        // run as one burst, overriding even usually-kana protection (癌) — inside a burst nothing is
        // vocabulary. The clip is required evidence: blob adjacency alone is not — a real
        // usually-kana word can sit right against a dropped fragment (わーい after its deduped twin).
        bool burstContext = exclamatoryClip
                            && (prevShard
                                || (ctx.Prev != null && ctx.Prev.Text.Length <= 2
                                    && WanaKana.IsKana(ctx.Prev.Text)));

        // The token must be cut off from the sentence on both sides — by punctuation, an utterance
        // boundary, an interjection, a trailing sentence-final particle, or another shard of the same
        // unsegmentable blob. A case particle or content word on either side means real syntax,
        // which the gate must not touch. When a sokuon is clipped straight onto the token (ず|がんっ),
        // a short kana neighbour is part of the same burst even if it happened to resolve.
        bool frameBefore = ctx.Prev == null || ctx.SymbolsBefore.Length > 0 || ctx.IsSentenceInitial
                           || ctx.Prev.PartOfSpeech == PartOfSpeech.Interjection
                           || (ctx.Prev.PartOfSpeech == PartOfSpeech.Particle
                               && ctx.Prev.Text is "な" or "よ" or "ね" or "ぞ" or "ぜ" or "わ" or "さ")
                           || (exclamatoryClip && ctx.Prev.Text.Length <= 2 && WanaKana.IsKana(ctx.Prev.Text))
                           || prevShard;
        if (!frameBefore) return false;

        bool nextIsQuotativeTo = ctx.Next is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }
                                 && ctx.SymbolsAfter.Length > 0;
        bool frameAfter = ctx.Next == null || ctx.SymbolsAfter.Length > 0 || nextShard;
        if (!frameAfter) return false;

        // Require a positive mimetic signal; isolation alone describes any one-word answer (「梨」).
        // An exclamation mark counts only when the entry does not actually spell the surface
        // (「きゃ！」 matched to 毛) — attested words are legitimately shouted (「だめ！」) — or when a
        // lone vowel kana trails a final particle into the exclamation (…なよ|お|！, a scream tail).
        bool marker = surface.IndexOfAny(ExpressiveSmallVowels) >= 0
                      || ctx.SymbolsAfter.IndexOfAny(GapMimeticChars) >= 0
                      || nextIsQuotativeTo
                      || prevShard || nextShard
                      // An in-surface sokuon is ordinary orthography when the entry spells the
                      // surface — literally (おっさん) or through the expressive-deformation check
                      // in GetWordFlags (バカッ/ほんっと) — but phonetic evidence when it does not
                      // (ズクッ matched to the owl 木菟 only by ignoring the ッ). JMnedict entries
                      // are exempt: a name is matched through spelling variants by design.
                      || (!ctx.SurfaceAttestsListedForm
                          && ctx.SelectedWord.WordId is < 5000000 or >= 8000000
                          && surface.IndexOfAny(['っ', 'ッ']) >= 0)
                      || (surface.Length <= 2
                          && (!ctx.SurfaceAttestsListedForm
                              || (surface.Length == 1 && "あいうえお".IndexOf(surface[0]) >= 0 && ctx.Next == null))
                          && ctx.SymbolsAfter.IndexOfAny(['！', '？', '!', '?']) >= 0);
        if (!marker) return false;

        // A token inside an unsegmentable blob (ざ|くぅ) is phonetic no matter what it matched —
        // including "conjugated" matches invented from a blob shard. The exceptions are words
        // genuinely at home beside a burst: attested usually-kana words (なぜ), attested
        // interjections/expressions (そっか), and usually-kana interjections even when the exact
        // stretch is unlisted (わーい). A bare usually-kana NOUN or verb reached through mutation
        // (ばぁ→婆, おりゃ→居る) is still burst noise.
        // Adverb included: mimetic adverbs (おどおど) are kana-only entries without a uk tag.
        bool blobInterjection = ctx.SelectedWord.PartsOfSpeech.Any(p => p is PartOfSpeech.Interjection
            or PartOfSpeech.Expression or PartOfSpeech.Adverb or PartOfSpeech.AdverbTo);
        if (ctx.ShardBlobUnattested
            && !(blobInterjection && (ctx.IsUsuallyKana || ctx.SurfaceAttestsListedForm))
            && !(ctx.IsUsuallyKana && ctx.SurfaceAttestsListedForm))
            return true;

        // SCOPE: outside blobs, this gate judges only plain noun/numeral matches — the
        // homographs it exists to remove are that class (パン, 額, 塔, 盆, 癌, 梨…). Any other
        // word class is out of scope when it is a real analysis of its surface (やめて！,
        // うぜえ、と, ごくり、と, ハルさんっ): attested as written, usually-kana, or reached
        // through conjugation. A non-noun invented by text mutation (うぅ→うん) stays in scope
        // as noise.
        if ((ctx.SelectedWord.PartsOfSpeech.Count == 0
             || ctx.SelectedWord.PartsOfSpeech[0] is not (PartOfSpeech.Noun
                 or PartOfSpeech.CommonNoun or PartOfSpeech.Numeral or PartOfSpeech.NaAdjective))
            && (ctx.SurfaceAttestsListedForm || ctx.IsUsuallyKana
                || (ctx.SelectedWord.Conjugations.Count > 0
                    && ctx.SelectedWord.Conjugations[0] is not ("(stem)" or "(infinitive)"
                        or "(unstressed infinitive)" or "provisional conditional" or "conjunctive"
                        or "(izenkei)" or "(mizenkei)" or "contracted"))))
            return false;

        // Exemptions for in-scope nouns genuinely uttered bare: usually-kana vocatives (ばかっ！)
        // and nouns carrying an attested exclamatory sense (嘘っ！ — 嘘 is noun-primary with int).
        // Usually-kana protection requires the entry to actually spell the surface — an
        // unattested stretch (ズクッ for the owl ズク) is not the word as-written.
        if (ctx.IsUsuallyKana && ctx.SurfaceAttestsListedForm && !burstContext) return false;
        if (ctx.SurfaceAttestsListedForm
            && ctx.SelectedWord.PartsOfSpeech.Any(p => p is PartOfSpeech.Interjection
                or PartOfSpeech.Expression))
            return false;

        // A sokuon clipped straight onto the token (とうっ！) or punctuation inside the quotative
        // frame (ぶん！と) is exclamatory phonetics — plain nouns are never written that way, however
        // common the homograph.
        bool punctuatedQuotative = nextIsQuotativeTo
                                   && ctx.SymbolsAfter.IndexOfAny(['！', '？', '、', '。', '…', '!', '?']) >= 0;
        if (exclamatoryClip || punctuatedQuotative) return true;

        if (ctx.SurfaceAttestsListedForm)
        {
            // A single kana spelled like a kanji word (ぶ→部) is only that word when case-marked;
            // bare in a mimetic frame it is the burst's first mora.
            bool anchored = surface.Length >= 2
                            || (ctx.Next != null && IsGrammaticalFollower(ctx.Next.Text));
            if (anchored && ctx.ReadingIsIchi) return false;
        }

        return true;
    }

    // A neighbouring token that is itself an unresolved kana scrap marks the current token as part
    // of a shredded blob rather than a standalone word.
    internal static bool IsKanaShardNeighbour(WordInfo? w)
        => w is { ResolvedWordId: null } && w.Text.Length <= 3 && WanaKana.IsKana(w.Text)
           && w.PartOfSpeech is not (PartOfSpeech.Particle or PartOfSpeech.Auxiliary
               or PartOfSpeech.BlankSpace);

    private static bool IsRepeatedKanaStuttering(in MisparseGateContext ctx)
    {
        string surface = ctx.Token.Text;
        if (surface.Length < 2 || !WanaKana.IsKana(surface)) return false;

        char first = surface[0];
        for (int i = 1; i < surface.Length; i++)
            if (surface[i] != first) return false;

        // Genuine repeated-vowel interjections (ああ, ええ, おお, ささ) are Interjection-tagged and matched
        // to interjection entries; the neighbour-vowel heuristics below over-fire when a following word
        // coincidentally shares the vowel (ああ before あたし). Real stutter shreds (ぼぼ, なな) are Noun.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Interjection) return false;

        // Common vocabulary — trust the match (パパ, ママ, もも, みみ, etc.)
        if (ctx.ReadingIsIchi || ctx.IsUsuallyKana) return false;

        char katakanaChar = first >= 'ぁ' && first <= 'ん'
            ? (char)(first + 0x60) // hiragana → katakana
            : first;

        // Prev token contains the same kana
        if (ctx.Prev != null && ctx.Prev.Text.IndexOf(first) >= 0)
            return true;

        // Next token's Sudachi reading starts with the same kana (catches ぼぼ僕: Reading=ボク)
        if (ctx.Next?.Reading is { Length: > 0 } reading && reading[0] == katakanaChar)
            return true;

        // Both neighbours are single kana (onomatopoeia context like ちゅぼぼっ)
        if (ctx.Prev is { Text.Length: <= 2 } && WanaKana.IsKana(ctx.Prev.Text)
            && ctx.Next is { Text.Length: <= 2 } && WanaKana.IsKana(ctx.Next.Text))
            return true;

        return false;
    }

    private static bool IsKanaStutterBeforeWord(in MisparseGateContext ctx)
    {
        string surface = ctx.Token.Text;
        if (surface.Length > 3 || !WanaKana.IsKana(surface)) return false;

        // An emphatic interjection token ending in っ/ー (くそっ, あー) that is repeated for effect
        // (くそっくそっ…) is deliberate, not a sub-word stutter shred, so it is kept. Plain response
        // interjections (はい) lack the っ/ー and are still de-duplicated (the repeat is dropped).
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Interjection
            && (surface.EndsWith('っ') || surface.EndsWith('ッ') || surface.EndsWith('ー'))) return false;

        if (ctx.ReadingIsIchi || ctx.IsUsuallyKana) return false;

        if (ctx.Next is not { Reading.Length: > 0, Text.Length: > 0 } next) return false;

        // Hiragana before a katakana word is not a stutter (e.g. は + ハードル)
        if (next.Text[0] >= 'ァ' && next.Text[0] <= 'ヴ') return false;

        // A particle after a real word is the particle, not a stutter, even when the next word happens to
        // start with the same kana (で before できる; は before 離れる; particle-stacking からは/には/では).
        // Real stutters (ぼ before ぼく) follow punctuation/start, so their Prev is a symbol or null.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Particle && ctx.Prev is { PartOfSpeech: PartOfSpeech.Noun or PartOfSpeech.Verb
            or PartOfSpeech.IAdjective or PartOfSpeech.NaAdjective or PartOfSpeech.Adverb or PartOfSpeech.Pronoun
            or PartOfSpeech.Expression or PartOfSpeech.Suffix or PartOfSpeech.Counter or PartOfSpeech.Numeral
            or PartOfSpeech.Particle })
            return false;

        string katakana = surface.Length == 1
            ? new string(surface[0] >= 'ぁ' && surface[0] <= 'ん' ? (char)(surface[0] + 0x60) : surface[0], 1)
            : WanaKana.ToKatakana(surface);

        return next.Reading.StartsWith(katakana, StringComparison.Ordinal);
    }

    private static bool IsShortKanaNameWithoutContext(in MisparseGateContext ctx)
    {
        if (!WanaKana.IsKana(ctx.Token.Text)) return false;
        if (ctx.Token.Text.Length > 2) return false;
        if (!ctx.SelectedWord.PartsOfSpeech.Contains(PartOfSpeech.Name)) return false;
        if (ctx.Token.IsPersonNameContext) return false;
        if (JapaneseTextHelper.IsAllKatakana(ctx.Token.Text)) return false;

        return true;
    }

    private static bool IsShortKanaTokenWithoutJustification(in MisparseGateContext ctx)
    {
        string surface = ctx.Token.Text;

        if (!WanaKana.IsKana(surface)) return false;
        if (surface.Length > 2) return false;

        if (ExemptFromKanaGate.Contains(ctx.Token.PartOfSpeech)) return false;

        // Sentence-initial OR post-punctuation two-kana interjections (ん、ああ、 / ええ、) are legitimate
        // standalone utterances even when a kanji spelling exists (嗚呼). Mid-word elongation shreds
        // (いきた+ああ) attach directly to a content word (Prev is a verb/noun) and stay gated.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Interjection && surface.Length >= 2
            && (ctx.IsSentenceInitial || ctx.Prev == null
                || ctx.Prev.PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                    or PartOfSpeech.BlankSpace or PartOfSpeech.Interjection)) return false;

        // Demonstrative ああ/こう/そう directly before a verb (ああなった, こう言う) is the
        // "like that/this" adverb, not an elongation shred — shreds never precede a verb.
        if (surface is "ああ" or "こう" or "そう" && ctx.Next?.PartOfSpeech == PartOfSpeech.Verb)
            return false;

        if (ctx.IsUsuallyKana) return false;

        if (!ctx.HasKanjiSpelling) return false;

        if (ctx.ReadingIsIchi) return false;

        if (JapaneseTextHelper.IsAllKatakana(surface)) return false;

        if (ctx.Next != null && IsGrammaticalFollower(ctx.Next.Text))
            return false;

        return true;
    }

    private static bool IsGrammaticalFollower(string text)
        => text is "が" or "を" or "に" or "は" or "の" or "で" or "と" or "へ"
               or "から" or "まで" or "より" or "も" or "って" or "だ" or "です"
           // Quotative って-clusters (っていう, ってのは, …) justify a short-kana verb being quoted
           // (してある+っていう), the same way a bare って does — they only differ by a later merge.
           || text.StartsWith("って", StringComparison.Ordinal);

    public static (bool isUsuallyKana, bool hasKanjiSpelling, bool readingIsIchi, bool surfaceAttestsForm)
        GetWordFlags(JmDictWord? word, byte readingIndex, string? surface = null)
    {
        if (word == null) return (false, true, false, false);

        bool isUk = word.PartsOfSpeech.Contains("uk");
        bool hasKanji = word.Forms.Any(f => f.FormType == JmDictFormType.KanjiForm);

        bool readingIsIchi = word.Priorities?.Contains("jiten") == true
                             || word.Forms.Any(f => f.FormType == JmDictFormType.KanaForm
                                                    && f.ReadingIndex == readingIndex
                                                    && f.Priorities != null
                                                    && (f.Priorities.Contains("ichi1") || f.Priorities.Contains("ichi2")
                                                        || f.Priorities.Contains("jiten")));

        // Literal, same-script comparison: hiragana ぱん does not attest katakana-only パン. A surface
        // the entry does not actually spell was reached through normalisation or text mutation.
        bool surfaceAttestsForm = surface != null && word.Forms.Any(f => f.Text == surface);

        // Expressive spelling deforms a word without changing it: emphatic gemination writes a
        // sokuon in (ほんっと, バカッ, マジッす), a chōonpu stretches a mora (おーっと), and a
        // trailing stretch elongates the final one (そっかー, だってぇ, なんだとぉ). Such a
        // spelling still attests the word — when the word is credible as shouted vocabulary:
        // kana-native (マジ, そっか) or carrying a priority tag (馬鹿, 本当). For a rare kanji
        // word the unlisted deformation is instead the give-away that only text mutation produced
        // the match (ズクッ → the owl 木菟).
        if (!surfaceAttestsForm && surface != null
            && (!hasKanji || word.Priorities is { Count: > 0 }))
        {
            string detrailed = surface.TrimEnd('ー', '〜', 'っ', 'ッ', 'ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ');
            string degeminated = string.Concat(surface.Where(c => c is not ('っ' or 'ッ')));
            // An internal stretch mark belongs to a shouted content word (おーっと); function
            // words are unstressed, so a particle reached by de-stretching (わーい → the
            // sentence-final わい) is noise, not the word elongated.
            bool stretchable = word.Priorities is { Count: > 0 }
                               || word.PartsOfSpeech.Any(p => p is "int" or "exp");
            string destretched = stretchable
                ? string.Concat(surface.Where(c => c is not ('ー' or '〜')))
                : surface;
            string bare = stretchable
                ? string.Concat(detrailed.Where(c => c is not ('っ' or 'ッ' or 'ー' or '〜')))
                : detrailed;
            // The trailing strip must not dominate the surface: そっかー is そっか elongated, but
            // in a scream tail (やった|ああぁぁーー) the leftover word is buried in the stretch —
            // that is phonetic material, not the word.
            if (detrailed.Length * 2 < surface.Length) detrailed = surface;
            if (bare.Length * 2 < surface.Length) bare = surface;
            foreach (var variant in (ReadOnlySpan<string>)[detrailed, degeminated, destretched, bare])
            {
                if (variant.Length == 0 || variant == surface) continue;
                if (word.Forms.Any(f => f.Text == variant))
                {
                    surfaceAttestsForm = true;
                    break;
                }
            }
        }

        return (isUk, hasKanji, readingIsIchi, surfaceAttestsForm);
    }
}
