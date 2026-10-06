using System.Text;

namespace Jiten.Parser.Romanization;

/// <summary>Modified Hepburn with long vowels spelled out (おう → ou, ー repeats the vowel), the convention AniList and VNDB use for titles.</summary>
public static class HepburnRomanizer
{
    private static readonly Dictionary<char, string> Syllables = new()
    {
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['ゐ'] = "i", ['ゑ'] = "e", ['を'] = "wo",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ゔ'] = "vu",
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
        ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo", ['ゎ'] = "wa", ['ゕ'] = "ka", ['ゖ'] = "ke",
        ['ヷ'] = "va", ['ヸ'] = "vi", ['ヹ'] = "ve", ['ヺ'] = "vo",
    };

    private static readonly Dictionary<string, string> Digraphs = BuildDigraphs();

    private static Dictionary<string, string> BuildDigraphs()
    {
        var digraphs = new Dictionary<string, string>
        {
            ["しぇ"] = "she", ["じぇ"] = "je", ["ちぇ"] = "che", ["いぇ"] = "ye",
            ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo", ["ふゅ"] = "fyu",
            ["てぃ"] = "ti", ["でぃ"] = "di", ["てゅ"] = "tyu", ["でゅ"] = "dyu", ["とぅ"] = "tu", ["どぅ"] = "du",
            ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo",
            ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo", ["ゔゅ"] = "vyu",
            ["つぁ"] = "tsa", ["つぃ"] = "tsi", ["つぇ"] = "tse", ["つぉ"] = "tso",
            ["くぁ"] = "kwa", ["くぃ"] = "kwi", ["くぇ"] = "kwe", ["くぉ"] = "kwo", ["ぐぁ"] = "gwa",
            ["すぃ"] = "si", ["ずぃ"] = "zi",
        };

        (char Kana, string Onset)[] iRow =
        [
            ('き', "ky"), ('ぎ', "gy"), ('し', "sh"), ('じ', "j"), ('ち', "ch"), ('ぢ', "j"), ('に', "ny"),
            ('ひ', "hy"), ('び', "by"), ('ぴ', "py"), ('み', "my"), ('り', "ry"),
        ];
        foreach (var (kana, onset) in iRow)
        {
            digraphs[$"{kana}ゃ"] = onset + "a";
            digraphs[$"{kana}ゅ"] = onset + "u";
            digraphs[$"{kana}ょ"] = onset + "o";
        }

        return digraphs;
    }

    public static string ToRomaji(string kana)
    {
        var text = ToHiragana(kana);
        var sb = new StringBuilder(text.Length * 2);
        var geminate = false;

        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c is 'っ')
            {
                geminate = true;
                continue;
            }

            if (c is 'ー')
            {
                if (sb.Length > 0 && IsVowel(sb[^1])) sb.Append(sb[^1]);
                continue;
            }

            if (c is 'ん')
            {
                sb.Append('n');
                // Disambiguates ren'ai (れんあい) from renai (れない)
                if (i + 1 < text.Length && text[i + 1] is 'あ' or 'い' or 'う' or 'え' or 'お' or 'や' or 'ゆ' or 'よ')
                    sb.Append('\'');
                geminate = false;
                continue;
            }

            string? syllable = null;
            if (i + 1 < text.Length && Digraphs.TryGetValue(text.Substring(i, 2), out var digraph))
            {
                syllable = digraph;
                i++;
            }
            else if (Syllables.TryGetValue(c, out var single))
            {
                syllable = single;
            }

            if (syllable == null)
            {
                geminate = false;
                sb.Append(c);
                continue;
            }

            if (geminate && !IsVowel(syllable[0]))
                sb.Append(syllable.StartsWith("ch", StringComparison.Ordinal) ? 't' : syllable[0]);
            geminate = false;
            sb.Append(syllable);
        }

        return sb.ToString();
    }

    private static string ToHiragana(string kana) =>
        string.Create(kana.Length, kana, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
                span[i] = src[i] is >= 'ァ' and <= 'ヶ' ? (char)(src[i] - 0x60) : src[i];
        });

    private static bool IsVowel(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o';
}
