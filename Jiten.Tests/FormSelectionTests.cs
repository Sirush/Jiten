using Jiten.Core;
using Jiten.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Jiten.Tests;

using Xunit;
using FluentAssertions;

public class FormSelectionTests
{
    private static IDbContextFactory<JitenDbContext>? _contextFactory;

    private async Task<List<DeckWord>> Parse(string text)
    {
        if (_contextFactory == null)
        {
            var configuration = new ConfigurationBuilder()
                                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                                .AddJsonFile("sharedsettings.json", optional: false)
                                .AddEnvironmentVariables()
                                .Build();

            var optionsBuilder = new DbContextOptionsBuilder<JitenDbContext>();
            optionsBuilder.UseNpgsql(configuration.GetConnectionString("JitenDatabase"));

            _contextFactory = new PooledDbContextFactory<JitenDbContext>(optionsBuilder.Options);
        }

        return await Jiten.Parser.Parser.ParseText(_contextFactory, text);
    }

    public static IEnumerable<object[]> FormSelectionCases()
    {
        // Pure kana word should win over secondary katakana form of a kanji word
        // ママ (mama, 1129240) should beat まま (as-is, 1585410) form ママ
        yield return ["ママ", "ママ", 1129240, (byte)0];

        // Hiragana まま should resolve to まま (as-is, 1585410), not mama
        yield return ["まま", "まま", 1585410, (byte)2];

        // オレ should resolve to 俺 (1576870), not olé (2768550)
        yield return ["オレ", "オレ", 1576870, (byte)3];

        // Pure kana gairaigo: テレビ has only kana forms
        yield return ["テレビ", "テレビ", 1080510, (byte)0];

        // 食べている should resolve to 食べる (1358280)
        yield return ["食べている", "食べている", 1358280, (byte)0];

        // パパ (papa, 1102140) — standalone kana word, no conflict
        yield return ["パパ", "パパ", 1102140, (byte)0];

        // いらない should resolve to 要る (1546640, uk), not 入る (1465580)
        yield return ["礼なんていらない", "いらない", 1546640, (byte)1];

        // いえない should resolve to 言える (1008860), not 癒える (1538740)
        // Both score identically — tie-broken by lower WordId
        yield return ["いえない", "いえない", 1008860, (byte)2];

        // いえる should resolve to 言える (1008860, "to be able to say"), not 癒える (1538740, "to be healed")
        // 言える has arch on one definition only — IsFullyArchaic=false prevents the -200 penalty
        yield return ["狂信的ともいえる技術信仰", "いえる", 1008860, (byte)2];

        // 得る should resolve to える (1588760, v1 ichidan), not うる (1454500, v2a-s archaic nidan)
        // Sudachi returns ウル but archaic POS penalty counteracts the ReadingMatchScore
        yield return ["得る", "得る", 1588760, (byte)0];

        yield return ["得るべし", "得る", 1588760, (byte)0];

        // 身体 in isolation: Sudachi gives reading シンタイ, so しんたい (2830705) wins via ReadingMatchScore.
        // Ruby priors favor からだ but ReadingMatchScore (+70) outweighs ruby (+30/-30 = 60pt swing).
        yield return ["身体", "身体", 2830705, (byte)0];

        // 石 should resolve to いし/rock (1382440), not こく/unit of volume (1382450)
        // Sudachi reading イシ disambiguates between homographic kanji entries
        yield return ["石", "石", 1382440, (byte)0];

        // 忌々しい should resolve to いまいましい/annoying (1587580), not ゆゆしい/grave (1605760)
        // Sudachi reading イマイマシイ disambiguates between entries sharing the same kanji form
        yield return ["忌々しい", "忌々しい", 1587580, (byte)0];

        // 泥濘 should resolve to ぬかるみ/mud (1437020), not でいねい (2863417)
        // User dic overrides Sudachi reading to ヌカルミ for the common everyday reading
        yield return ["泥濘", "泥濘", 1437020, (byte)0];

        // 表へ出る — 表 should be おもて/surface (1489340), not ひょう/chart (1489350)
        // FixReadingAmbiguity overrides Sudachi ヒョウ→オモテ when followed by directional particle without preceding noun
        yield return ["表へ出る", "表", 1489340, (byte)0];

        // メニュー表 — 表 should be ひょう/chart (1489350) when preceded by a noun
        yield return ["メニュー表を見る", "表", 1489350, (byte)0];

        // 一日 standalone → いちにち (1576260), not ついたち (2225040)
        // FixReadingAmbiguity overrides Sudachi ツイタチ→イチニチ when not preceded by a month
        yield return ["一日でこれだけやれば", "一日", 1576260, (byte)0];

        // １日 (fullwidth numeral) → いちにち (1576260)
        yield return ["忘れられない１日が", "１日", 1576260, (byte)1];

        // 七月一日 → ついたち (2225040) — month context preserves the date reading
        yield return ["七月一日に生まれた", "一日", 2225040, (byte)1];

        // 寒気がする → さむけ/chills (1210410), not かんき/cold air (2866134)
        // FixReadingAmbiguity overrides Sudachi カンキ→サムケ when followed by が+する
        yield return ["寒気がした", "寒気", 1210410, (byte)0];

        // 開いた → ひらく (1202440), not あく (1586270)
        // Stem-based ReadingMatchScore: Sudachi ヒライ stem ひら matches ひらく kana form
        yield return ["本を開いた", "開いた", 1202440, (byte)0];

        // だろう after an adverb — Sudachi tags as 助動詞 (dict form だ), should resolve to
        // だろう expression (1928670), not だる aux-v (2867372)
        yield return ["どうだろう", "だろう", 1928670, (byte)0];

        // 初めまして — Sudachi tags as 感動詞 (Interjection), should resolve to the expression
        // 初めまして (1625780), not deconjugate to 初める aux-v (1342560)
        yield return ["初めまして", "初めまして", 1625780, (byte)0];

        // よくしています — should resolve to 良くする (2257610, uk), not 浴する (2255500)
        // GetPriorityScore uk bonus disambiguates when compound lookup returns both
        yield return ["私はある２つのことをよくしています", "よくしています", 2257610, (byte)3];

        // あの before non-noun → interjection "um" (1000430), not prenominal "that" (1000420)
        yield return ["あのすみません", "あの", 1000430, (byte)0];
        yield return ["あの、もし嫌だったらいいんだけど", "あの", 1000430, (byte)0];

        // あの before noun → prenominal adjective "that" (1000420)
        yield return ["あの時は大変だった", "あの", 1000420, (byte)1];

        // Sudachi misclassifies あの as 感動詞 — FixReadingAmbiguity overrides to PrenounAdjectival
        yield return ["ちなみにあのネバネバ粘液はしっとり濃厚化粧水。", "あの", 1000420, (byte)1];
        yield return ["実はあの〝名無し〟とは少々因縁があってな。", "あの", 1000420, (byte)1];
        yield return ["柔らかく、冷たいあの見慣れた顔。", "あの", 1000420, (byte)1];
        yield return ["音無さんのあの『パンツ』発言。", "あの", 1000420, (byte)1];

        // 禍 standalone → わざわい/disaster (1295080), not か (2844158)
        // FixReadingAmbiguity overrides Sudachi カ→ワザワイ for standalone 禍
        yield return ["大戦の禍に飲み込まれた", "禍", 1295080, (byte)1];

        // 空 as から (empty, 1245280) — Sudachi gives 形状詞/ウツロ, ProcessSpecialCases overrides to noun/カラ
        yield return ["妹のベッドも空なのか？", "空", 1245280, (byte)0];

        // 空 as そら (sky, 1245290) — Sudachi gives 名詞/ソラ, ReadingMatchScore disambiguates
        yield return ["空を見上げた", "空", 1245290, (byte)0];

        // 君 as くん/Mr (1247260), not ぎみ (2697530)
        // Common word with one archaic sense: no arch penalty (has ichi1), ReadingMatchScore disambiguates
        yield return ["６君にはＡｎｃｉｅｎｔ　Ｏｒｄｅｒの廃墟まで行ってもらう。", "君", 1247260, (byte)0];

        // 後 as あと/after (1269320) in kanji — Sudachi reading アト disambiguates vs うしろ (1269410)
        yield return ["一瞬の溜めの後、少女は勢いよく俺を引っ張った。", "後", 1269320, (byte)0];

        // あと in kana — 後/after (1269320, nf01) beats 跡/trace (1383680, nf03) via top-band nf granularity
        yield return ["あとＴｈｉｅｖｅｓ　Ｃｉｒｃｌｅが例のごとくチンケな問題を起こしている。", "あと", 1269320, (byte)1];

        // 後 as あと before numeral — FixReadingAmbiguity overrides Sudachi ゴ→アト
        yield return ["今死んどけば後何年も苦しまずに済むぞ。", "後", 1269320, (byte)0];

        // メンドイ in katakana should select the katakana form (index 2), not the hiragana めんどい (index 1)
        // The katakana form is tagged sk (search-only) but exact surface match preserves it
        yield return ["メンドイ", "メンドイ", 2078360, (byte)2];

        // 因 standalone → いん/cause (1168640), not もと/origin (1260670)
        // User dic overrides Sudachi reading モト→イン for the common standalone reading
        yield return ["因の情報", "因", 1168640, (byte)0];
        yield return ["因と果の間で", "因", 1168640, (byte)0];
        yield return ["因である", "因", 1168640, (byte)0];

        // Tilde/ー as vowel elongation resolves short katakana adj-i stems
        // ヤバ～ → ヤバい (やばい, 1012840) — tilde normalised, adj stem resolved
        yield return ["ヤバ～", "ヤバい", 1012840, (byte)1];
        // スゴ～ → スゴい (すごい, 1374550) — same pattern
        yield return ["スゴ～", "スゴい", 1374550, (byte)3];

        // いけなかった → いけない (1000730), not いける (1587190)
        // Deep deconjugation chain scales down Sudachi's lemma match
        yield return ["あのいけなかったでしょうか", "いけなかった", 1000730, (byte)1];

        // いかん → 行かん expression (2829697), not 移管 suru-noun (1158230)
        // Suru-noun identity matches are filtered when DictionaryForm confirms a deconjugated verb base
        yield return ["いかんいかん、心の声が漏れそうになった。", "いかん", 2829697, (byte)1];
        yield return ["戦線を伸ばしきるわけにもいかんしこのあたりが限界か", "いかん", 2829697, (byte)1];

        // Numeral+counter words: Sudachi DictionaryForm=つ must not match colloquial つ (2798260)
        // Numeral POS exemption in ConjugatedIdentityPenalty preserves the SurfaceMatchScore
        yield return ["二つ", "二つ", 1461160, (byte)0];
        yield return ["６鼠の小説には優れた点が二つある。", "二つ", 1461160, (byte)0];
        yield return ["３つ", "３つ", 1299740, (byte)1];
        yield return ["２つ", "２つ", 1461160, (byte)1];
        yield return ["小太郎は七つであった。", "七つ", 1319220, (byte)0];
        yield return ["規定は主に四つあります", "四つ", 1307040, (byte)0];

        // 罪 after expression token → noun つみ (1296680), not suffix ザイ (2836325)
        // Sudachi merges それが into a single expression token, causing 罪 to be misclassified as suffix;
        // ReclassifyOrphanedSuffixes fixes this by detecting the non-noun predecessor
        yield return ["それが罪だと？", "罪", 1296680, (byte)0];

        // やろう as volitional of やる (1012980) → Sudachi POS=動詞, DictForm=やる
        // Identity match penalty prevents expression やろう (2083340, "seems") from outscoring the verb
        yield return ["やろうとしていることを知ってしまったら", "やろう", 1012980, (byte)2];

        // やろう as noun 野郎/guy (1537700) → Sudachi POS=名詞, DictForm=やろう (no penalty: dictForm==surface)
        yield return ["あのやろうが来た", "やろう", 1537700, (byte)1];

        // やろ (Kansai dialect, volitional of copula や) → expression やろう/やろ (2083340)
        // ExpressionConflictPenalty is softened when DictForm "や" is a prefix of surface "やろ",
        // preventing noun 夜露 (1537230, "evening dew") from winning via its coincidental やろ kana reading
        yield return ["いやもっと楽しそうにしとったやろ", "やろ", 2083340, (byte)1];

        // 長 as suffix (チョウ) should resolve to ちょう "chief/head" (1429740), not なが "long" (2647210)
        yield return ["騎士団長", "長", 1429740, (byte)0];

        // まる as prefix should resolve to 丸 "whole/complete" (1216250), not archaic verb (2706850)
        yield return ["まる二日のこと", "まる", 1216250, (byte)2];

        // 若干 should resolve to じゃっかん (1324330), not archaic そこばく (2867570)
        yield return ["若干", "若干", 1324330, (byte)0];

        // 行けよ imperative should resolve to 行く (1578850), not potential 行ける (1631370)
        yield return ["さっさと病院行けよ", "行けよ", 1578850, (byte)0];

        // 仏 standalone should resolve to ほとけ/Buddha (1501760), not フツ/France (1501740)
        yield return ["俺だって仏じゃねぇからなあ！", "仏", 1501760, (byte)0];
        yield return ["神も仏も無いこの世界で", "仏", 1501760, (byte)0];

        // 公 standalone → おおやけ/public (1273170), not こう/duke (1578630)
        // User dic overrides Sudachi reading コウ→オオヤケ for standalone 公
        yield return ["公の利益のために戦う", "公", 1273170, (byte)0];
        yield return ["公と私の区別がつかない", "公", 1273170, (byte)0];
        yield return ["彼は公だ", "公", 1273170, (byte)0];

        // 公にする compound expression (2158500) should still resolve correctly
        yield return ["彼は公にした", "公にした", 2158500, (byte)0];

        // 私 standalone → わたし/I (1311110), not し/private affairs (2728300)
        // FixReadingAmbiguity overrides Sudachi シ→ワタシ + reclassifies as Pronoun
        yield return ["この本には修行とあります私修行します", "私", 1311110, (byte)0];

        // ことにする → 事にする/to decide to (2215340), not 異にする/to differ (1640290)
        // FindValidCompoundWordId picks higher-priority expression when multiple expressions match
        yield return ["ことにしないか", "ことにしない", 2215340, (byte)1];

        // 立ち (godan masu-stem) → 立つ verb (1597040), not 立ち noun/departure (1551240)
        yield return ["宗介のそばに立ち、マオがつぶやいた。", "立ち", 1597040, (byte)0];

        // たち after pronoun → 達/pluralising suffix (1416220), not 立ち/departure (1551240)
        // ReclassifyOrphanedSuffixes preserves Suffix POS after Pronoun; POS compatibility matches suf
        yield return ["彼女たちは家を預かるプロフェッショナル", "たち", 1416220, (byte)1];

        // たち after suffix 者 → 達/pluralising suffix (1416220), not 断つ verb (1597030)
        // ReclassifyOrphanedSuffixes preserves Suffix POS after another Suffix; POS compatibility matches suf
        yield return ["覚醒者たちの頂点を極める存在となり", "たち", 1416220, (byte)1];

        // うわッ → うわっ interjection (2061250, reading index 2), not split into う + わッ
        yield return ["うわッ人呼んでる", "うわッ", 2061250, (byte)2];

        // いや as interjection → 嫌/いや (1587610, ichi1), not 弥 archaic adverb (2580180)
        // Sudachi classifies いや as 副詞; Adverb→Interjection POS compatibility allows correct match
        yield return ["悪気はなかったのいや助かった", "いや", 1587610, (byte)2];

        // 次 standalone before verb → つぎ/next (1316380), not じ/prefix (1579580)
        // FixReadingAmbiguity overrides Sudachi ジ→ツギ + reclassifies as CommonNoun
        yield return ["大丈夫才川次頑張ろう！", "次", 1316380, (byte)0];

        // 忘れてる → 忘れる/to forget (1519210), not archaic 忘る (1519190)
        // Reduced stem-fallback ReadingMatchScore (25 vs 50) prevents false stem match from outscoring ichi1 entry
        yield return ["でも何か忘れてるような気がしますけど", "忘れてる", 1519210, (byte)0];
        yield return ["みんな言うのを忘れてたけど", "忘れてた", 1519210, (byte)0];

        // 見とく → 見る (1259290) with ておく contraction, not 篤と (とくと) adverb
        // ProcessSpecialCases splits Sudachi's misparse of とくと back to とく + と
        yield return ["よく見とくといいよ", "見とく", 1259290, (byte)0];
        yield return ["食べとくといいよ", "食べとく", 1358280, (byte)0];

        // 隠れていれば → 隠れる (1170660), not archaic 隠る (1985190)
        yield return ["さすがにこれだけ隠れていれば向こうから見えはしないと思うけど", "隠れていれば", 1170660, (byte)0];

        // ふう → 風 "style/manner" (1499730), not 封 "seal" (1499580)
        yield return ["あんまりそういうふうには見えないけどなー", "ふう", 1499730, (byte)1];

        // がち (hiragana) → colloquial ガチ/serious (2653620), not 雅致/artistry (1197950)
        // User dic overrides Sudachi 名詞→形状詞 so adj-na POS filter matches the colloquial entry
        yield return ["のがちこで分かる", "がち", 2653620, (byte)0];

        // ガチ (katakana) → colloquial ガチ/serious (2653620)
        yield return ["ガチの無人島サバイバル", "ガチ", 2653620, (byte)1];
        yield return ["ガチ返しをしてきた", "ガチ", 2653620, (byte)1];
        yield return ["ガチ目の前でやられてる", "ガチ", 2653620, (byte)1];
        yield return ["ガチ惚れしそうで怖い", "ガチ", 2653620, (byte)1];

        // がちがち → onomatopoeia "stiff/rigid" (1003110), not split into がち+が+ちち
        yield return ["がちがちに緊張する", "がちがち", 1003110, (byte)1];

        // Vowel elongation ー after る-verbs — lattice-level lookup finds verb without trailing ー,
        // surface preserved with ー
        yield return ["手伝って来るー", "来るー", 1547720, (byte)0];
        yield return ["ダンジョンに潜るー", "潜るー", 1609715, (byte)0];
        yield return ["ここにおるー", "おるー", 1577985, (byte)1];
        yield return ["きっと写るーっ", "写る", 1321820, (byte)0];

        // 玩具 → おもちゃ/toy (1217070), not がんぐ (2863107)
        // User dic overrides Sudachi reading ガング→オモチャ for the common modern reading
        yield return ["俺は神連に玩具扱いされておりその一環でみぎりと知り合った。", "玩具", 1217070, (byte)0];

        // なんじ → archaic pronoun 汝/you (2015140), not verb 難じる (2174390)
        // User dic overrides Sudachi 動詞→代名詞 for the common archaic pronoun reading
        yield return ["なんじもし私の信者ならば", "なんじ", 2015140, (byte)2];

        // 気づかれずに → 気づく (1591330), not 気づかれ/気疲れ (1222530)
        yield return ["麦をかき分ける音に気づかれずに接近できるからだ。", "気づかれずに", 1591330, (byte)0];

        // 気附かぬ → 気付く (1591330) — user dic adds 気附く as verb, ぬ reclassified to auxiliary
        yield return ["気附かぬ如くゆっくり", "気附かぬ", 1591330, (byte)5];

        // 如く stays separate (not merged by CombineAuxiliary) → 如く (1466920)
        yield return ["気附かぬ如くゆっくり", "如く", 1466920, (byte)0];

        // ごとく (kana) → 如く aux-v (1466920, r1), not 五徳 noun (1268560)
        // noun-no-noun-synergy was incorrectly boosting the noun candidate after の
        yield return ["便器のごとく真っ白な歯", "ごとく", 1466920, (byte)1];
        yield return ["間男のごとく割り込んで", "ごとく", 1466920, (byte)1];

        // Noun/verb stem disambiguation: when Sudachi tags an ichidan verb 連用形 as a noun,
        // the parser should prefer the verb if it has strictly higher priority than the noun.
        // 抱え: noun "armful" (1516300, nf23) vs verb 抱える (1516310, ichi1/nf08) → verb wins
        yield return ["お姫様だっこのように抱え", "抱え", 1516310, (byte)0];

        // Regression guards: common nouns that share surface with verb stems must stay as nouns.
        // 考え: noun "thought" (1281000, ichi1/nf01) vs verb 考える (1281020, ichi1/nf10) → noun wins
        yield return ["良い考えだ", "考え", 1281000, (byte)0];
        yield return ["彼の考えに賛成する", "考え", 1281000, (byte)0];

        // 答え: noun "answer" (1449530, ichi1/nf04) vs verb 答える (1449540, news2/nf34) → noun wins
        yield return ["その答えは正しい", "答え", 1449530, (byte)0];

        // お答えする: humble form — 答え (verb stem of 答える) + する, should resolve to 答える not いらえ (2852531)
        yield return ["お答えする", "答え", 1449540, (byte)0];

        // 教え: noun "teaching" (1236890, ichi1/nf12) vs verb 教える (1236900, ichi1/nf38) → noun wins
        yield return ["先生の教えに従う", "教え", 1236890, (byte)0];

        // 訴え: noun "lawsuit" (1397710, ichi1/nf05) vs verb 訴える (1397720, ichi1/nf39) → noun wins
        yield return ["訴えを起こす", "訴え", 1397710, (byte)0];

        // Lower-priority nouns: verb should win when noun entry has low/no priority.
        // 構え: noun (1279690, no priority) vs verb 構える (1279700, ichi1) → verb wins
        yield return ["銃を構え撃った", "構え", 1279700, (byte)0];

        // 備え: noun (1485630, nf15) vs verb 備える (1244960, ichi1/ichi2/nf09) → verb wins
        yield return ["武器を備え出発した", "備え", 1244960, (byte)0];

        // 支え: noun (1310080, no priority) vs verb 支える (1310090, ichi1) → verb wins
        yield return ["体を支え立ち上がった", "支え", 1310090, (byte)0];

        // 蓄え: noun (1854350, nf30) vs verb 蓄える (1596860, ichi1/nf36) → verb wins
        yield return ["力を蓄え待った", "蓄え", 1596860, (byte)0];

        // 頭 as counter とう (1450690, ctr ichi1) after numeral, not がしら suffix (2252670, suf)
        // Sudachi tags as 接尾辞,助数詞 — FilterMisparse reclassifies Suffix+Counter to Counter POS
        yield return ["二頭の猟犬", "頭", 1450690, (byte)0];

        // わからずじまい is JMDict 2870678 (exp, "ending up not understanding") — kept as compound
        yield return ["相変わらず瑛の母親はわからずじまいで…。", "わからずじまいで", 2870678, (byte)5];

        // チック should match the colloquial suffix (-esque/-like/-ish), not pomade stick
        yield return ["小悪魔チックに微笑む彼女。", "チック", 2846862, (byte)0];
        yield return ["乙女チックー", "チックー", 2846862, (byte)0];

        // 飛んで → 飛ぶ te-form (1429700), not obscure expression 飛んで (2248530, "zero/flying")
        // Conjugated-form identity penalty + reading match zeroing prevents the expression from outscoring the verb
        yield return ["早くどうかしないと飛んでもねえ事になるぜ", "飛んで", 1429700, (byte)0];
        yield return ["無事飛んで戻ってきたら", "飛んで", 1429700, (byte)0];

        // まぁいい → adj-ix いい (2820690), not taru-adj 易々 (2672320) or Iran-Iraq abbr (1923080)
        // Sudachi misidentifies いい as verb いう (dictForm=いう); adj-ix exemption prevents the
        // conjugated-identity penalty from firing on the correct word
        yield return ["まぁいい、タイムトラベルの真相には一歩近づいたんだ。", "いい", 2820690, (byte)0];
        yield return ["まぁいいか", "いい", 2820690, (byte)0];
        yield return ["いい", "いい", 2820690, (byte)0];

        // くさい should resolve to 臭い (1333150, adj-i), not 救済 (2673180, noun) or くさる (1497800, verb)
        yield return ["くさい", "くさい", 1333150, (byte)1];
        yield return ["とてもくさい", "くさい", 1333150, (byte)1];
        yield return ["ここがくさい", "くさい", 1333150, (byte)1];
        yield return ["すごいくさい", "くさい", 1333150, (byte)1];

        // colloquial せぇ → さい: 面倒くせぇ = 面倒くさい (1533560, adj-i)
        yield return ["面倒くせぇ", "面倒くさい", 1533560, (byte)0];

        // さ-nominalization of adj-i: 嬉しさ → 嬉しい (1219510, adj-i)
        yield return ["嬉しさで胸が熱くなる", "嬉しさ", 1219510, (byte)0];

        // 隙 in context → すき/opening (1253780), not ひま/obsolete (2861550)
        // FixReadingAmbiguity overrides Sudachi ヒマ→スキ for standalone 隙
        yield return ["いなくなった隙に奪い取る", "隙", 1253780, (byte)0];
        yield return ["意識が幼獣に向いている隙に攻撃を加えれば", "隙", 1253780, (byte)0];

        // 額 standalone → ひたい/forehead (1207510), not がく/amount (1207500)
        // FixReadingAmbiguity overrides Sudachi ガク→ヒタイ; がく primarily in compounds (金額, 総額)
        yield return ["渡は額に手をあてて、深いため息を吐く。", "額", 1207510, (byte)0];
        yield return ["額に汗が浮かぶ", "額", 1207510, (byte)0];
        yield return ["額を押さえる", "額", 1207510, (byte)0];
        yield return ["額にキスをした", "額", 1207510, (byte)0];

        // クスクス → onomatopoeia "chuckle/giggle" (2007850), not couscous (2002980) or cuscus animal (2853576)
        // on-mim POS bonus breaks the three-way tie in favour of the mimetic word
        yield return ["クスクスと笑う", "クスクス", 2007850, (byte)1];
        yield return ["わたしはクスクスと笑う。", "クスクス", 2007850, (byte)1];

        // ぬかるんで → 泥濘む/ぬかるむ "to become muddy" (2009310), not 抜かる "to make a mistake" (1478130)
        // Sudachi DictionaryForm lemma floor ensures deep deconjugation chains don't nullify Sudachi's identification
        yield return ["下がぬかるんでたから受け止めた拍子に足を滑らせただけだぞ", "ぬかるんでた", 2009310, (byte)1];

        // 割れ as ichidan verb 連用形 → 割れる "to break" (1208020), not noun 割れ "broken piece" (1208010)
        // Sudachi misclassifies as noun in sentence 1; GetPriorityScore fallback picks the verb (ichi1)
        yield return ["壁が大きくきしみ割れ無数の破片へと砕けて十三階の高さから落ちてゆく。", "割れ", 1208020, (byte)0];
        yield return ["爪が割れ剥がれそうになる…。", "割れ", 1208020, (byte)0];

        // やつら → 奴ら "they/those guys" (1913290), not つら "face" (1584690)
        yield return ["やつらの思うつぼだ", "やつら", 1913290, (byte)4];

        // ガラ (katakana) → 柄/character (1508300), not gala (2834398)
        // Katakana surface matches the katakana form (RI=2) rather than the hiragana canonical (RI=1)
        yield return ["ガラじゃないしさ", "ガラ", 1508300, (byte)2];
        yield return ["俺だってガラじゃないのは分かってるんだから", "ガラ", 1508300, (byte)2];

        // くすん → sniff/sniffle onomatopoeia (2130690), not くすむ/to be dull (1957380)
        // User dic overrides Sudachi 動詞(くすむ撥音便)→副詞 for the common onomatopoeia
        yield return ["くすんと鼻を鳴らし", "くすん", 2130690, (byte)0];

        // リス → 栗鼠/squirrel (1246890), not 離/fracture (1141430)
        // Sudachi NormalizedForm 栗鼠 matches squirrel's kanji form, overcoming fracture's gai1 priority
        yield return ["リスのように膨らんだ色白の頬に食べ滓がついている。", "リス", 1246890, (byte)2];
        yield return ["ところがぼくは小林君というリスのようにすばしっこい助手を持っていました。", "リス", 1246890, (byte)2];

        // つうか → conjunction "or rather" (2848301), not 通過 "passing through" (1433070)
        // User dic tokenizes as single token; Conjunction POS skips verb-fallback priority comparison
        yield return ["つうか何を迷走しているんだ。", "つうか", 2848301, (byte)5];

        // 聞ける → potential of 聞く (1591110), not archaic ichidan 聞ける "to tell" (2517260)
        // Archaic POS penalty ensures the common potential form wins over the exact surface match
        yield return ["聞ける", "聞ける", 1591110, (byte)0];
        yield return ["だから彼女が聞けたのはそこから続くやりとりだ。", "聞けた", 1591110, (byte)0];
        yield return ["娘の私ひとりの話を聞けない人が国民の声を聞けると思えないけど", "聞けない", 1591110, (byte)0];
        yield return ["いい結果が聞けるといいわね", "聞ける", 1591110, (byte)0];

        // 弾ける → potential of 弾く/ひく (1419370, "to play"), not 弾ける/はじける (1419380, "to burst")
        // Reading prefix mismatch: Sudachi reading ヒケ- ≠ はじけ-, so surface discount kicks in
        yield return ["何なら弾けるの", "弾ける", 1419370, (byte)0];
        yield return ["ギターなんて弾けないよ", "弾けない", 1419370, (byte)0];

        // 出来 as noun (でき, workmanship) should not be overridden by suru-verb 出来 (しゅったい)
        yield return ["純粋に刀剣としての出来をくらべたら、立夏の剣の方がおそらく上だ。", "出来", 1340430, (byte)0];

        // 出来ません should be 出来る (dekiru) not 出来 (しゅったい)
        yield return ["これ丈けの簡単な動作でも、手早くやればなかなか観察出来ません。", "出来ません", 1340450, (byte)0];

        // 出来なさそう should be 出来る (dekiru, 1340450) — verb + negative-appearance suffix
        yield return ["そんな量出来なさそうだけど", "出来なさそう", 1340450, (byte)0];

        // 食べなさそう should be 食べる (taberu, 1358280)
        yield return ["あいつ野菜食べなさそうだよな", "食べなさそう", 1358280, (byte)0];

        // こうやって → conjunction "thus/in this way" (2123630), not 公約/public commitment (1274880)
        // SpecialCases2 merges Sudachi's こう+やって so the direct dictionary entry wins
        yield return ["こうやって、お互いに端っこと端っこを咥えて", "こうやって", 2123630, (byte)0];
        yield return ["こうやって、直接相談に乗るためか！", "こうやって", 2123630, (byte)0];

        // こと + って must not merge — こと stays as 事 (1313580), not verb stem of ことる (Kotor, place name)
        yield return ["自分のことって自分では分からない", "こと", 1313580, (byte)2];

        // であった → である copula (1008340, r0), not 出会う (1598530)
        // である has spec1 priority; 出会う has ichi1+news1 — copula boost needed
        yield return ["一と眼でわかるはずであった", "であった", 1008340, (byte)0];

        // なし → 無し (1529560, r1 kana), not 梨/pear (1549860)
        // archaicPosTypes penalty (-75) fires on 無し due to adj-ku POS, dropping it below 梨
        yield return ["丘間に一小湾をなし", "なし", 1529560, (byte)1];

        // たった in elapsed-time context → 経つ (1251100, r1 kana たつ), not 断つ (1597030)
        // 断つ/立つ have news1; 経つ has news2 — needs context disambiguation by preceding time noun
        yield return ["四、五年たった頃に", "たった", 1251100, (byte)1];

        // ぽーず → ポーズ/pause (1124650, r0), Sudachi treats ぽ as OOV → 3 tokens
        yield return ["ぽーず", "ぽーず", 1124650, (byte)0];

        // 悪しからず → adverb あしからず (1151300, r0 kanji form), not deconjugated 悪しい (2862515)
        // POS compat check filters 1151300 (adverb vs Auxiliary token) — bypass for exact surface matches
        yield return ["悪しからず", "悪しからず", 1151300, (byte)0];

        // こいてる → 放く (2019450, r1 こく), not name Koyter (5032002 unclass)
        // Unclass name wins via direct surface lookup in rescue path
        yield return ["超余裕ブチこいてるのは確かねあの女", "こいてる", 2019450, (byte)1];

        // にらんでいる → 睨む/to glare (1569880), not 似る/to resemble (1314600)
        // 似る has "jiten" priority (+100 WordScore) but is v1; the deconjugation chain ending in v5m
        // correctly gates it out. The erroneous path went teiru→adj-i te-form→slurred negative→a-stem,
        // exploiting the intermediate v1 con_tag to pass the POS validation for the v1 word 似る.
        // Fix: validate only the last deconjugation tag (v5m), not all accumulated intermediate tags.
        yield return ["にらんでいる", "にらんでいる", 1569880, (byte)1];

        // 似ている → 似る/to resemble (1314600, v1) — regression guard: v1 verbs must still match
        // via the correct teiru→stem-te-verbal→stem-ren-less→v1 chain after the POS fix
        yield return ["似ている", "似ている", 1314600, (byte)0];

        // 飲んでいる → 飲む/to drink (1169870, v5m) — v5m+teiru pattern same as にらむ
        yield return ["飲んでいる", "飲んでいる", 1169870, (byte)0];

        // 絡んでいる → 絡む/to get entangled (1548520, v5m) — v5m+teiru pattern
        yield return ["絡んでいる", "絡んでいる", 1548520, (byte)0];

        // このにんむは → 任務/duty (1467260, n), not 似る/to resemble (1314600, v1)
        // Sudachi splits にんむ as にん (verb, dict=にる) + む (aux), CombineAuxiliary merges them back,
        // but the inherited DictionaryForm=にる gave the verb a false +100 LemmaScorer boost.
        // Fix: user dict entry at cost 3000 makes Sudachi output にんむ as a single noun (dict=にんむ),
        // removing the verb boost; む also excluded from CombineAuxiliary as a safeguard.
        yield return ["このにんむは、里長とヒノエさんから、コミツにあたえられたんだよ。", "にんむ", 1467260, (byte)1];

        yield return ["そんな心地", "そんな", 1007130, (byte)0];
        yield return ["ごめんなさいね？", "ね", 2029080, (byte)0];
        yield return ["どうして、こんな状況になっているんだろう。", "こんな", 1004880, (byte)0];

        // 無き should match the adj-pn lexical entry (2138570), not deconjugate to 無い (1529520)
        yield return ["余す所無き実力", "無き", 2138570, (byte)0];

        // 事 after なすべき expression → こと nominalizer (1313580), not じ suffix (2187200)
        // Sudachi tags 事 as 接尾辞/ジ; ReclassifyOrphanedSuffixes reclassifies it, then
        // FixReadingAmbiguity overrides ジ→コト so ReadingMatchScore goes to the correct entry
        yield return ["どうやらこの男は俺と違って、自分のなすべき事を理解しているようだった。", "事", 1313580, (byte)0];

        // べき should be "should/must" aux (1011430), not "exponent/power" noun (1564290)
        yield return ["もう少し様子を見るべきですが、場合によっては病院に搬送するかどうかも考えましょう。", "べき", 1011430, (byte)1];
        yield return ["けど、なるべきだと思ったのよね。", "べき", 1011430, (byte)1];

        yield return ["一人でシコってくれた。", "シコってくれた", 2595020, (byte)0];
        yield return [" 「……あんたはさ、考え方が古くせーんだよ」", "古くさい", 1265530, (byte)1];

        // お肉 → 肉/meat (1463520), not northern groundcone おにく (2716820)
        // CombinePrefixes reading-based path matched おにく because にく is 肉's reading;
        // おにく exclusion prevents the spurious merge so 肉 resolves correctly
        yield return ["お、お肉は余計な脂肪が付きやすいのよ。", "肉", 1463520, (byte)0];

        // キリ → 切り/限り (1383800, n/ctr/prt) "end/limit", not 霧 (1531110, n) "fog/mist"
        // 1383800 has "prt" in JMDict POS → particle-particle-penalty and orphan-counter-penalty
        // were wrongly firing; both now require CandidateIsNotNounLike so noun-primary words are exempt
        yield return ["疑いだしたらキリはない", "キリ", 1383800, (byte)5];

        // なの → expression "that's the way it is" (2425930), not nano- prefix (1090530)
        // "fem" in JMDict POS [exp, fem, col] was incorrectly mapped to PartOfSpeech.Name, triggering
        // the -50 name penalty; fix: "fem"/"masc"/"male" are register markers, not name-type tags
        yield return ["あなたたち生き人形なの", "なの", 2425930, (byte)0];
        yield return ["何をご所望なのかな", "なの", 2425930, (byte)0];
        yield return ["あんたはその程度の執事なのかい", "なの", 2425930, (byte)0];

        // なのに → conjunction "and yet; despite this" (2395490)
        // SpecialCases2 merges Sudachi's なの+に into なのに before form scoring
        yield return ["はずなのに忘れてた", "なのに", 2395490, (byte)0];

        // なので → conjunction/particle "because; since" (2827864)
        // SpecialCases2 merges Sudachi's なの+で into なので before form scoring
        yield return ["降りてもカードを晒すルールなので互いの手札が晒される", "なので", 2827864, (byte)0];

        // ではなく (adverbial form of ではない) → ではない (2823770)
        // CombineParticles merges で+は+なく into ではなく expression
        yield return ["気配といっても曖昧な何かではなく、単に落ち葉がかすれる微かな音が聞こえただけだ。", "ではなく", 2823770, (byte)1];

        // のではないか → ではないか expression (2027020), not ないか as 内科 noun
        yield return ["再生しているのではないかという兆候もある。", "ではないか", 2027020, (byte)1];
        yield return ["会長になるのではないかという呼び声も高いのだ。", "ではないか", 2027020, (byte)1];

        // standalone ないか after auxiliary → expression "won't/isn't" (2210280), not 内科 noun (1457830)
        yield return ["無駄じゃ、ないかお前も生きたくて必死なんだな。", "ないか", 2210280, (byte)0];

        // チックショー (colloquial geminated form of ちくしょう) → 畜生 (1422200)
        // Sudachi splits into チック(suffix)+ショー(noun); user_dic + NormalizedForm lookup fix recombines
        yield return ["チックショー", "チックショー", 1422200, (byte)1];

        // さん after a name should resolve to the honorific suffix (1005340), not the numeral 三 (1579350)
        // ReclassifyOrphanedSuffixes now preserves Suffix POS for さん, and Suffix tokens skip the
        // verb-fallback comparison that was selecting the high-frequency numeral
        yield return ["やっと会えました。待ってましたよ、桐島玲さん", "さん", 1005340, (byte)0];

        // 様 disambiguation: さま (honorific suffix, 1545790) vs よう (appearance/manner, 1605840)
        // After removing jiten priority from 1605840, context must drive the choice.
        // Honorific さま: after a katakana name or polite pronoun
        yield return ["私は今日からケイト様にお仕えする生き人形です", "様", 1545790, (byte)0];
        yield return ["田中様にご連絡ください", "様", 1545790, (byte)0];
        yield return ["どなた様でございましょうか", "様", 1545790, (byte)0];
        // よう: appearance/manner after demonstratives or nouns (non-honorific context)
        yield return ["この様に話す", "様", 1605840, (byte)0];
        yield return ["そんな様では困る", "様", 1605840, (byte)0];
        yield return ["生き物の様に動く", "様", 1605840, (byte)0];

        // Kansai-ben せん (negative of する): suru-noun + せん combines to the suru-verb entry
        yield return ["卑下せんでもいい", "卑下せん", 1482700, (byte)0];
        // Standalone prefix-tagged せん → 2844926 (do not / will not do)
        yield return ["遠出はせんほうがいいぞ", "せん", 2844926, (byte)0];
        // いかんせん = 如何せん (1919420), not いかん + せん
        yield return ["いかんせん、距離が離れすぎている", "いかんせん", 1919420, (byte)1];
        yield return ["いかんせん頑張ってるだけで勝てるほど甘くはない", "いかんせん", 1919420, (byte)1];
        // いいセンいってる → compound expression "to be on the right track" (2394060)
        yield return ["いいセンいってるとかいったじゃないか", "いいセンいってる", 2394060, (byte)4];
        // ノリ in katakana → 乗り (1354720, riding/enthusiasm), not 海苔 (1201620, seaweed)
        yield return ["ノリを合わせて一度話し始めると", "ノリ", 1354720, (byte)2];
        yield return ["ノリだけで応募するわけじゃありません", "ノリ", 1354720, (byte)2];
        // リッキー = Rickey/Ricky (5091164), not 六気 (2656100)
        yield return ["さすがだなリッキー", "リッキー", 5091164, (byte)0];

        // Katakana words with extra ー in the middle should match dictionary forms without it
        // アボカード → アボカド/avocado (1018410)
        yield return ["アボカードを食べる", "アボカード", 1018410, (byte)0];
        // アモローソ → アモロソ/amoroso (2434210)
        yield return ["アモローソ", "アモローソ", 2434210, (byte)0];

        // 許し should resolve to verb 許す (1232870, v5s "to forgive"), not particle ばかし (2256550, prt)
        // Kanji-surface particle exemption: particles in kanji get the conjugated-identity penalty
        yield return ["私をお許しくださいぃ！", "許し", 1232870, (byte)0];
        yield return ["あの人ならばきっとそれを許しはしないだろうから。", "許し", 1232870, (byte)0];
        yield return ["誤解しそうになった私を、お許しください", "許し", 1232870, (byte)0];

        // 背負う (1472860, せおう "carry on back") should beat 背負ってる (2831946, しょってる "conceited")
        yield return ["背負っていた籠を下ろす", "背負っていた", 1472860, (byte)0];
        yield return ["背負ってるものを完全に理解することはできない", "背負ってる", 1472860, (byte)0];
        yield return ["頭の上にある空気の重さを全部背負ってるんだ", "背負ってる", 1472860, (byte)0];
        // しょってる in kana → the expression 背負ってる (2831946, "conceited")
        yield return ["あいつしょってるよな", "しょってる", 2831946, (byte)1];

        // 刃向かい should be continuative form of 刃向かう (1601070, v5u "to strike back")
        yield return ["刃向かいやがって", "刃向かい", 1601070, (byte)2];

        // ねえ → particle/interjection "hey" (2029080), not 姉/older sister suffix (2266990)
        // Sudachi misclassifies ねえ as 名詞 with NormalizedForm=姉 in sentence context;
        // n-suf NormalizedForm bonus is reduced so the spec1 particle wins
        yield return ["ねえ君、こいつも連れてってやろうよ。", "ねえ", 2029080, (byte)2];
        yield return ["ねえ三人とも、目線こっちにちょうだい？", "ねえ", 2029080, (byte)2];
        yield return ["ねえ陽南ちゃん、あれ全部カットできなかったの？", "ねえ", 2029080, (byte)2];
        yield return ["部屋を出て行こうとした加奈が、ふと足を止め「ねえ」", "ねえ", 2029080, (byte)2];

        // ないわ should NOT merge into ナイワ (Najwa, name 5057701).
        // わ is a sentence-ending particle and should stay separate from ない (1529520, adj-i).
        yield return ["あなたがここの団長じゃあないわよね", "ない", 1529520, (byte)1];
        // ことはない is JMDict 2585230 (exp, "there's no way that") — kept as compound
        yield return ["例のプラスチック爆薬が主食になる、なんてことはないわよね", "ことはない", 2585230, (byte)3];
        yield return ["この子は悩みなんてないわよね", "ない", 1529520, (byte)1];

        // よう after verb/aux should resolve to 様 (1605840, "seeming/manner"), not
        // interjection よう (2853599) or 陽 (1605845, "positive/yang").
        yield return ["彼女は彼女で、かなり今回のことについて、いろいろと考えているようだった。", "よう", 1605840, (byte)1];
        yield return ["直哉の動揺には気付いていないようだった。", "よう", 1605840, (byte)1];
        yield return ["捜しようがないし", "よう", 1605840, (byte)1];
        yield return ["文句を垂れているようで、その場を歩きながらめちゃくちゃご機嫌そうな信田。", "よう", 1605840, (byte)1];
        yield return ["側に浮いていた黒いキューブが変形していたようなので、おそらく装備品でしょう」", "よう", 1605840, (byte)1];
        yield return ["なかったようで安心した", "よう", 1605840, (byte)1];
        yield return ["嬉しいようで照れていた", "よう", 1605840, (byte)1];
        yield return ["あの男もそのことは分かっていたようだが、それを解決したがっていた。", "よう", 1605840, (byte)1];
        yield return ["何かに感づいたようだが、オレには全く分からない。", "よう", 1605840, (byte)1];
        yield return ["朝凪さんは何もしていないのに、まるで劣っているような言い方。", "よう", 1605840, (byte)1];
        yield return ["この一二年で、すっかり腑抜けちまったようだな。", "よう", 1605840, (byte)1];

        // よし as interjection "alright" (2607690), not adverb 縦し "even if" (2607700)
        yield return ["よし分かった", "よし", 2607690, (byte)0];
        yield return ["よし起こすとするか", "よし", 2607690, (byte)0];

        // 米 standalone → こめ/rice (1508750), not べい/America (2150610)
        // User dic overrides Sudachi reading ベイ→コメ for the common standalone reading
        yield return ["米とか醤油とかは、馴染みの店が配達してくれるけどさ。", "米", 1508750, (byte)0];

        // 里 as さと/village (1550760) — Sudachi reading サト disambiguates vs り/unit of distance (1550770)
        yield return ["鬼が出て『鬼の里』に連れて行かれるからな", "里", 1550760, (byte)0];
        yield return ["虹の雨で獣人種の里が滅びた時に", "里", 1550760, (byte)0];
        // 里 as り/unit of distance (1550770) — Sudachi reading リ after numeral
        yield return ["十里も離れた場所", "里", 1550770, (byte)0];

        // Internal ー stripped: じゃなーい → じゃない expression (2755350)
        yield return ["じゃなーい", "じゃない", 2755350, (byte)2];
        // Internal ー stripped: 作ってなーい → 作ってない → 作る (1597890)
        yield return ["作ってなーい", "作ってない", 1597890, (byte)0];
        // おーい preserved as-is — real JMDict interjection (2853873)
        yield return ["おーい", "おーい", 2853873, (byte)1];

        // ドキッと / ドキっと — being startled (1009040), not 土器 (1445310)
        yield return ["俺は内心ドキッとしたが", "ドキッと", 1009040, (byte)0];
        yield return ["ちょっとドキっとしたからって", "ドキっと", 1009040, (byte)1];

        // いちゃ (contracted いては) should resolve to いる (1577980), not 射手 (1579670)
        yield return ["いちゃ駄目", "いちゃ", 1577980, (byte)1];
        yield return ["ここにいちゃまずいんじゃないか", "いちゃ", 1577980, (byte)1];

        // いない (negative of いる) should resolve to 居る (1577980), not 以内 (1155180)
        yield return ["誰もいない場所でひたすら撃ちまくって", "いない", 1577980, (byte)1];
        yield return ["どうして……貴方が……いないの？", "いない", 1577980, (byte)1];

        // 気がついて expression should resolve to 気がつく (1591050), not be split into 気+がつ+いて
        yield return ["気がついてしまう", "気がついて", 1591050, (byte)0];
        yield return ["まだ気がついていまい", "気がついて", 1591050, (byte)0];

        // Verb 連用形 + 合い compounds: nominalized form should resolve to the compound verb
        yield return ["殺し合いを始めようとしている", "殺し合い", 2640960, (byte)0];
        yield return ["胸ぐらの掴み合いが始まりそう", "掴み合い", 1847870, (byte)1];
        yield return ["じゃれ合いのような会話", "じゃれ合い", 2825730, (byte)0];
        yield return ["引っ張り合いをして", "引っ張り合い", 2761310, (byte)0];

        // ギリギリ disambiguation: "barely" (1003660) vs "grinding sound" (2832861)
        // Before copula/verb → adverb "barely" sense
        yield return ["ただでさえ去年ギリギリだったんだぞ", "ギリギリ", 1003660, (byte)2];
        yield return ["ギリギリ保っていた形勢が大きく先手に傾いていく", "ギリギリ", 1003660, (byte)2];
        yield return ["限界ギリギリの譲歩なのだろう", "ギリギリ", 1003660, (byte)2];
        // Before という → onomatopoeia "grinding" sense (adv-to synergy)
        yield return ["ギリギリという歯噛みの音が聞こえそうなくらい顔をゆがめ", "ギリギリ", 2832861, (byte)1];

        // ないない → emphatic "no way, no chance" (2835362, uk adj-i), not 内内 "private" (1582450, adv)
        // Sudachi classifies as 形容詞; POS-compatible tiebreaker prefers adj-i over POS-incompatible adv
        yield return ["いまどき大学卒業したくらいじゃ就職ないない", "ないない", 2835362, (byte)1];
        yield return ["ないない、なんにもないわよ！", "ないない", 2835362, (byte)1];
        yield return ["あたしらに恩を感じることなんてないない", "ないない", 2835362, (byte)1];

        // どの should resolve to adj-pn "which" (1920240), not suffix 殿 "Mr./Mrs." (1442500)
        yield return ["どの本を買えばいいのやら", "どの", 1920240, (byte)1];

        // 滲み出す should resolve to にじみだす (2859607), not しみだす (2158570)
        // Sudachi reading ニジミダス disambiguates; compound kept intact so form scoring uses reading
        yield return ["滲み出す", "滲み出す", 2859607, (byte)0];

        // 拳 standalone → こぶし/fist (1257740), not けん/hand game (2255310)
        // User dic overrides Sudachi 接尾辞→名詞 so the common noun reading wins
        yield return ["色が変わるほどに力いっぱい拳を握り締め語り続ける水波に、思わず数歩後ずさる。", "拳", 1257740, (byte)0];
        yield return ["それから揃えた膝の上で、きゅっと拳を形作る。", "拳", 1257740, (byte)0];
        yield return ["少年は思わず拳を握りしめた。", "拳", 1257740, (byte)0];
        yield return ["元気よく拳を突き上げ、空に向かって叫ぶ。", "拳", 1257740, (byte)0];

        // うえ → 上 "above/on top of" (1352130), not 飢える "to starve" (1224080)
        // Ichidan stem fallback should not override high-confidence noun match
        yield return ["知ったうえで行動していた", "うえ", 1352130, (byte)1];
        yield return ["理解したうえで同意したのだ", "うえ", 1352130, (byte)1];
        yield return ["何ボーッとしたうえフラフラしてんのよ", "うえ", 1352130, (byte)1];

        // そうそう → interjection "oh yes!/that's right" (1006640), not 匆匆 "busy/hurried" (2435210)
        // adverb-before-noun-penalty was tipping the balance during rederivation;
        // interjection-adverb-noun-exempt rule offsets the penalty for recognized interjections
        yield return ["そうそう、俺たちがその本屋を知ったのも冒険者ギルドからの依頼。", "そうそう", 1006640, (byte)1];
        yield return ["なんて言えばいいのかなそうそう、役の気持ちを考えるの", "そうそう", 1006640, (byte)1];

        // Verb-like suffix かねる — must resolve to かねる/かねない, not 鐘 (bell, 1352030)
        yield return ["壊してしまいかねない", "かねない", 1922120, (byte)1];
        yield return ["決めかねている", "かねている", 1256520, (byte)1];

        // してん (colloquial してる→してん) should resolve to する (1157170), not noun 支店 (1310230)
        // ConjugatedIdentityPenalty -300 for plain nouns fully cancels the coincidental surface-match
        yield return ["何してんの", "してん", 1157170, (byte)1];
        yield return ["あんた何してんのよ", "してん", 1157170, (byte)1];
        yield return ["なに抜け駆けしてんのよっ", "してん", 1157170, (byte)1];

        // や (Kansai copula) should resolve to particle/copula (2028960), not 矢 arrow (1537760)
        yield return ["ウソやで", "や", 2028960, (byte)0];

        // Orphaned suffix 店 (テン) should resolve to standalone noun みせ (1582120), not suffix てん (1582125)
        yield return ["昔こういう店でバイトしようとしたことがあって", "店", 1582120, (byte)0];

        // うかつすぎる → 迂闊/careless (1171890), not split into う+かつ+すぎる
        // User dic adds うかつ as 形状詞 so Sudachi recognizes it as a single token
        yield return ["うかつすぎる", "うかつすぎる", 1171890, (byte)2];

        // いってん (colloquial 言ってる→言ってん) should resolve to 言う (1587040), not いる (1577980) or 一転 (1165070)
        // Sudachi correctly identifies DictForm=いう; DictForm conflict penalty suppresses いる and 一転
        yield return ["どんな姿で買いにいってんの？", "いってん", 1587040, (byte)3];
        yield return ["今更ナニいってんの！", "いってん", 1587040, (byte)3];

        // 弄る should be いじる (1560700), not いらう (2849632) — いらう is archaic
        yield return ["シリルさんが弄ったというのは本当のようだ", "弄った", 1560700, (byte)0];
        yield return ["そこを無数の腕が乱暴に弄っていた", "弄っていた", 1560700, (byte)0];

        // たわけ split → わけ should match 訳 (not 戯け)
        yield return ["術があるったわけではない", "わけではない", 2057560, (byte)3];
        yield return ["どうしてたわけ", "わけ", 1538330, (byte)1];
        // Legitimate たわけ (戯け) should match 戯け
        yield return ["この大たわけがっ", "たわけ", 2644710, (byte)2];

        // === ニッと should be にっと (with a grin, 2747260), not 日 name (5579910) ===
        yield return ["ニッと笑った", "ニッと", 2747260, (byte)0];

        // === よる in にもよるが should be 依る (1168660), not 夜 (1536350) ===
        yield return ["懐具合にもよるが", "よる", 1168660, (byte)4];
        yield return ["規模にもよるが、", "よる", 1168660, (byte)4];
        yield return ["迷惑の定義にもよるわね。", "よる", 1168660, (byte)4];

        // === 捩る in body movement context should be 捩る/nejiru (1611090), not もじる (2793790) ===
        yield return ["二人が胸元を押さえながらショックを受けたように身体を捩る。", "捩る", 1611090, (byte)0];
        yield return ["腰を捩って", "捩って", 1611090, (byte)0];

        // === 立て in を立て should be 立てる (1551530), not prefix たて (2081610) ===
        yield return ["結合部が淫らな水音を立て、部屋に響く。", "立て", 1551530, (byte)0];
        yield return ["俺だけに見えるように親指を立て、屋敷を出ていった。", "立て", 1551530, (byte)0];

        // === 来る should be くる (1547720), not きたる (1591270) in modern contexts ===
        yield return ["来るわけねえだろ", "来る", 1547720, (byte)0];
        yield return ["ヘンな眼鏡がこっち来たー！", "来たー", 1547720, (byte)0];
        yield return ["別の場所で泣いてから来るべきだ。", "来る", 1547720, (byte)0];
        // 来てない = te-form negative of 来る, not the expression 来てる (2830009)
        yield return ["まだ来てないか", "来てない", 1547720, (byte)0];

        // === 来る in archaic context should stay きたる (1591270) ===
        yield return ["冬来りなば春遠からじ。", "来りなば", 1591270, (byte)0];

        // === 一列 should be いちれつ (1167430), not the name かずなみ (5126363) ===
        yield return ["横一列に並んだブリューナクのメンバーたちは", "一列", 1167430, (byte)0];

        // 何となく merged from 何と+なく (SpecialCases2)
        yield return ["その様子を眺めながら、今の話について何となく考えを巡らせる。", "何となく", 1599730, (byte)0];
        // 中途半端 merged from 中途+半端 (SpecialCases2)
        yield return ["こんな…任務も中途半端なまんま全滅なんて…", "中途半端", 1425050, (byte)0];
        // 常に merged from 常+に (SpecialCases2)
        yield return ["人海戦術は常に行っているもの足りないのは量じゃなくて質のほう", "常に", 1355970, (byte)0];

        // === Currently failing — tracked in MISPARSES_TO_FIX.txt ===
        // 各 in 各色 context should read かく (1204860), not おのおの (2826190)
        yield return ["号令に合わせて各色の閃光が奔り、次の瞬間には周囲一帯を劈く轟音が響き渡っていた。", "各", 1204860, (byte)0];
        // 大仰 should be おおぎょう (1413470, adj-na), not the place name Oonoki (5490075)
        yield return ["誠実どころか大仰すぎんぞ", "大仰", 1413470, (byte)0];

        // === Misparses batch 2026-04-14 (tracked in MISPARSES_TO_FIX.txt) ===
        // いけない as "must not" (1000730, exp/adj-i) — currently matches いける (1587190) instead
        yield return ["ここから出てはいけない", "いけない", 1000730, (byte)1];
        yield return ["他にしなきゃいけないことは", "いけない", 1000730, (byte)1];
        // タンゴ as tango dance (2019220) — currently matches 単語 (1417330) due to near-tie scoring
        yield return ["タンゴを踊る", "タンゴ", 2019220, (byte)0];
        // トム as the given name Tom (5055293) — currently picks 1496740 (wrong word)
        yield return ["トム・ソーヤーは読んだことあるって言ってたね", "トム", 5055293, (byte)0];
        // 汝 should resolve to 2015140 (汝/爾/なんじ, pn/arch/poet), not 1631650 (汝/己/うぬ, vulg)
        yield return ["古の誓約により、我が銃身は汝のものとなり", "汝", 2015140, (byte)0];

        // カッコウ (katakana stylisation of 格好 "appearance") should resolve to 格好 (1590480),
        // not to 郭公 cuckoo (1206270). User_dic sets NormalizedForm=格好.
        yield return ["どうしてそんなカッコウを？", "カッコウ", 1590480, (byte)2];

        // 泳ごー (volitional with elongated ー → う) should resolve to 泳ぐ (1174340) volitional,
        // not be split into 泳 (Name) + ご (Numeral) + ー. Pattern added in RepairVowelElongation.
        yield return ["手治ったら一緒に泳ごーね。", "泳ごう", 1174340, (byte)0];

        // そう + 言って (kanji 言) must NOT merge into そう言って/沿う (1176700 "to run along").
        // ProcessSpecialCases restricts the そう+いう merge to kana 言 (そういう/そういって).
        // Here そう is adverb (2137720) + 言って is 言う verb (1587040).
        yield return ["そう言ってありがたいです", "そう", 2137720, (byte)1];
        yield return ["そう言ってありがたいです", "言って", 1587040, (byte)0];
        // Regression: kana-form そういう as "such" still merges into WID 1394680.
        yield return ["そういう事だ", "そういう", 1394680, (byte)2];

        // === Group A: Sudachi mis-tagging ===
        // #2: 太鼓持ち noun (1585720), not 太鼓持 + ちかい (近い)
        yield return ["生徒会長の太鼓持ちかい？", "太鼓持ち", 1585720, (byte)0];
        // #7: 半人前 noun (1479520), not 半 + 人前で
        yield return ["例え半人前でも俺は魔術師なんだから", "半人前", 1479520, (byte)0];
        // #10: 貴様 pronoun (1223620), not 貴 prefix + 様に
        yield return ["貴様に用はないアサシン", "貴様", 1223620, (byte)0];
        // #17: なれ as 成る (1375610) potential/imperative, not 汝 pronoun (2174460)
        // (First sentence 冷静になれって collapses into the 冷静になる compound, 2557400)
        yield return ["それで冷静になれって、どんな修行僧よ。", "冷静になれって", 2557400, (byte)0];
        yield return ["誰だって、なろうと思えば、なれんだよ。", "なれ", 1375610, (byte)2];
        yield return ["誰かの特別になんてなれやしない。", "なれ", 1375610, (byte)2];

        // 殺し続ける: Sudachi mis-tags 殺 as Prefix (reading サツ) and merges し into し続ける.
        // Repair in ProcessSpecialCases: kanji-prefix whose +す is a verb → split into 殺し + 続ける.
        yield return ["殺し続ける存在だ", "殺し", 1299030, (byte)0];
        yield return ["殺し続ける存在だ", "続ける", 1405800, (byte)0];

        // 備え (noun 1485630) must win over 備える verb-stem match consistently — both occurrences.
        // Sudachi locks DictionaryForm=備え explicitly; the verb 備える should not override via stem.
        yield return ["現実は備えに備えを重ねた", "備え", 1485630, (byte)0];
        yield return ["備えに備えを重ねた", "備え", 1485630, (byte)0];

        // Positive 資格がある must not collapse to negative expression 資格がない (2159140).
        // Parser.cs negative-fallback gate requires surface to be actually negated.
        yield return ["お前はまだ継承者としての資格がある", "資格", 1312690, (byte)0];
        // Regression: genuinely negative expressions still compound-match via ない fallback
        yield return ["それは俺には関係ない", "関係ない", 2076040, (byte)0];
        yield return ["彼はびくともしません", "びくともしません", 1010720, (byte)0];

        // Sentence-final particle bonus な/ね/よ/ぞ
        // should resolve to the prt entry, not a homographic noun/name/interjection.
        yield return ["そうだな", "な", 2029110, (byte)0];
        yield return ["行くぞ", "ぞ", 2029130, (byte)0];
        yield return ["いいよ", "よ", 2029090, (byte)0];
        yield return ["うるさいわ", "わ", 2029100, (byte)0];

        // わい sentence-final particle (dialectal/emphatic) — Sudachi splits わ+い
        yield return ["慣れておるわい", "わい", 2201380, (byte)0];

        // 営 as standalone noun — Sudachi suffix reclassified to noun via user_dic
        yield return ["官の営による一大事業", "営", 1173410, (byte)0];

        // もと (元) as prefix "former" — Sudachi must not split into も+と particles
        yield return ["もと御同期の方", "もと", 1260670, (byte)5];

        // 眼差し (まなざし) — Sudachi must not split as 眼+差し向ける
        yield return ["眼差し向けるであろう", "眼差し", 1217200, (byte)0];

        // === Multi-word expressions spanning particles ===
        // 飴と鞭 (carrot and stick) — expression with particle と in the middle
        yield return ["飴と鞭", "飴と鞭", 1970680, (byte)0];

        // 腹に据えかねる — expression spanning に particle
        yield return ["腹に据えかねている", "腹に据えかねている", 2126260, (byte)0];

        // 合点がいく — expression spanning が particle; いった must not be 言った
        yield return ["合点がいった", "合点がいった", 1285130, (byte)0];

        // === Colloquial contractions ===
        // やっちまえ = やっちまう (1012780) imperative — Sudachi splits や/っち/ま/えー
        yield return ["やっちまえー！", "やっちまえー", 1012780, (byte)0];

        // === Onomatopoeia with と ===
        // ビクリと = びくりと (2207870, adv, on-mim), not Bikuri surname (5605618)
        yield return ["最初はビクリと体を引き攣らせた", "ビクリと", 2207870, (byte)3];

        // === なし absorption ===
        // 異常なし — なし (1529560) must not be absorbed as na-adj copula な + し
        yield return ["眼球運動異常なし歯肉の出血なし", "なし", 1529560, (byte)1];

        // === Greedy compound vs better split ===
        // 人見知り (1367260) should win over ただの人 (1891410) + 見知り
        yield return ["ただの人見知り", "人見知り", 1367260, (byte)0];

        // === Volitional + vowel elongation — original surface preserved ===
        // 遊ぼー → volitional of 遊ぶ (1542160), not 遊 (name) + ボー (bow)
        yield return ["遊ぼー", "遊ぼー", 1542160, (byte)0];

        // === つく homophone disambiguation ===
        // 嘘をつく → 吐く (1444150, to tell a lie), not 点く (1441400, to be lit)
        yield return ["適当な噓をつくな！", "つく", 1444150, (byte)1];
        yield return ["嘘はつかない", "つかない", 1444150, (byte)1];
        yield return ["こいつらは嘘はついていないんだろう。", "ついていない", 1444150, (byte)1];

        // ため息をつく → 吐く (1444150, to sigh), not 付く (1495740)
        yield return ["ため息もつかずに走り続けた", "つかずに", 1444150, (byte)1];

        // 水をくむ → 汲む (1229610, to draw water), not 組む (1397590, to assemble)
        yield return ["水をくむ", "くむ", 1229610, (byte)1];

        // 推測がつく → 付く (1495740, to be attached/settled), not 点く (1441400)
        yield return ["彼女自身も推測がついていた筈だ", "ついていた", 1495740, (byte)2];

        // ともつかない → 付く (1495740, to be attached), not 点く (1441400)
        yield return ["その行動力に感心とも呆れともつかない", "つかない", 1495740, (byte)2];

        // === Counter without number context ===
        // 色 should be noun (1357600), not counter (2097830) when not preceded by a number
        yield return ["世界が敵色だぜ、まったく", "色", 1357600, (byte)0];

        // === Vowel elongation on particles ===
        // けどー stripped to けど → (1004200), ケドー normalized to けど → (1004200)
        yield return ["バンフレットの校正なんだけどー", "けど", 1004200, (byte)0];
        yield return ["知ってると思うケドー", "けど", 1004200, (byte)0];

        // === Onomatopoeia vs rare nouns ===
        // コホン → cough/ahem (2579880), not 古本/secondhand book (1578510)
        yield return ["コホン、と咳払いしつつもう一度言う。", "コホン", 2579880, (byte)2];

        // === えと should be filler interjection (1001150), not sexagenary cycle 干支 (1650120) ===
        yield return ["えと", "えと", 1001150, (byte)8];

        // === 創造主 should be single word (1581250), not 創造+主たる ===
        yield return ["創造主たる", "創造主", 1581250, (byte)0];

        // === 完全無欠 should be single yojijukugo (1651155) — na-adj merges with な ===
        yield return ["完全無欠な人", "完全無欠な", 1651155, (byte)0];

        // === 諸君 should be (1344230), not 諸+君たち ===
        yield return ["諸君たち", "諸君", 1344230, (byte)0];

        // === なれど should be single conjunction (2173630) ===
        yield return ["身なれどご助力したい", "なれど", 2173630, (byte)0];

        // === 体力 should be (1409760), not 体+力無さ ===
        yield return ["体力無さすぎです", "体力", 1409760, (byte)0];

        // === 言いすぎた should resolve to verb 言い過ぎる (1848440) ===
        // Structural: transition rule drops た after noun (Sudachi classifies 言いすぎ as noun)
        yield return ["少し言いすぎたようだ許せ", "言いすぎた", 1848440, (byte)1];

        // === 強い should be adjective つよい (1236070), not verb 強いる/しいる (1236100) ===
        // Sudachi misclassifies 強い as 名詞 reading シイ; form scoring correctly picks adj-i
        // but reading-gating overrides to verb. Adj-i with exact surface match should win.
        yield return ["強いは弱いの実", "強い", 1236070, (byte)0];

        // === 訳 should be わけ (1538330), not やく (2057030) in these contexts ===
        yield return ["残念ながらそういう訳にはいかんな", "訳", 1538330, (byte)0];
        yield return ["自分でも訳が分からずに", "訳", 1538330, (byte)0];
        yield return ["見間違う訳がねぇ", "訳", 1538330, (byte)0];

        // === たちどころに should be adverb "at once" (1838090), not surname (5700879) ===
        yield return ["心臓の傷はたちどころに消え失せていた。", "たちどころに", 1838090, (byte)3];

        // === イキ should resolve to 行く (1578850), not 生きる (1378520) or 遺棄 (1587090) ===
        // Sudachi maps katakana イキ to 生きる; disambiguation overrides DictionaryForm to 行く
        yield return ["俺もイキました思いっきり出しました", "イキました", 1578850, (byte)2];
        yield return ["そろそろちゃんと僕と話して貰うわけには、いきませんか？", "いきません", 1578850, (byte)2];

        // === 事 in お祝い事 is ごと (2613010, nominalizing suffix), not こと ===
        yield return ["お祝い事に招かれたのですから", "事", 2613010, (byte)0];

        // === 住み易そう should resolve to 住み易い (2839799), not 住む (1334040) ===
        // Verb stem + adj-forming suffix: CombineInflections must set POS to IAdjective
        yield return ["住み易そう", "住み易そう", 2839799, (byte)1];

        // === 呼ぶ-family: verb conjugation forms must not be split by resegmentation ===
        // 呼びつけ (continuative of 呼びつける) — resegmentation was splitting to 呼び+つけ
        yield return ["何だったら俺を呼びつけでもいいし", "呼びつけ", 2870929, (byte)0];
        yield return ["ランスの呼びつけを無視して", "呼びつけ", 1266390, (byte)0];

        // 呼ばれ (continuative of 呼ばれる) — resegmentation was splitting to 呼+ばれ
        yield return ["呼ばれもしない", "呼ばれ", 1631030, (byte)0];
        yield return ["もしもセキュリティを呼ばれでもしたら", "呼ばれ", 1631030, (byte)0];

        // 呼ぼう (volitional of 呼ぶ) with emphatic long vowel mark
        yield return ["フィルチを呼ーぼう", "呼ぼう", 1266440, (byte)0];

        // 呼ばわる past tense (contracted 呼ばわった → 呼ばった)
        yield return ["彼女は強く戸を敲きつけて更に大きく呼ばった。", "呼ばった", 2870930, (byte)0];

        // ささやかれる = passive of ささやく (囁く, to whisper), not na-adj ささやか + れる
        yield return ["ささやかれるどころか", "ささやかれる", 1565670, (byte)2];

        // Should be まえ　and not ぜん
        yield return ["「前準備って……見当とかついたりするか？」", "前", 1392580, (byte)0];

        // ぶっ壊れる is not in JMDict — should split into ぶっ (prefix) + 壊れる
        yield return ["ぶっ壊れた", "壊れた", 1199900, (byte)0];

        // そうやって should be the conjunction (2772380), not 装薬 souyaku (1799180)
        yield return ["そうやって", "そうやって", 2772380, (byte)0];

        // 内儀 (wife) — WordId 1458040
        yield return ["その方はお館様のお内儀で", "内儀", 1458040, (byte)0];

        // 殿 after a name should be どの (honorific, 1442500) not でん (hall, 2859792)
        yield return ["ランド殿は反対した。", "殿", 1442500, (byte)0];

        // 町 standalone → まち/town (1603990), not ちょう/counter (2853569)
        // User dic overrides Sudachi reading チョウ→マチ for the common standalone reading
        yield return ["まずは町に戻ろう", "町", 1603990, (byte)0];

        // 大勢 → おおぜい/many people (1414220), not たいせい/general trend (1414230)
        yield return ["これほど大勢の方に参列していただき", "大勢", 1414220, (byte)0];

        // 意気軒昂 — yojijukugo compound (1156440), not split into 意気+軒昂
        yield return ["彼は意気軒昂な様子だった", "意気軒昂な", 1156440, (byte)0];

        // あーあ should preserve ー (not strip to ああ) and match 2205270 (aah!/oh no!)
        yield return ["……あーあ、馬鹿らしいことで悩んでたわ、私ってば。", "あーあ", 2205270, (byte)1];

        // ってば should combine って+ば into particle 2130420 (speaking of / I told you already)
        yield return ["……あーあ、馬鹿らしいことで悩んでたわ、私ってば。", "ってば", 2130420, (byte)1];

        // やや after case particle を should be adverb "somewhat" (1570120), not interjection "oh my!" (2771700)
        yield return ["ホープがグレイの提案をやや方向修正した。", "やや", 1570120, (byte)2];

        // 挙手せん: classical negative of 挙手する (not potential+slurred)
        yield return ["お二人は挙手せんでもよろしい", "挙手せん", 1232570, (byte)0];

        // どうあっても — expression "whatever happens; no matter what" (2250230)
        yield return ["どうあっても覆すことはできないのだから", "どうあっても", 2250230, (byte)1];

        // 節くれだった → 節くれだつ (1846970) "to be gnarled" — past tense of compound verb
        yield return ["節くれだった", "節くれだった", 1846970, (byte)0];

        // 糞 standalone → くそ/damn (1504900), not ふん/animal feces (2834408)
        yield return ["糞！", "糞", 1504900, (byte)0];

        // 鼠の糞 → ふん/droppings (2834408) — の preserves the フン reading
        yield return ["さらに黴や鼠の糞の臭いが淀んだ空気に漂っている。", "糞", 2834408, (byte)0];

        // 額 → ひたい/forehead (1207510) — body-contact context triggers ひたい
        yield return ["額にキスをした", "額", 1207510, (byte)0];

        // 皆 → みんな (1202150) — standalone 皆 is みんな in modern Japanese, not みな
        yield return ["皆で遊ぼう", "皆", 1202150, (byte)0];

        // 抱く → だく (2844997) — いだく is literary; modern 抱く is だく
        yield return ["彼女を抱く", "抱く", 2844997, (byte)0];

        // 一枚 → いちまい (1166710) — number + counter combined
        yield return ["一枚ください", "一枚", 1166710, (byte)0];

        // こと after verb+attributive must not merge with そう — hearsay そうだ, not appearance そう
        yield return ["できることそうだ。", "こと", 1313580, (byte)2];

        // こと after 言う = 事 (thing/matter), not 琴 (koto instrument)
        yield return ["ハウルさんの言うことしか", "こと", 1313580, (byte)2];

        // Colloquial ためとこう = ためておこう → 溜める (1552630)
        yield return ["よーしお小遣いためとこう！", "ためとこう", 1552630, (byte)1];

        // ぶちキレてる → ぶち切れる (2118860)
        yield return ["ぶちキレてる！", "ぶちキレてる", 2118860, (byte)2];

        // 誕生日会 as single token → birthday party (2773340)
        yield return ["まだ？お誕生日会遅れるよ", "誕生日会", 2773340, (byte)0];

        // すまなかった kept as single token (2844144)
        yield return ["余計な心配をさせてすまなかった", "すまなかった", 2844144, (byte)0];

        // いい子ぶって should match いい子ぶる (2121070), not いい子 (1835640)
        yield return ["何今さらいい子ぶってんだよ", "いい子ぶって", 2121070, (byte)0];

        // 走り高跳び as single token (1402450)
        yield return ["あなた日が落ちるまでずっと走り高跳びやってたことがあるでしょ", "走り高跳び", 1402450, (byte)0];

        // うちら should match pronoun (2868804)
        yield return ["うちらと同じく認識阻害の魔法で守られてるさかい", "うちら", 2868804, (byte)4];

        // いけず should match na-adj (2064110), not いける negative
        yield return ["いけずやわ", "いけず", 2064110, (byte)0];

        // 戦いたく should match 戦う (1596960) after 私戦 split
        yield return ["私戦いたくなんてないんです", "戦いたく", 1596960, (byte)0];

        // おおきに should match Kansai interjection (1412930), not おきに
        yield return ["煙幕焚いてもろておおきにな", "おおきに", 1412930, (byte)1];

        // 倒しちゃいます should match 倒す (1445770) via ちゃう contraction
        yield return ["倒しちゃいます", "倒しちゃいます", 1445770, (byte)0];

        // 信じて下さい → 信じる (1359040) — kanji 下さい must deconjugate like hiragana ください
        yield return ["信じて下さい", "信じて下さい", 1359040, (byte)0];

        // ではなかろうか should not match 中廊下 (2533920)
        yield return ["少し飲み過ぎではなかろうか", "ではなかろうか", 2724540, (byte)1];

        // 用事 (errands, 1546300) should not be swallowed into 私用 + 事
        yield return ["すみません私用事ができました", "用事", 1546300, (byte)0];

        // 着なきゃ should resolve to 着る (to wear, 1423000), not suffix ぎ + ない
        yield return ["本当にこれ着なきゃダメ", "着なきゃ", 1423000, (byte)0];

        // 長かった should resolve to 長い (long, 1429750), not 列長 compound + 方
        yield return ["列長かった？", "長かった", 1429750, (byte)0];

        // はいてる should resolve to 履く (to wear, 1607260), not は particle + 凍てる
        yield return ["でも、きっとパンツはいてる", "はいてる", 1607260, (byte)5];

        // 来てもろて / 来てくださる → 来る/くる (1547720), not きたる (1591270)
        yield return ["相川にウチに来てもろて", "来てもろて", 1547720, (byte)0];
        yield return ["舞さんも来てくださるんですか？", "来てくださる", 1547720, (byte)0];

        // 第一人者 as single token (1415350) — user dic entry
        yield return ["第一人者", "第一人者", 1415350, (byte)0];

        // 突然変異 as single token (1457060) — user dic entry
        yield return ["突然変異", "突然変異", 1457060, (byte)0];

        // 嘲り → noun 嘲り/ridicule (1565570), not verb 嘲る (1565590) ren'youkei
        yield return ["嘲りの笑みが浮かんでいた", "嘲り", 1565570, (byte)0];

        // くれる as auxiliary (非自立可能) → 呉れる/to give (1269130), not 暮れる/to get dark (1514960)
        yield return ["食べ残ししかくれなかったのに", "くれなかった", 1269130, (byte)1];
        yield return ["わたしの話を聞いてはくれませんでした", "くれませんでした", 1269130, (byte)1];

        // 丸暗記 as single token (1604220) — user dic entry
        yield return ["答え丸暗記させればさすがに何とかなるだろ", "丸暗記させれば", 1604220, (byte)0];

        // 得ぬ — suffix 得る ("able to") with archaic negative, should parse separately from preceding verb
        yield return ["逃れ得ぬ運命", "得ぬ", 1588760, (byte)0];

        // 打たれ強い — compound adjective (resilient)
        yield return ["打たれ強さ", "打たれ強さ", 2673090, (byte)0];

        // 全く以って — expression (utterly/completely)
        yield return ["全く以って", "全く以って", 1394820, (byte)1];

        // 同盟 before copula should be "alliance" (1599290), not "Japanese Confederation of Labor" (5744958)
        yield return ["銀河帝国と自由惑星同盟である", "同盟", 1599290, (byte)0];

        // 正気の沙汰 — JMDict removed the ではない form but the base expression should still resolve
        yield return ["正気の沙汰ではない", "正気の沙汰", 2682500, (byte)3];

        // げ suffix ("seeming") should be 2006580, not ゲ "videogame" (2812650)
        yield return ["落ち着かなさげに", "げ", 2006580, (byte)1];
        yield return ["皮肉げに笑いあう二人だが", "げ", 2006580, (byte)1];

        // ようかい should resolve to 妖怪 (1545300, ghost), not 溶解 (1546110, dissolution)
        yield return ["ようかい", "ようかい", 1545300, (byte)1];
        
        // 言うことを聞く (to obey, 2033700) — user_dic entry makes Sudachi tokenize as single verb
        yield return ["よく言うことを聞いてるね", "言うことを聞いてる", 2033700, (byte)0];

        // 凛と (dignified, 1564320) — Sudachi fuses adv-to words with their と particle into a single adverb token
        yield return ["凛とした表情で彼女の顔を見据えた", "凛と", 1564320, (byte)0];

        // いけすかねぇ (colloquial negative of いけ好かない) → 2007280 (nasty/disagreeable)
        // Sudachi splits into いけす (生け簀) + か + ねぇ; SpecialCases3 recombines
        yield return ["いけすかねぇ", "いけすかねぇ", 2007280, (byte)2];

        // 人魚 (mermaid, 1367090) — Sudachi fuses 人魚姫 as proper noun (name Marina); PreprocessText splits
        yield return ["おそらく人魚姫だな", "人魚", 1367090, (byte)0];

        // ３時 should be "3 o'clock" (1300520), not 三次 "third/tertiary" (1657930)
        yield return ["時刻は、午後３時３３分。", "３時", 1300520, (byte)0];
        yield return ["午前３時過ぎの街角は、まったく人の姿がない。", "３時", 1300520, (byte)0];

        // て下さる chain deconjugates to 信じる (1359040), not 信ずる (1359070)
        yield return ["お嬢様が私のことを信じて下さっていて", "信じて下さっていて", 1359040, (byte)0];
        // ずにいる chain resolves to 気付く
        yield return ["周囲の誰もがそれに気付かずにいた", "気付かずにいた", 1591330, (byte)1];
        // め after a noun is the derogatory suffix 奴 (2089650), not 目
        yield return ["そうして欲しいがな、畜生め", "め", 2089650, (byte)1];

        // S-E script gate: pure-katakana surface with a direct katakana entry must not fold to
        // a kanji/hiragana-only homograph (家紋 / 降る)
        yield return ["カモン。", "カモン", 2806650, (byte)0];
        // フル+に now merges into the lexicalized adverb フルに (2854949) — and must never be 降る
        yield return ["五感をフルに活動する。", "フルに", 2854949, (byte)0];
        // S-E lopsided-frequency override (≥10× word-level prior between surface-exact homographs)
        yield return ["こちら側か、むこう側か", "むこう", 1277140, (byte)2];
        yield return ["雪さん――主をカイロ代わりとはいかがなものかと", "カイロ", 1200640, (byte)2];
        // ...but it must not flip context decisions: にもよる is 依る, not the more frequent homographs
        yield return ["懐具合にもよるが", "よる", 1168660, (byte)4];
        yield return ["あんまりそういうふうには見えないけどなー", "ふう", 1499730, (byte)1];
        // NormalizedForm script-crossing guard: 屈し must not become 屈指 via Sudachi normalization
        yield return ["だが絶望はない、屈しはしない、諦観など以っての外だ。", "屈し", 1246540, (byte)0];
        // Classical attributive: 由々しき is its own entry (2423900), not a 由々しい conjugation
        yield return ["由々しき事態だと思う。", "由々しき", 2423900, (byte)0];

        // Classical す + べき merges to すべき (1006200) instead of dropping す…
        yield return ["己の魂を代償とする、本来なら禁忌とすべき戦術だ。", "すべき", 1006200, (byte)1];
        // …but after a suru-noun, す attaches left instead (満足す|べき policy)
        yield return ["それほど、大切な――尊敬すべき方です、あなたは", "尊敬す", 1406400, (byte)0];
        // なさ+すぎる merges and deconjugates to ない instead of dropping なさ
        yield return ["こんな調査方法では、当てがなさすぎるでしょう。", "なさすぎる", 1529520, (byte)1];
        // kanji adjective stem + すぎ → 近い, not the JMnedict name 近
        yield return ["レイスはあまりにも近すぎ、私とお嬢様はあまりに密着しすぎていた。", "近すぎ", 1242130, (byte)0];
        // volitional before とする outranks the seemingness reading (走り出す, not 走り出る)
        yield return ["走り出そうとする。", "走り出そう", 1402480, (byte)0];

        // elongated いえー resolves to いえ "no" (1583250), never to 遺影 via the いえい reading key
        yield return ["「いえー、やめときます」", "いえー", 1583250, (byte)1];

        // standalone ひと is 人, not the 一 bound-prefix entry Sudachi's lexeme suggests
        yield return ["もう、しょうがないひと……ほんとう、に、しょうが、ない……", "ひと", 1580640, (byte)1];
        // ま shred rejoins its following token instead of being deleted (ま+ねた → 真似る)
        yield return ["１つまねたくらいでいい気になるな", "まねた", 1363760, (byte)1];
        // Sudachi shreds kana 〜あう into あ+う interjections; rejoined as the reciprocal 合う
        yield return ["微笑みあう私たち。", "あう", 1284430, (byte)1];
        // すっ (adverb shred) fuses with the following verb when the combined word exists
        yield return ["分かっているくせに、すっとぼける。", "すっとぼける", 2833343, (byte)4];
        // 歩兵 is infantry (news1), not the shogi pawn Sudachi's フヒョウ lexeme suggests
        yield return ["「第２５歩兵連隊にいたんだと」", "歩兵", 1514440, (byte)0];

        // lexicalized adverbs: X的+に always merges; 無性に/意地でも are curated pairs
        yield return ["反射的に首を傾けた。", "反射的に", 1480480, (byte)0];
        yield return ["ただ、外界の環境によって狂うことを選んだあの男が無性に悲しかった。", "無性に", 1611900, (byte)0];
        yield return ["意地でもここを出るぞ無名！", "意地でも", 2518220, (byte)0];

        // し(為る連用形)+んだ is ungrammatical — kana しんだ is 死んだ
        yield return ["きみをそだてたおとこもきみのなかまもすべてしんだ。", "しんだ", 1310730, (byte)1];
        // trailing small vowel stripped: なんちゃってぇ still resolves to なんちゃって
        yield return ["……なんちゃってぇ！", "なんちゃってぇ", 2202800, (byte)0];
        // 連用形 directly after genitive の is impossible — 群れ is the noun, not 群れる
        yield return ["窓から見えたのは。虚ろな瞳をした、子供たちの群れ、群れ、群れ――。", "群れ", 1247510, (byte)0];

        // === user_dic / preprocess lattice fixes ===
        // 射出される is the suru-noun, not Sudachi's archaic 射出す(いだす)
        yield return ["わたしの体は、凄まじい勢いで射出された。", "射出", 1629190, (byte)0];
        // 哂う is a rare-kanji form of 笑う; Sudachi previously dropped the OOV 哂
        yield return ["強がって哂ったりもせず", "哂ったり", 1351360, (byte)2];
        // 使い捨て must not be eaten by 捨てだし(手出し)
        yield return ["防腐処理をしてないが、所詮使い捨てだしなぁ、ま、いいか", "使い捨て", 1597750, (byte)0];
        // kana やつれる resolves to 窶れる, not や+連れて行く
        yield return ["日に日にやつれていく母親を見ていられずに、キールは逃げた", "やつれていく", 1570210, (byte)1];
        // が after conjunctive から can only start the kana verb がなる "to yell"
        yield return ["ルダ、頼むからがなるな。", "がなる", 2101910, (byte)1];
        // 一人ごちた is 独りごちる, not 一人+ごちる
        yield return ["一人ごちた", "一人ごちた", 2056450, (byte)1];
        // 間中 after a clause is あいだじゅう "during" (1215610), not まなか "half a ken"
        yield return ["会話をしている間中、どうにか呼吸を整えられた", "間中", 1215610, (byte)0];

        // ごうごう (adv-to) should match 轟々 (1593500, thundering/roaring), not go-go
        // (moved from ShouldNotMatch, where the 4-element row crashed the 2-param test)
        yield return ["ごうごうとエレベーターの音が五月蝿い。", "ごうごう", 1593500, (byte)2];

        // === drop-gate rescues and quotative guards ===
        // ギシギシいってます = 言う te-form + ます, not the ぎしぎし adverb eating い
        yield return ["なんか結界がギシギシいってますよぅ", "いってます", 1587040, (byte)3];
        // reduplicated interjection rescue
        yield return ["フン、やれやれ…………ってコラ", "やれやれ", 1013000, (byte)0];
        // ああ before a verb is the demonstrative/interjection, not a dropped shred
        yield return ["何故なら、彼女がああなったのはお前の責任だ。", "ああ", 1565440, (byte)7];
        // emphatic ェッ must not destroy the imperative
        yield return ["黙れェッ！", "黙れ", 1534930, (byte)0];
        // prefixed adjective decomposition
        yield return ["薄赤い", "赤い", 1383240, (byte)0];
        // noun+する decomposition for non-vs nouns
        yield return ["突然大怪我して帰ってきて", "大怪我", 2078830, (byte)0];
        // classical attributive き → base adjective
        yield return ["白き尾", "白き", 1474910, (byte)0];

        // === benefactive/presumptive chain gaps ===
        // kanji て貰う now deconjugates: する + [te-morau, want], not a chainless partial match
        yield return ["して貰いたいこと", "して貰いたい", 1157170, (byte)1];
        // Sudachi df=立てる homograph rejected; deconj finds 役に立つ with the full chain
        yield return ["少しは我々の役に立って貰わんとな", "役に立って貰わん", 1537980, (byte)0];
        // ぬじゃろう = archaic negative presumptive of 行く
        yield return ["という訳にもいかぬじゃろう", "いかぬじゃろう", 1578850, (byte)2];
        // kanji 頂きたい stays a separate token after the te-form (benefactive boundary policy)
        yield return ["頼んで頂きたいことがある", "頂きたい", 1587290, (byte)0];

        // === colloquial gemination collapse (ばっか/バッカ = 馬鹿) ===
        // katakana バッカ as a noun is 馬鹿, not 麦価 "price of wheat" or the ばかり particle
        yield return ["バッカおまえ見りゃ分かるじゃん", "バッカ", 1601260, (byte)4];
        // ばっかな (Sudachi df=ばか) is 馬鹿+な, not 幕下 "military camp"
        yield return ["貴族ってのはあんなのばっかなのか", "ばっかな", 1601260, (byte)4];
        // genuine ばかり-particle uses stay untouched (regression guards for the collapse)
        yield return ["あれ、来たばっかなのに", "ばっか", 2857403, (byte)0];
        yield return ["生クリームばっかでどうすんのよ！", "ばっか", 2857403, (byte)0];

        // === whitelist entries resolve to the right words ===
        yield return ["持っているとはいえ、獣の速度", "とはいえ", 2037320, (byte)1];
        yield return ["あいよ。", "あいよ", 2835730, (byte)1];
        yield return ["いいや。", "いいや", 2857379, (byte)0];
        yield return ["ちょっと疲れて休みが欲しいな火薬のカス取りたいなって思っただけだい！", "だい", 2097680, (byte)0];
        yield return ["悔やんでも詮無きことです、お嬢様。", "詮無き", 1824270, (byte)0];
        yield return ["主従の誓いを破りかねなかったことも事実だった。", "かねなかった", 1922120, (byte)1];
        yield return ["シドには気をつけたまえ。", "たまえ", 2134420, (byte)1];

        // === scoring fixes ===
        // exact-surface archaic expression beats junk fallbacks (was buried by the −350 arch penalty)
        yield return ["人にあらざる咆哮。", "あらざる", 2854344, (byte)3];
        // 露になった = あらわ "exposed", not dew (1560070) or Russia (1147330)
        yield return ["密着した服が露になった。", "露", 1560080, (byte)0];
        // clause-final あり after と共に is the ある continuative, not 蟻
        yield return ["私はいつでも貴女と共にあり――そして", "あり", 1296400, (byte)2];

        // === batch 3 (T-H): beam gates ===
        // suru-noun before できる keeps the noun reading (not 真似る's continuative stem)
        yield return ["それでもこのテンションだけは真似できないよ", "真似", 1363740, (byte)0];
        // 私してない = 私 + する, never 私する (わたくしする)
        yield return ["そんな表情、私してない", "してない", 1157170, (byte)1];

        // 総 before a noun (総本部) is the prefix 総(そう) 1401470, not the counter 房総(ふさ) 1519300
        yield return ["政治総本部と国家保安省が対立しているといっても", "総", 1401470, (byte)0];
    }

