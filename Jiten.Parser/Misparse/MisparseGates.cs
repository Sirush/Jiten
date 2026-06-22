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
    bool IsSentenceInitial = false);

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

        return default;
    }

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

    private static bool IsAllKatakana(string text)
    {
        foreach (var c in text)
            if (c is < '゠' or > 'ヿ') return false;
        return text.Length > 0;
    }

    private static bool IsShortKanaNameWithoutContext(in MisparseGateContext ctx)
    {
        if (!WanaKana.IsKana(ctx.Token.Text)) return false;
        if (ctx.Token.Text.Length > 2) return false;
        if (!ctx.SelectedWord.PartsOfSpeech.Contains(PartOfSpeech.Name)) return false;
        if (ctx.Token.IsPersonNameContext) return false;
        if (IsAllKatakana(ctx.Token.Text)) return false;

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

        if (IsAllKatakana(surface)) return false;

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

    public static (bool isUsuallyKana, bool hasKanjiSpelling, bool readingIsIchi) GetWordFlags(
        JmDictWord? word, byte readingIndex)
    {
        if (word == null) return (false, true, false);

        bool isUk = word.PartsOfSpeech.Contains("uk");
        bool hasKanji = word.Forms.Any(f => f.FormType == JmDictFormType.KanjiForm);

        bool readingIsIchi = word.Priorities?.Contains("jiten") == true
                             || word.Forms.Any(f => f.FormType == JmDictFormType.KanaForm
                                                    && f.ReadingIndex == readingIndex
                                                    && f.Priorities != null
                                                    && (f.Priorities.Contains("ichi1") || f.Priorities.Contains("ichi2")
                                                        || f.Priorities.Contains("jiten")));

        return (isUk, hasKanji, readingIsIchi);
    }
}
