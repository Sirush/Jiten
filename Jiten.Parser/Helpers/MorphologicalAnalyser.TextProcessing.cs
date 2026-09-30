using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Utils;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    [GeneratedRegex(@"[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005\u3001-\u3003\u3008-\u3011\u3014-\u301F\uFF01-\uFF0F\uFF1A-\uFF1F\uFF3B-\uFF3F\uFF5B-\uFF60\uFF62-\uFF65．\n…\u3000―\u2500()。！？「」）|]")]
    private static partial Regex NonJapaneseCharRegex();

    // VN script ruby/emphasis markup: 癒#［１なお］す, も#［１・］ど (the digit is the base length).
    [GeneratedRegex(@"#［[0-9０-９]+[^］\n]*］")]
    private static partial Regex HashRubyAnnotationRegex();

    [GeneratedRegex(@"(?<=[\u3040-\u309F\u30A0-\u30FF])[～〜]+")]
    private static partial Regex TildeAfterKanaRegex();

    [GeneratedRegex(@"ー{2,}")]
    private static partial Regex MultipleLongVowelRegex();

    [GeneratedRegex(@"(?<=[一-龯])ー(?=[぀-ゟ])")]
    private static partial Regex EmphLongVowelKanjiHiraganaRegex();

    [GeneratedRegex(@"(?<!を)はやめ")]
    private static partial Regex HayameWithoutWoRegex();

    [GeneratedRegex(@"(?<!が)はやる")]
    private static partial Regex HayaruWithoutGaRegex();

    [GeneratedRegex(@"(?<!あ)やつれ")]
    private static partial Regex YatsureRegex();

    [GeneratedRegex(@"(外|家)出(ない|なかった|なく)")]
    private static partial Regex DeNaiCompoundRegex();

    [GeneratedRegex(@"(?<=.[\p{IsHiragana}\p{IsCJKUnifiedIdeographs}])(?<!うわ)([っッ])(?![かきくけこがぎぐげござじずぜぞさしすせそたちつてとだぢづでどぱぴぷぺぽばびぶべぼカキクケコガギグゲゴザジズゼゾサシスセソタチツテトダヂヅデドパピプペポバビブベボ\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex EmphaticTsuRegex();

    [GeneratedRegex(@"(?<=[\p{IsHiragana}\p{IsCJKUnifiedIdeographs}])…+(?=[っッ](?![かきくけこがぎぐげござじずぜぞさしすせそたちつてとだぢづでどぱぴぷぺぽばびぶべぼカキクケコガギグゲゴザジズゼゾサシスセソタチツテトダヂヅデドパピプペポバビブベボ\p{IsCJKUnifiedIdeographs}]))")]
    private static partial Regex EllipsisBeforeEmphaticTsuRegex();

    [GeneratedRegex(@"ホント(バカ|ダメ|マジ|クソ|アホ)")]
    private static partial Regex HontoKatakanaRegex();

    [GeneratedRegex(@"(?<!い)っしょ[ーう]?(?=[\s\n]|$)")]
    private static partial Regex ColloquialSshoRegex();

    [GeneratedRegex(@"(?<=[\u4E00-\u9FAF])番っ")]
    private static partial Regex BanCompoundTsuRegex();

    [GeneratedRegex(@"(?<=(?:どー|どう|そー|そう|こー|こう|ああ|あー))ゆう")]
    private static partial Regex ColloquialYuuRegex();

    // A lone kana closed by punctuation after the pause is its own interjection (だが…ん？ must not become がん).
    [GeneratedRegex(@"(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]{2})…+(?=[^\r\n…])(?![\p{IsHiragana}\p{IsKatakana}][？！?!、。」』）\r\n])")]
    private static partial Regex MidSentenceEllipsisRegex();

    [GeneratedRegex(@"…{2,}")]
    private static partial Regex EllipsisRunRegex();

    [GeneratedRegex(@"…(?=\r?\n(?![ 　]*[」』）]))")]
    private static partial Regex LineEndEllipsisRegex();

    [GeneratedRegex(@"[）)](?=\r?\n)")]
    private static partial Regex LineEndParenRegex();

    [GeneratedRegex(@"([ァ-ヴ]ンッ)(?=[ァ-ヴぁ-ゔ\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex KatakanaInterjectionTsuRegex();

    // Not after など (本などして) or a stem-final mora fronting もどす (取りもどして); でも/かも never end in those.
    [GeneratedRegex(@"(?<!な)(?<![りれびきちしい]も)どし(?=[たてよ])")]
    private static partial Regex ColloquialDoshiRegex();

    // ーっ after hiragana is emphatic (けどーっ → けど), except before と where it is a mimetic adverb (ぼーっと).
    [GeneratedRegex(@"(?<=[぀-ゟ])ー+[っッ]+(?!と)")]
    private static partial Regex EmphLongVowelSokuonRegex();

    // Small vowel stretching a mimetic adverb (すぅっと → すっと); same-vowel only, since ファ/ティ are digraphs.
    [GeneratedRegex(@"([ぁ-ゖァ-ヴ])([ぁぃぅぇぉァィゥェォ]+)(?=[っッ]と)")]
    private static partial Regex SmallVowelBeforeSokuonToRegex();

    private const string VowelRowA = "あかがさざただなはばぱまやらわゃぁアカガサザタダナハバパマヤラワャァ";
    private const string VowelRowI = "いきぎしじちぢにひびぴみりぃイキギシジチヂニヒビピミリィ";
    private const string VowelRowU = "うくぐすずつづぬふぶぷむゆるゅぅゔウクグスズツヅヌフブプムユルュゥヴ";
    private const string VowelRowE = "えけげせぜてでねへべぺめれぇエケゲセゼテデネヘベペメレェ";
    private const string VowelRowO = "おこごそぞとどのほぼぽもよろをょぉオコゴソゾトドノホボポモヨロヲョォ";

    private static int VowelRowOf(char c) =>
        VowelRowA.IndexOf(c) >= 0 ? 0 :
        VowelRowI.IndexOf(c) >= 0 ? 1 :
        VowelRowU.IndexOf(c) >= 0 ? 2 :
        VowelRowE.IndexOf(c) >= 0 ? 3 :
        VowelRowO.IndexOf(c) >= 0 ? 4 : -1;

    private static string CollapseSameVowelSmallBeforeSokuonTo(string text) =>
        SmallVowelBeforeSokuonToRegex().Replace(text, m =>
        {
            int row = VowelRowOf(m.Groups[1].Value[0]);
            return row >= 0 && m.Groups[2].Value.All(c => VowelRowOf(c) == row)
                ? m.Groups[1].Value
                : m.Value;
        });

    // Shouted stretch before a clause-final ー run (行けぇーー → 行けー): drop the small vowel, keep the ー.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])[ぁぃぅぇぉ](?=ー+([\s\n！？!?]|$))")]
    private static partial Regex SmallVowelBeforeFinalLongVowelRegex();

    // ー stretching a shouted final い (せんぱーい); hiragana only (ボーイ), and not おーい, なーい or a repeat (わーいわーい).
    [GeneratedRegex(@"(?<=[ぁ-ゖ][ぁ-ゖ])(?<!な)(?<!ー[ぁ-ゖ]{1,8})ー+(?=い([\s\n！？!?」』）]|$))")]
    private static partial Regex LongVowelBeforeFinalIRegex();

    // A small vowel after the opposite script (黙れェッ, ヤダぁ) is never a digraph; Sudachi shreds 黙れェ otherwise.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])([ァィゥェォ]+[っッ]?)|(?<=[ァ-ヴ])([ぁぃぅぇぉ]+[っッ]?)")]
    private static partial Regex ScriptCrossingSmallVowelRegex();

    // A run of 2+ hiragana small vowels is elongation (急げぇぇ → 急|げぇぇ otherwise); ゃゅょ digraphs are untouched.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])[ぁぃぅぇぉ]{2,}")]
    private static partial Regex SameScriptSmallVowelRunRegex();
    // A lone small vowel before clause-final っ is a shouted imperative (撃てぇっ!); すげぇ! and ねぇ stay.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])[ぁぃぅぇぉ]([っッ]+)(?=[\s\n]|$)")]
    private static partial Regex ShoutedImperativeSmallVowelRegex();

    // Stutter fragment (ぼ、ぼく); a preceding kana/kanji means a real particle or repeat (今は、はっきり, ええ、ええ).
    [GeneratedRegex(@"(?<![ぁ-んァ-ヶー一-龯々])([ぁ-んァ-ヶ])[っッ]?[、,，]\s*(?=\1)")]
    private static partial Regex StutterFragmentRegex();

    // 4+ identical kana are sound effects (ぼぼぼぼぼ); runs of 3 occur in real words (とっとと), left to MisparseGates.
    [GeneratedRegex(@"([ぁ-んァ-ヶ])([\sっッ]*\1){3,}")]
    private static partial Regex StutteringRunRegex();

    // 3+ identical digraph morae (じょじょじょ); small kana never start a mora, so each capture is one digraph.
    [GeneratedRegex(@"([ぁ-んァ-ヶ][ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮ])([\sっッ]*\1){2,}")]
    private static partial Regex StutteringDigraphRunRegex();

    // Quotative って's gemination steals 〜通り's り (計画+通+りって).
    [GeneratedRegex(@"(?<=通り|どおり)(?=って)")]
    private static partial Regex TooriQuotativeRegex();

    // Sudachi shifts a hiragana long-vowel tail into a katakana word (じゃあアヒル → ああ + ヒル).
    [GeneratedRegex(@"(?<=[ぁ-ゖ][あぁ])(?=[ァ-ヴ])")]
    private static partial Regex VowelTailKatakanaBoundaryRegex();

    // Sudachi's spurious もしカ (2133220) steals a katakana name's first mora (もしカ|ティア); もしも/もしか stay.
    [GeneratedRegex(@"もし(?=[ァ-ヴ])")]
    private static partial Regex MoshiKatakanaBoundaryRegex();

    // Sudachi fuses を/へ + quotative って into a bogus verb (ことをって); no word contains をって/へって.
    [GeneratedRegex(@"(?<=[をへ])(?=って)")]
    private static partial Regex CaseParticleTteRegex();

    // っしょ after an i-adjective (すごいっしょ), which ColloquialSshoRegex's い guard blocks; と excluded for ずっと一緒.
    [GeneratedRegex(@"(?<=[ぁ-ゖ]い)(?<!とい)っしょ[ーう]?(?=[\s\n]|$)")]
    private static partial Regex IAdjSshoRegex();

    // Sudachi shreds a demonstrative before って (こい|つっ|て, あ|そ|こっ|て).
    [GeneratedRegex(@"([こそあど]いつ|ここ|そこ|あそこ|どこ)って")]
    private static partial Regex DemonstrativePronounTteRegex();

    // 景気づけよ → 景気づけ (景気付け 2010780) + よ; NOT the volitional 景気づけよう.
    [GeneratedRegex(@"景気づけよ(?!う)")]
    private static partial Regex KeikizukeYoRegex();

    // Sudachi OOV-swallows らって after a pronoun's plural ら (キミ|らってやっぱり).
    [GeneratedRegex(@"(?<=(?:キミ|きみ|君|僕|ぼく|俺|おれ|お前|おまえ|あいつ|こいつ|そいつ|あなた|彼|彼女|私|わたし|あたし|うち)ら)(?=って)")]
    private static partial Regex PronounRaTteRegex();

    // Rough-speech elongated ある after a particle (覚えがあらァ, 金ならあらぁ → がある/ならある).
    [GeneratedRegex(@"(が|なら)あら[ァぁ]")]
    private static partial Regex ElongatedAruRegex();

    // Copula っす (2269410) after an i-adjective, else swallowed into a noun (いいっすか → イスカ); gated on what follows.
    [GeneratedRegex(@"(?<=[ぁ-ゖ]い)(?<!とい)っす(?=[かよねぞなわぜさ、。！？…」]|けど|し|もん|から|が|[\s\n]|$)")]
    private static partial Regex IAdjSsuRegex();

    // Han characters outside Shift_JIS (爱, 们, 这): simplified Chinese, never Japanese text.
    private static readonly bool[] NonShiftJisHan = BuildNonShiftJisHan();

    private static bool[] BuildNonShiftJisHan()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sjis = Encoding.GetEncoding(932, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
        var map = new bool[0x9FFF - 0x4E00 + 1];
        Span<char> one = stackalloc char[1];
        Span<byte> bytes = stackalloc byte[8];
        for (int c = 0x4E00; c <= 0x9FFF; c++)
        {
            one[0] = (char)c;
            map[c - 0x4E00] = sjis.GetBytes(one, bytes) == 1 && bytes[0] == (byte)'?';
        }

        return map;
    }

    private static bool IsNonShiftJisHan(char c) => c is >= '\u4E00' and <= '\u9FFF' && NonShiftJisHan[c - 0x4E00];

    // Subtitle archives interleave Chinese tracks; kana-less lines with 2+ non-Shift_JIS hanzi are dropped.
    private static string StripChineseLines(string text)
    {
        bool any = false;
        foreach (var c in text)
            if (IsNonShiftJisHan(c)) { any = true; break; }
        if (!any) return text;

        var lines = text.Split('\n');
        for (int l = 0; l < lines.Length; l++)
        {
            int nonSjis = 0;
            bool hasKana = false;
            foreach (var c in lines[l])
            {
                if (JapaneseTextHelper.IsKana(c)) { hasKana = true; break; }
                if (IsNonShiftJisHan(c)) nonSjis++;
            }

            if (!hasKana && nonSjis >= 2)
                lines[l] = lines[l].EndsWith('\r') ? "\r" : "";
        }

        return string.Join('\n', lines);
    }

    private void PreprocessText(ref string text, bool preserveStopToken, out int rawContentCharCount)
    {
        text = StripChineseLines(text);
        text = HashRubyAnnotationRegex().Replace(text, "");
        text = text.Replace("<", " ").Replace(">", " ").Replace("〝", " ").Replace("〟", " ");
        text = text.Replace('‥', '…');
        text = text.ToFullWidthDigits();
        text = NonJapaneseCharRegex().Replace(text, "");

        rawContentCharCount = CountContentChars(text);

        if (!preserveStopToken)
            text = text.Replace(_stopToken, "");

        text = LineEndParenRegex().Replace(text, m => m.Value + LineEndParenMark);

        text = text
            .Replace("「", "\n「 ")
            .Replace("」", " 」\n")
            .Replace("〈", " \n〈 ")
            .Replace("〉", " 〉\n")
            .Replace("\n（", " （")
            .Replace("）", " ）\n")
            .Replace("《", " \n《 ")
            .Replace("》", " 》\n")
            .Replace("\u201C", " \n\u201C ")
            .Replace("\u201D", " \u201D\n")
            .Replace("―", " ― ")
            .Replace("。", "\n。\n")
            .Replace("！", "\n！\n")
            .Replace("？", "\n？\n");

        text = TildeAfterKanaRegex().Replace(text, "ー");
        text = MultipleLongVowelRegex().Replace(text, "ー");
        text = EmphLongVowelKanjiHiraganaRegex().Replace(text, "");
        text = EmphLongVowelSokuonRegex().Replace(text, "");
        text = CollapseSameVowelSmallBeforeSokuonTo(text);
        text = SmallVowelBeforeFinalLongVowelRegex().Replace(text, "");
        // Before the script-crossing split, which would strand あら as the interjection; clause-initial あらぁ stays.
        text = ElongatedAruRegex().Replace(text, "$1ある");
        text = ScriptCrossingSmallVowelRegex().Replace(text, $"{_stopToken}$1$2");
        text = SameScriptSmallVowelRunRegex().Replace(text, "");
        text = ShoutedImperativeSmallVowelRegex().Replace(text, "$1");
        // After the small-vowel deletions: おぉぉーーい must reduce to おーい, not おい.
        text = LongVowelBeforeFinalIRegex().Replace(text, "");

        text = StutterFragmentRegex().Replace(text, "");
        text = StutteringDigraphRunRegex().Replace(text, "");
        text = StutteringRunRegex().Replace(text, "");

        text = text
            .Replace("垣間見", $"垣間{_stopToken}見")
            .Replace("今手", $"今{_stopToken}手");
        text = HayameWithoutWoRegex().Replace(text, $"は{_stopToken}やめ");
        text = text.Replace("もやる", $"も{_stopToken}やる");
        text = HayaruWithoutGaRegex().Replace(text, $"は{_stopToken}やる");
        // Quotative って fragments やる into や+る; after the はやる split so 流行る is unaffected.
        text = text.Replace("やるって", $"やる{_stopToken}って");
        // Quotative って after なんとなく, else re-read as なんと + なくって.
        text = text.Replace("なんとなくって", $"なんとなく{_stopToken}って");
        text = text
            .Replace("ええんや", $"ええ{_stopToken}んや")
            .Replace("べや", $"べ{_stopToken}や")
            .Replace("はいい", $"は{_stopToken}いい")
            .Replace("元国王", $"元{_stopToken}国王")
            .Replace("なんだろう", $"なん{_stopToken}だろう")
            .Replace("一人静かに", $"一人{_stopToken}静かに")
            .Replace("いやあんま", $"いや{_stopToken}あんま")
            .Replace("この手紙", $"この{_stopToken}手紙")
            .Replace("少女の手", $"少女{_stopToken}の手")
            .Replace("はたまたま", $"は{_stopToken}たまたま")
            .Replace("悶え苦しむ", $"悶え{_stopToken}苦しむ")
            .Replace("悶え苦しん", $"悶え{_stopToken}苦しん")
            // すぐそこ (user_dic) must not eat the すぐ of もうすぐ
            .Replace("もうすぐそこ", $"もうすぐ{_stopToken}そこ")
            ;

        // Kana すみません collides with the verb 済む; すいませんでした is covered by a user_dic entry.
        text = text
            .Replace("すみませんでした", $"すみません{_stopToken}でした")  // すみ|ませんでした
            .Replace("この世界", $"この{_stopToken}世界")                  // この世+界 → この|世界
            .Replace("だけって", $"だけ{_stopToken}って")                  // 広がっ+ただけ phantom けっ → だけ|って
            .Replace("ははーん", "ははん")                                // は+はーん(ハーン khan) → ははん(2096970)
            .Replace("にって", $"に{_stopToken}って")                     // にっ+てこ blob → に + って + こと
            .Replace("なんかいな", $"なんか{_stopToken}いな")             // なんか+い stolen → なんか + いない
            .Replace("繋がりって", $"繋がり{_stopToken}って")             // 繋|が|り shredded by って → 繋がり + って
            .Replace("んったら", $"ん{_stopToken}ったら")                 // ちゃ|んっ|たら → ちゃん + ったら
            .Replace("にいる", $"に{_stopToken}いる")                     // にいる(name 5408860) → に + いる(居る)
            .Replace("さっきこ", $"さっき{_stopToken}こ")                 // sokuon-norm reads name さきこ(咲子)
            .Replace("ないっていう", $"ない{_stopToken}っていう")          // って must not attach left into 〜ない expr
            // In hiragana text が行/は行 is always particle + 行〜 (母が行かせまい, が行方); row nouns keep katakana ガ行.
            .Replace("が行", $"が{_stopToken}行")                         // ガ行(1040670) fusion → が + 行〜
            .Replace("は行", $"は{_stopToken}行")                         // ハ行(1096940) fusion → は + 行〜
            // 金のこ (hacksaw) swallows 金のこと; gated on のこと so 金のこで切る keeps its entry.
            .Replace("金のこと", $"金{_stopToken}のこと")
            // 誰's だあれ must not eat なんだ's copula; keyed on なんだ/何だ so child-speech お姉さんだあれ？ keeps 誰.
            .Replace("なんだあれ", $"なんだ{_stopToken}あれ")
            .Replace("何だあれ", $"何だ{_stopToken}あれ")
            // Sudachi's てく contraction steals the く of くだせえ (勘弁して|く|だ|せえ).
            .Replace("てくだせえ", $"て{_stopToken}くだせえ")
            // Sudachi fuses るっす into a noun shard (わかるっす); る never ends a word before っす otherwise.
            .Replace("るっす", $"る{_stopToken}っす")
            // です steals the す of 済まして (顔ですましている); です+まし never occurs in prose.
            .Replace("ですまし", $"で{_stopToken}すまし")
            // After an i-adjective the いっ shard reads as 行ったら; the full tail keeps 会いに行ったら.
            .Replace("いったらありゃしない", $"い{_stopToken}ったらありゃしない")
            // Sudachi cuts ２つ|も|らって (ラッテ); 積もる's te-form is 積もって, so つもらっ has no other reading.
            .Replace("つもらっ", $"つ{_stopToken}もらっ")
            // Sudachi hands たい's い to 行って (知りた|いって); the cut also suits 重たいって and 鯛って.
            .Replace("たいって", $"たい{_stopToken}って")
            // Sudachi fuses るっていう into a dropped blob; る never ends a word before っていう otherwise.
            .Replace("るっていう", $"る{_stopToken}っていう");
        text = TooriQuotativeRegex().Replace(text, _stopToken);
        text = VowelTailKatakanaBoundaryRegex().Replace(text, _stopToken);
        text = MoshiKatakanaBoundaryRegex().Replace(text, $"もし{_stopToken}");
        text = CaseParticleTteRegex().Replace(text, _stopToken);
        text = DemonstrativePronounTteRegex().Replace(text, $"$1{_stopToken}って");
        text = PronounRaTteRegex().Replace(text, _stopToken);
        text = KeikizukeYoRegex().Replace(text, $"景気づけ{_stopToken}よ");

        text = text.Replace('頚', '頸');

        text = text.Replace("前出すぎ", $"前{_stopToken}出すぎ");

        text = DeNaiCompoundRegex().Replace(text, $"$1{_stopToken}出$2");
        text = text.Replace("届出さ", $"届{_stopToken}出さ");
        // Sudachi's archaic 射出す(いだす) eats the noun 射出 before される/して
        text = text.Replace("射出さ", $"射出{_stopToken}さ");
        text = text.Replace("射出し", $"射出{_stopToken}し");
        // Anchors user_dic entries against connection costs: やつれ (not 操れ), 小木曽, するすると.
        text = YatsureRegex().Replace(text, $"{_stopToken}やつれ");
        // quotative と + かぶりを振る: SpecialCases とか otherwise steals the か (と|か|ぶり)
        text = text.Replace("とかぶりを振", $"と{_stopToken}かぶりを振");
        // Sudachi's 虫を殺す is the rare temper idiom; fiction means literal insect-killing.
        text = text.Replace("虫を殺", $"虫を{_stopToken}殺");
        text = text.Replace("小木曽", $"小木曽{_stopToken}");
        text = text.Replace("するすると", $"{_stopToken}するすると");
        text = text.Replace("ぶっち切", "ぶち切");
        // The playful おっはよー is おはよう; left alone the emphatic-っ boundary strands お (→ 尾).
        text = text.Replace("おっはよ", "おはよ");
        text = EllipsisBeforeEmphaticTsuRegex().Replace(text, "");
        text = EmphaticTsuRegex().Replace(text, $"{_stopToken}$1");
        // After EmphaticTsuRegex, else it cuts ぶ|っ and が+ぶ merges; ぶっ壊れる has no JMDict entry, so ぶっ (2698210) + 壊れた.
        text = text.Replace("ぶっ壊れ", $"{_stopToken}ぶっ{_stopToken}壊れ");
        text = BanCompoundTsuRegex().Replace(text, $"番{_stopToken}っ");

        text = text
            .Replace("水魔法", $"水{_stopToken}魔法")
            .Replace("不適応", $"不{_stopToken}適応")
            .Replace("首落と", $"首{_stopToken}落と")
            .Replace("面の皮", $"面{_stopToken}の皮")
            .Replace("たっけ", $"た{_stopToken}っけ");

        text = HontoKatakanaRegex().Replace(text, $"ホント{_stopToken}$1");
        text = KatakanaInterjectionTsuRegex().Replace(text, $"$1{_stopToken}");

        text = text
            .Replace("バカバカ", $"バカ{_stopToken}バカ")
            .Replace("事大", $"事{_stopToken}大")
            // Sudachi cuts 前大 (surname) + 戦
            .Replace("前大戦", $"前{_stopToken}大戦")
            .Replace("人魚姫", $"人魚{_stopToken}姫")
            .Replace("日間", $"日{_stopToken}間")
            .Replace("何本", $"何{_stopToken}本")
            .Replace("年未公開", $"年{_stopToken}未公開")
            .Replace("足元気", $"足元{_stopToken}気");

        text = text
            .Replace("来イ", "来い")
            .Replace("にちがいねえ", "にちがいない")
            .Replace("せぇ", "さい")
            .Replace("くせー", "くさい")
            .Replace("ですぅ", "です")
            .Replace("ごめんなさいっ", "ごめんなさい");

        text = text.Replace("でもちょっと", $"でも{_stopToken}ちょっと");
        text = text.Replace("できんよう", $"できん{_stopToken}よう");
        text = ColloquialSshoRegex().Replace(text, $"{_stopToken}っしょ");
        text = IAdjSshoRegex().Replace(text, $"{_stopToken}っしょ");
        text = IAdjSsuRegex().Replace(text, $"{_stopToken}っす");

        text = ColloquialDoshiRegex().Replace(text, "どうし");
        text = ColloquialYuuRegex().Replace(text, "いう");

        text = EllipsisRunRegex().Replace(text, "…");
        text = MidSentenceEllipsisRegex().Replace(text, "");
        text = LineEndEllipsisRegex().Replace(text, "。");
    }

    private static int CountContentChars(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c is >= '぀' and <= 'ゟ'   // hiragana
                  or >= '゠' and <= 'ヿ'    // katakana (incl. ー)
                  or >= '一' and <= '龯'    // CJK
                  or '々'
                  or >= 'Ａ' and <= 'Ｚ'    // fullwidth A-Z
                  or >= 'ａ' and <= 'ｚ'    // fullwidth a-z
                  or >= '０' and <= '９')   // fullwidth 0-9
                count++;
        }
        return count;
    }

    private static void ComputeTokenOffsets(string originalText, List<WordInfo> wordInfos)
    {
        var text = originalText.Replace("\r", "").Replace("\n", "");
        int pos = 0;
        foreach (var word in wordInfos)
        {
            if (string.IsNullOrEmpty(word.Text) || word.PartOfSpeech == PartOfSpeech.BlankSpace)
                continue;

            int found = text.IndexOf(word.Text, pos, StringComparison.Ordinal);
            if (found >= 0)
            {
                word.StartOffset = found;
                word.EndOffset = found + word.Text.Length;
                pos = word.EndOffset;
            }
        }
    }

    /// <summary>Strips the ） marks; returns their positions in the text with line breaks removed.</summary>
    private static HashSet<int> TakeLineEndParenMarks(ref string text)
    {
        var positions = new HashSet<int>();
        if (text.IndexOf(LineEndParenMark) < 0)
            return positions;

        var sb = new StringBuilder(text.Length);
        int flatPos = 0;
        foreach (char c in text)
        {
            if (c == LineEndParenMark)
            {
                positions.Add(flatPos - 1);
                continue;
            }

            sb.Append(c);
            if (c is not ('\r' or '\n'))
                flatPos++;
        }

        text = sb.ToString();
        return positions;
    }

    private List<SentenceInfo> SplitIntoSentences(string text, List<WordInfo> wordInfos, HashSet<int> lineEndParens)
    {
        text = text.Replace("\r", "").Replace("\n", "");

        // Start positions give O(1) sentence lookup by offset.
        var sentenceData = new List<(SentenceInfo info, int startPos)>();
        var sb = new StringBuilder();
        bool seenEnder = false;
        int sentenceStartPos = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            sb.Append(current);

            if (_sentenceEnders.Contains(current) || lineEndParens.Contains(i))
            {
                seenEnder = true;
                continue;
            }

            if (seenEnder)
            {
                if (_sentenceEnders.Contains(current))
                    continue;

                // The current character belongs to the next sentence.
                var sentenceText = sb.ToString(0, sb.Length - 1);
                sentenceData.Add((new SentenceInfo(sentenceText), sentenceStartPos));

                sentenceStartPos = i;
                sb.Clear();
                sb.Append(current);
                seenEnder = false;
            }
        }

        if (sb.Length > 0)
        {
            sentenceData.Add((new SentenceInfo(sb.ToString()), sentenceStartPos));
        }

        if (sentenceData.Count == 0)
            return [];

        // Offsets come from raw Sudachi output and survive merge/split stages; IndexOf breaks when a stage edits Text.
        int sentenceIdx = 0;

        foreach (var word in wordInfos)
        {
            if (string.IsNullOrEmpty(word.Text) || word.PartOfSpeech == PartOfSpeech.BlankSpace)
                continue;

            if (word.StartOffset < 0 || word.EndOffset < 0)
                continue;

            int wordPos = word.StartOffset;
            int wordEnd = word.EndOffset;

            while (sentenceIdx < sentenceData.Count - 1)
            {
                int nextSentenceStart = sentenceData[sentenceIdx + 1].startPos;
                if (wordPos < nextSentenceStart)
                    break;
                sentenceIdx++;
            }

            var (sentence, sentenceStart) = sentenceData[sentenceIdx];
            int sentenceEnd = sentenceStart + sentence.Text.Length;

            // A word spanning a boundary merges the sentences.
            while (wordEnd > sentenceEnd && sentenceIdx + 1 < sentenceData.Count)
            {
                var nextSentence = sentenceData[sentenceIdx + 1].info;
                sentence.Text += nextSentence.Text;
                sentenceData.RemoveAt(sentenceIdx + 1);
                sentenceEnd = sentenceStart + sentence.Text.Length;
            }

            int posInSentence = wordPos - sentenceStart;
            int spanLength = wordEnd - wordPos;
            sentence.Words.Add((word, posInSentence, spanLength));
        }

        return sentenceData.Select(s => s.info).ToList();
    }
}