    public static IEnumerable<object[]> FormSelectionShouldNotMatchCases()
    {
        // Kana surface ざと should not match kanji 里 — ざと is not a valid standalone reading
        yield return ["次第に周りからざわざと声が聞こえてくる。", "ざと"];

        // あん in moans/exclamations should not match 案 (1154770)
        yield return ["んぅっ、あん、あぁっ", "あん"];
        yield return ["ミレーニアさんあんやだぁふぐぐぐぐぐ…。", "あん"];

        // Positive 資格がある must not match as a combined compound at all (no JMDict entry exists).
        yield return ["お前はまだ継承者としての資格がある", "資格がある"];

        // いるか (dolphin) should not appear when か is embedded question particle
        yield return ["どうしてこんな所にいるかというと、それはまあ、ちょっとした話…。", "いるか"];

        // うん+ま (filler speech) must not merge into ウンマ (JMnedict organization)
        yield return ["「うんま、気にしなさんな」", "うんま"];
        yield return ["「ああ、うんまあそんな所だな。」", "うんま"];
        // katakana exclamations must not resolve to 遺影 through the いえい reading key
        yield return ["「なんちゃってイエーイ」", "イエーイ"];
        yield return ["レイの体が上下に赤く弾けて消えて、「イエイ！」", "イエイ"];
        // hiragana onomatopoeia must not match the name ヒューン or mutate into 庇陰
        yield return ["どひゅーんと、私を置き去りにして雪さんはヴァレリア様と共にこの場から逃れた。", "ひゅーん"];
    }

    [Theory]
    [MemberData(nameof(FormSelectionCases))]
    public async Task FormSelectionTest(string input, string expectedOriginalText, int expectedWordId, byte expectedReadingIndex)
    {
        var results = await Parse(input);

        var match = results.FirstOrDefault(w => w.OriginalText == expectedOriginalText);
        match.Should().NotBeNull($"expected token '{expectedOriginalText}' in parse results for '{input}'");
        match!.WordId.Should().Be(expectedWordId, $"WordId for '{expectedOriginalText}'");
        match.ReadingIndex.Should().Be(expectedReadingIndex, $"ReadingIndex for '{expectedOriginalText}'");
    }

    [Theory]
    [MemberData(nameof(FormSelectionShouldNotMatchCases))]
    public async Task FormSelectionShouldNotMatchTest(string input, string tokenText)
    {
        var results = await Parse(input);

        var match = results.FirstOrDefault(w => w.OriginalText == tokenText);
        match.Should().BeNull($"token '{tokenText}' should not match any word in '{input}'");
    }
}