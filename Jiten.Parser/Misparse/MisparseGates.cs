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
    bool SurfaceAttestsLiterally = false,
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

    private static readonly char[] TrailingStretchChars = ['ー', '〜', 'っ', 'ッ', 'ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ'];

    /// <summary>Superset pre-filter: gates judge only kana of 4 chars or fewer, or a single-kana run.</summary>
    public static bool MayBeKanaFragment(string s)
    {
        if (s.Length == 0) return false;
        if (s.Length > 4)
        {
            for (int i = 1; i < s.Length; i++)
                if (s[i] != s[0])
                    return false;
        }

        foreach (var c in s)
            if (!(c is >= '぀' and <= 'ヿ' or >= 'ｦ' and <= 'ﾝ' or '〜'))
                return false;
        return true;
    }

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

    // A hiragana small vowel is expressive (ぱぁん); sokuon and katakana smalls are real orthography (おっさん, ファン).
    private static readonly char[] ExpressiveSmallVowels = ['ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ'];
    private static readonly char[] GapMimeticChars = ['っ', 'ッ', 'ぁ', 'ぃ', 'ぅ', 'ぇ', 'ぉ'];

    /// <summary>Drops blob shards (ざ|くぅ) and isolated mimetic nouns (ぱぁん！); real non-noun analyses (うりゃ→売る) stay.</summary>
    private static bool IsSfxMimeticFragment(in MisparseGateContext ctx)
    {
        string surface = ctx.SelectedWord.OriginalText.Length > 0
            ? ctx.SelectedWord.OriginalText
            : ctx.Token.Text;

        // Reduplication (コクコク) is mimetic; 6-char ones (ギュウギュウ) arrive only via a shorter token's OriginalText.
        bool reduplicated = surface.Length is 4 or 6
                            && surface[..(surface.Length / 2)] == surface[(surface.Length / 2)..];
        if (surface.Length == 0 || (surface.Length > 4 && !reduplicated) || surface.Length > 6
            || !JapaneseTextHelper.IsAllKana(surface)) return false;

        // A dropped or unresolved kana shard neighbour is the same burst, so that side counts as framed.
        bool prevShard = IsKanaShardNeighbour(ctx.Prev) || ctx.PrevDroppedByGate;
        bool nextShard = IsKanaShardNeighbour(ctx.Next) || ctx.NextDroppedByGate;
        // Sokuon only: a trailing ー is ordinary colloquial elongation of a real word (おまえー).
        bool exclamatoryClip = ctx.SymbolsAfter.Length > 0 && ctx.SymbolsAfter[0] is 'っ' or 'ッ';
        // A clipped sokuon after a kana scrap (ず|がんっ) overrides usually-kana (癌); adjacency alone doesn't (わーい).
        bool burstContext = exclamatoryClip
                            && (prevShard
                                || (ctx.Prev != null && ctx.Prev.Text.Length <= 2
                                    && JapaneseTextHelper.IsAllKana(ctx.Prev.Text)));

        // A reduplication heading 〜と+verb (コクコクと振る) is isolated by the construction itself.
        bool reduplicatedToFrame = reduplicated
                                   && ctx.Next is { Text: "と", PartOfSpeech: PartOfSpeech.Particle };

        // Both sides must be cut off; a case particle or content word next to it is real syntax.
        bool frameBefore = ctx.Prev == null || ctx.SymbolsBefore.Length > 0 || ctx.IsSentenceInitial
                           || ctx.Prev.PartOfSpeech == PartOfSpeech.Interjection
                           || (ctx.Prev.PartOfSpeech == PartOfSpeech.Particle
                               && ctx.Prev.Text is "な" or "よ" or "ね" or "ぞ" or "ぜ" or "わ" or "さ")
                           || (exclamatoryClip && ctx.Prev.Text.Length <= 2 && JapaneseTextHelper.IsAllKana(ctx.Prev.Text))
                           || prevShard
                           || reduplicatedToFrame;
        if (!frameBefore) return false;

        bool nextIsQuotativeTo = ctx.Next is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }
                                 && ctx.SymbolsAfter.Length > 0;
        bool frameAfter = ctx.Next == null || ctx.SymbolsAfter.Length > 0 || nextShard
                          || reduplicatedToFrame;
        if (!frameAfter) return false;

        // Isolation alone fits any one-word answer (「梨」); ！ counts only if unattested (きゃ！→毛, not だめ！) or a vowel tail.
        bool marker = surface.IndexOfAny(ExpressiveSmallVowels) >= 0
                      || ctx.SymbolsAfter.IndexOfAny(GapMimeticChars) >= 0
                      || nextIsQuotativeTo
                      || prevShard || nextShard
                      || reduplicated
                      // In-surface sokuon is phonetic unless the entry spells it (ズクッ→木菟, not バカッ); names exempt.
                      || (!ctx.SurfaceAttestsListedForm
                          && ctx.SelectedWord.WordId is < 5000000 or >= 8000000
                          && surface.IndexOfAny(['っ', 'ッ']) >= 0)
                      || (surface.Length <= 2
                          && (!ctx.SurfaceAttestsListedForm
                              || (surface.Length == 1 && "あいうえお".IndexOf(surface[0]) >= 0 && ctx.Next == null))
                          && ctx.SymbolsAfter.IndexOfAny(['！', '？', '!', '?']) >= 0);
        if (!marker) return false;

        // A blob shard is phonetic whatever it matched (ばぁ→婆); only literal attestation counts (ざ|くぅ, not くう).
        // Adverb included: mimetic adverbs (おどおど) are kana-only entries without a uk tag.
        bool blobInterjection = ctx.SelectedWord.PartsOfSpeech.Any(p => p is PartOfSpeech.Interjection
            or PartOfSpeech.Expression or PartOfSpeech.Adverb or PartOfSpeech.AdverbTo);
        if (ctx.ShardBlobUnattested
            && !(blobInterjection && (ctx.IsUsuallyKana || ctx.SurfaceAttestsLiterally))
            && !(ctx.IsUsuallyKana && ctx.SurfaceAttestsLiterally))
            return true;

        // Outside blobs only noun homographs are in scope (パン, 癌); real non-noun analyses (やめて！) stay, mutations (うぅ→うん) don't.
        // An adverbial sense in the と frame wins over the first-listed POS (ぽつぽつと灯す).
        bool adverbialInToFrame = ctx.Next is { Text: "と", PartOfSpeech: PartOfSpeech.Particle }
            && ctx.SelectedWord.PartsOfSpeech.Any(p => p is PartOfSpeech.Adverb or PartOfSpeech.AdverbTo);
        if ((ctx.SelectedWord.PartsOfSpeech.Count == 0
             || ctx.SelectedWord.PartsOfSpeech[0] is not (PartOfSpeech.Noun
                 or PartOfSpeech.CommonNoun or PartOfSpeech.Numeral or PartOfSpeech.NaAdjective)
             || adverbialInToFrame)
            && (ctx.SurfaceAttestsListedForm || ctx.IsUsuallyKana
                || (ctx.SelectedWord.Conjugations.Count > 0
                    && ctx.SelectedWord.Conjugations[0] is not ("(stem)" or "(infinitive)"
                        or "(unstressed infinitive)" or "provisional conditional" or "conjunctive"
                        or "(izenkei)" or "(mizenkei)" or "contracted"))))
            return false;

        // Bare-uttered nouns stay: spelled usually-kana vocatives (ばかっ！, not ズクッ) and exclamatory senses (嘘っ！).
        if (ctx.IsUsuallyKana && ctx.SurfaceAttestsListedForm && !burstContext) return false;
        if (ctx.SurfaceAttestsListedForm
            && ctx.SelectedWord.PartsOfSpeech.Any(p => p is PartOfSpeech.Interjection
                or PartOfSpeech.Expression))
            return false;

        // Plain nouns are never written with a clipped sokuon (とうっ！) or a punctuated quotative (ぶん！と).
        bool punctuatedQuotative = nextIsQuotativeTo
                                   && ctx.SymbolsAfter.IndexOfAny(['！', '？', '、', '。', '…', '!', '?']) >= 0;
        if (exclamatoryClip || punctuatedQuotative) return true;

        if (ctx.SurfaceAttestsListedForm)
        {
            // A single kana spelled like a kanji word (ぶ→部) is that word only when case-marked.
            bool anchored = surface.Length >= 2
                            || (ctx.Next != null && IsGrammaticalFollower(ctx.Next.Text));
            if (anchored && ctx.ReadingIsIchi) return false;
        }

        return true;
    }

    internal static bool IsKanaShardNeighbour(WordInfo? w)
        => w is { ResolvedWordId: null } && w.Text.Length <= 3 && JapaneseTextHelper.IsAllKana(w.Text)
           && w.PartOfSpeech is not (PartOfSpeech.Particle or PartOfSpeech.Auxiliary
               or PartOfSpeech.BlankSpace);

    private static bool IsRepeatedKanaStuttering(in MisparseGateContext ctx)
    {
        string surface = ctx.Token.Text;
        if (surface.Length < 2 || !JapaneseTextHelper.IsAllKana(surface)) return false;

        char first = surface[0];
        for (int i = 1; i < surface.Length; i++)
            if (surface[i] != first) return false;

        // Neighbour heuristics over-fire on repeated-vowel interjections (ああ before あたし); stutter shreds are Noun.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Interjection) return false;

        // Common vocabulary (パパ, もも).
        if (ctx.ReadingIsIchi || ctx.IsUsuallyKana) return false;

        char katakanaChar = first >= 'ぁ' && first <= 'ん'
            ? (char)(first + 0x60) // hiragana → katakana
            : first;

        if (ctx.Prev != null && ctx.Prev.Text.IndexOf(first) >= 0)
            return true;

        // ぼぼ僕: Next.Reading is ボク.
        if (ctx.Next?.Reading is { Length: > 0 } reading && reading[0] == katakanaChar)
            return true;

        // Onomatopoeia context (ちゅぼぼっ).
        if (ctx.Prev is { Text.Length: <= 2 } && JapaneseTextHelper.IsAllKana(ctx.Prev.Text)
            && ctx.Next is { Text.Length: <= 2 } && JapaneseTextHelper.IsAllKana(ctx.Next.Text))
            return true;

        return false;
    }

    private static bool IsKanaStutterBeforeWord(in MisparseGateContext ctx)
    {
        string surface = ctx.Token.Text;
        if (surface.Length > 3 || !JapaneseTextHelper.IsAllKana(surface)) return false;

        // A repeated emphatic interjection (くそっくそっ) is deliberate; plain ones (はい) lack っ/ー and are de-duplicated.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Interjection
            && (surface.EndsWith('っ') || surface.EndsWith('ッ') || surface.EndsWith('ー'))) return false;

        if (ctx.ReadingIsIchi || ctx.IsUsuallyKana) return false;

        if (ctx.Next is not { Reading.Length: > 0, Text.Length: > 0 } next) return false;

        // は + ハードル is not a stutter.
        if (next.Text[0] >= 'ァ' && next.Text[0] <= 'ヴ') return false;

        // A particle after a real word is the particle (で before できる); real stutters follow punctuation or start.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Particle && ctx.Prev is { PartOfSpeech: PartOfSpeech.Noun or PartOfSpeech.Verb
            or PartOfSpeech.IAdjective or PartOfSpeech.NaAdjective or PartOfSpeech.Adverb or PartOfSpeech.Pronoun
            or PartOfSpeech.Expression or PartOfSpeech.Suffix or PartOfSpeech.Counter or PartOfSpeech.Numeral
            or PartOfSpeech.Particle })
            return false;

        string katakana = JapaneseTextHelper.HiraganaToKatakana(surface);

        return next.Reading.StartsWith(katakana, StringComparison.Ordinal);
    }

    private static bool IsShortKanaNameWithoutContext(in MisparseGateContext ctx)
    {
        if (!JapaneseTextHelper.IsAllKana(ctx.Token.Text)) return false;
        if (ctx.Token.Text.Length > 2) return false;
        if (!ctx.SelectedWord.PartsOfSpeech.Contains(PartOfSpeech.Name)) return false;
        if (ctx.Token.IsPersonNameContext) return false;
        if (JapaneseTextHelper.IsAllKatakana(ctx.Token.Text)) return false;

        return true;
    }

    private static bool IsShortKanaTokenWithoutJustification(in MisparseGateContext ctx)
    {
        string surface = ctx.Token.Text;

        if (!JapaneseTextHelper.IsAllKana(surface)) return false;
        if (surface.Length > 2) return false;

        if (ExemptFromKanaGate.Contains(ctx.Token.PartOfSpeech)) return false;

        // A stem the deconjugator reassembled with its auxiliary (き|た) is grammar, not a stray fragment.
        if (ctx.Token.IsMergedInflection && ctx.SelectedWord.Conjugations.Count > 0) return false;

        // Isolated two-kana interjections (ええ、) are utterances despite 嗚呼; shreds after a content word (いきた+ああ) stay gated.
        if (ctx.Token.PartOfSpeech == PartOfSpeech.Interjection && surface.Length >= 2
            && (ctx.IsSentenceInitial || ctx.Prev == null
                || ctx.Prev.PartOfSpeech is PartOfSpeech.SupplementarySymbol or PartOfSpeech.Symbol
                    or PartOfSpeech.BlankSpace or PartOfSpeech.Interjection)) return false;

        // ああ/こう/そう before a verb (ああなった) is the demonstrative adverb; shreds never precede a verb.
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
           // って-clusters (っていう, ってのは) justify a quoted short-kana verb like bare って (してある+っていう).
           || text.StartsWith("って", StringComparison.Ordinal);

    public static (bool isUsuallyKana, bool hasKanjiSpelling, bool readingIsIchi, bool surfaceAttestsForm,
        bool surfaceAttestsLiterally)
        GetWordFlags(JmDictWord? word, byte readingIndex, string? surface = null)
    {
        if (word == null) return (false, true, false, false, false);

        bool isUk = word.PartsOfSpeech.Contains("uk");
        bool hasKanji = false;
        bool readingIsIchi = word.Priorities?.Contains("jiten") == true;
        // Literal same-script match: ぱん doesn't attest パン; an unspelled surface came from normalisation or mutation.
        bool surfaceAttestsForm = false;
        foreach (var f in word.Forms)
        {
            if (f.FormType == JmDictFormType.KanjiForm) hasKanji = true;
            if (!readingIsIchi && f.FormType == JmDictFormType.KanaForm && f.ReadingIndex == readingIndex
                && f.Priorities != null
                && (f.Priorities.Contains("ichi1") || f.Priorities.Contains("ichi2") || f.Priorities.Contains("jiten")))
                readingIsIchi = true;
            if (surface != null && f.Text == surface) surfaceAttestsForm = true;
        }

        bool surfaceAttestsLiterally = surfaceAttestsForm;

        // Mimetics take either kana script (スタスタと) but JMdict lists hiragana; class-limited so SFX can't claim other words.
        if (!surfaceAttestsForm && surface is { Length: > 0 }
            && word.PartsOfSpeech.Any(p => p is "adv" or "adv-to" or "int" or "on-mim")
            && JapaneseTextHelper.IsAllKatakana(surface))
        {
            var folded = KanaConverter.ToHiragana(surface);
            if (folded != surface && word.Forms.Any(f => f.Text == folded))
            {
                surfaceAttestsForm = true;
                surfaceAttestsLiterally = true;
            }
        }

        // Expressive spelling (ほんっと, おーっと, そっかー) attests kana-native or priority words; on rare kanji it means mutation (ズクッ→木菟).
        if (!surfaceAttestsForm && surface is { Length: > 0 }
            && (!hasKanji || word.Priorities is { Count: > 0 }))
        {
            string detrailed = surface.TrimEnd(TrailingStretchChars);
            string degeminated = string.Concat(surface.Where(c => c is not ('っ' or 'ッ')));
            // Function words are unstressed: de-stretching わーい to the particle わい is noise.
            bool stretchable = word.Priorities is { Count: > 0 }
                               || word.PartsOfSpeech.Any(p => p is "int" or "exp");
            string destretched = stretchable
                ? string.Concat(surface.Where(c => c is not ('ー' or '〜')))
                : surface;
            string bare = stretchable
                ? string.Concat(detrailed.Where(c => c is not ('っ' or 'ッ' or 'ー' or '〜')))
                : detrailed;
            // The strip must not dominate: そっかー is そっか, but ああぁぁーー is scream material.
            if (detrailed.Length * 2 < surface.Length) detrailed = surface;
            if (bare.Length * 2 < surface.Length) bare = surface;
            // A trailing small vowel stands for the full vowel (ふぅ→ふう), except in vowel-initial screams (うぅ).
            string promoted = surface;
            if ("あいうえおぁぃぅぇぉアイウエオァィゥェォ".IndexOf(surface[0]) < 0)
            {
                int end = surface.Length;
                while (end > 0 && "ぁぃぅぇぉァィゥェォ".IndexOf(surface[end - 1]) >= 0) end--;
                if (end < surface.Length)
                {
                    var chars = surface.ToCharArray();
                    for (int i = end; i < surface.Length; i++)
                        chars[i] = chars[i] switch
                        {
                            'ぁ' => 'あ', 'ぃ' => 'い', 'ぅ' => 'う', 'ぇ' => 'え', 'ぉ' => 'お',
                            'ァ' => 'ア', 'ィ' => 'イ', 'ゥ' => 'ウ', 'ェ' => 'エ', 'ォ' => 'オ',
                            _ => chars[i]
                        };
                    promoted = new string(chars);
                }
            }
            foreach (var variant in (ReadOnlySpan<string>)[detrailed, degeminated, destretched, bare, promoted])
            {
                if (variant.Length == 0 || variant == surface) continue;
                if (word.Forms.Any(f => f.Text == variant))
                {
                    surfaceAttestsForm = true;
                    break;
                }
            }
        }

        return (isUk, hasKanji, readingIsIchi, surfaceAttestsForm, surfaceAttestsLiterally);
    }
}
