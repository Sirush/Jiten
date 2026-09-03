namespace Jiten.Parser;

public class KanaNormalizer
{
    private static readonly Dictionary<char, char> KanaToVowel = BuildKanaToVowelMap();

    private static Dictionary<char, char> BuildKanaToVowelMap()
    {
        var map = new Dictionary<char, char>();
        foreach (char c in "おこそとのほもよろをごぞどぼぽょオコソトノホモヨロヲゴゾドボポョ") map[c] = 'う';
        foreach (char c in "うくすつぬふむゆるぐずづぶぷゅウクスツヌフムユルグズヅブプュ") map[c] = 'う';
        foreach (char c in "えけせてねへめれげぜでべぺエケセテネヘメレゲゼデベペ") map[c] = 'え';
        foreach (char c in "いきしちにひみりぎじぢびぴイキシチニヒミリギジヂビピ") map[c] = 'い';
        foreach (char c in "あかさたなはまやらわがざだばぱゃアカサタナハマヤラワガザダバパャ") map[c] = 'あ';
        return map;
    }

    /// Rewrites every ー after a kana as that kana's vowel; a leading ー or one after a non-kana char stays.
    public static string Normalize(string input)
    {
        if (string.IsNullOrEmpty(input) || input.IndexOf('ー') == -1)
            return input;

        return string.Create(input.Length, input, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
            {
                char c = src[i];
                span[i] = c == 'ー' && i > 0 ? KanaToVowel.GetValueOrDefault(src[i - 1], 'ー') : c;
            }
        });
    }
}
