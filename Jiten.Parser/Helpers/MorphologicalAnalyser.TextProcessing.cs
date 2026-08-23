using System.Text;
using System.Text.RegularExpressions;
using Jiten.Core.Data;
using Jiten.Core.Utils;

namespace Jiten.Parser;

public partial class MorphologicalAnalyser
{
    [GeneratedRegex(@"[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF\uFF21-\uFF3A\uFF41-\uFF5A\uFF10-\uFF19\u3005\u3001-\u3003\u3008-\u3011\u3014-\u301F\uFF01-\uFF0F\uFF1A-\uFF1F\uFF3B-\uFF3F\uFF5B-\uFF60\uFF62-\uFF65．\n…\u3000―\u2500()。！？「」）|]")]
    private static partial Regex NonJapaneseCharRegex();

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

    [GeneratedRegex(@"(?<=[\p{IsHiragana}\p{IsKatakana}\p{IsCJKUnifiedIdeographs}]{2})…+(?=[^\r\n…])")]
    private static partial Regex MidSentenceEllipsisRegex();

    [GeneratedRegex(@"…{2,}")]
    private static partial Regex EllipsisRunRegex();

    [GeneratedRegex(@"([ァ-ヴ]ンッ)(?=[ァ-ヴぁ-ゔ\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex KatakanaInterjectionTsuRegex();

    // Guard: in particle など (本などして) the ど is the 2nd mora of など, not colloquial どし(た/て/よ),
    // and in a renyoukei + もどす compound (取りもどして, 押しもどして, 追いもどして) the どし belongs to
    // 戻す — the class is the stem-final morae that front もどす; particles (でも, かも) never end
    // in them, so those stay expandable.
    [GeneratedRegex(@"(?<!な)(?<![りれびきちしい]も)どし(?=[たてよ])")]
    private static partial Regex ColloquialDoshiRegex();

    // ー followed by っ/っ after hiragana is emphatic/expressive (けどーっ → けど, 写るーっ → 写る)
    // EXCEPT before と, where ーっ is part of a mimetic adverb (ぼーっと, じーっと, ずーっと).
    [GeneratedRegex(@"(?<=[぀-ゟ])ー+[っッ]+(?!と)")]
    private static partial Regex EmphLongVowelSokuonRegex();

    // A small vowel stretching a mimetic adverb before っと (すぅっと, ふわぁっと, ざぁっと) is expressive
    // lengthening of the base form (すっと, ふわっと, ざっと) — collapse it so the adverb matches its
    // entry. Deleted only when the small vowel repeats the preceding mora's vowel: a different-row
    // small forms a digraph (ファ, ティ), which is that mora's spelling, not a stretch.
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

    // A small vowel before a clause-final ー run (行けぇーー, 切ったぁーー) is a shouted stretch; drop
    // the small vowel and keep the ー so the base form survives tokenisation (行けー, 切ったー).
    [GeneratedRegex(@"(?<=[ぁ-ゖ])[ぁぃぅぇぉ](?=ー+([\s\n！？!?]|$))")]
    private static partial Regex SmallVowelBeforeFinalLongVowelRegex();

    // ー stretching the final い of a shouted word (せんぱーい, すごーい, かわいーい) — drop it so
    // the base word survives. Hiragana context only (katakana ーイ endings are real loanword
    // orthography: ボーイ), at least two kana before the ー (おーい is itself a word), not after な
    // (なーい is the stretched negative), and no earlier ー in the run (わーいわーい repeats the
    // whole word わーい — its second ー is lexical, not a stretch).
    [GeneratedRegex(@"(?<=[ぁ-ゖ][ぁ-ゖ])(?<!な)(?<!ー[ぁ-ゖ]{1,8})ー+(?=い([\s\n！？!?」』）]|$))")]
    private static partial Regex LongVowelBeforeFinalIRegex();

    // Script-crossing emphatic small vowels: 黙れェッ！ / ヤダぁ！. A small vowel kana never
    // follows the opposite script as part of a real word (digraphs like ファ/ティ are same-script),
    // so the boundary is always real — Sudachi otherwise shreds 黙れェ into 黙|れ|ェ.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])([ァィゥェォ]+[っッ]?)|(?<=[ァ-ヴ])([ぁぃぅぇぉ]+[っッ]?)")]
    private static partial Regex ScriptCrossingSmallVowelRegex();

    // Same-script hiragana small-vowel elongation that defeats Sudachi (撃てぇ→撃|てぇ, 急げぇぇ→急|げぇぇ,
    // 移れぇぇ, 続けぇ, 行けぇぇ, 考えやがれぇ, 気をつけぇ). Two safe shapes (ぁぃぅぇぉ are vowel smalls, not the
    // digraph smalls ゃゅょゎ, so neither touches きゃ/しょ/ちゅ):
    //  (a) a RUN of ≥2 small vowels is unambiguous elongation — delete it.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])[ぁぃぅぇぉ]{2,}")]
    private static partial Regex SameScriptSmallVowelRunRegex();
    //  (b) a single small vowel right before っ/ッ at clause end is the shouted-imperative shape
    //      (撃てぇっ!) — drop the small vowel, keep the sokuon. Protects てめぇの / すげぇ! / 食べてぇ / ねぇ.
    [GeneratedRegex(@"(?<=[ぁ-ゖ])[ぁぃぅぇぉ]([っッ]+)(?=[\s\n]|$)")]
    private static partial Regex ShoutedImperativeSmallVowelRegex();

    // Comma-separated stutter fragment attached to the word it stutters: ぼ、ぼく / ぼっ、ぼぼ僕 / ば、ばっか.
    // The fragment must not be preceded by kana or kanji: stutters follow punctuation/quotes/start,
    // while a preceding word means a real particle (今は、はっきり) or repetition (ええ、ええ).
    [GeneratedRegex(@"(?<![ぁ-んァ-ヶー一-龯々])([ぁ-んァ-ヶ])[っッ]?[、,，]\s*(?=\1)")]
    private static partial Regex StutterFragmentRegex();

    // 4+ identical kana with optional っ/ッ/space between reps — spam/sound effects (ぼぼぼぼぼ).
    // Runs of exactly 3 are left alone: they occur in real words and across word boundaries
    // (落ち着いた+たたずまい, とっとと); short stutters are handled with context by MisparseGates.
    // Range ぁ-んァ-ヶ excludes ー (U+30FC) which is handled by MultipleLongVowelRegex.
    [GeneratedRegex(@"([ぁ-んァ-ヶ])([\sっッ]*\1){3,}")]
    private static partial Regex StutteringRunRegex();

    // 3+ identical digraph mora (じょじょじょ, ちゅちゅちゅ, しょしょしょ, etc.)
    // Small kana (ぁぃぅぇぉっゃゅょゎ / ァィゥェォッャュョヮ) cannot start a mora,
    // so (normal kana + small kana) captures exactly one digraph mora.
    [GeneratedRegex(@"([ぁ-んァ-ヶ][ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮ])([\sっッ]*\1){2,}")]
    private static partial Regex StutteringDigraphRunRegex();

    // 〜通り/〜どおり directly before quotative って: keep the compound whole (計画通り|って) instead of
    // letting って's gemination steal the り (計画+通+りって).
    [GeneratedRegex(@"(?<=通り|どおり)(?=って)")]
    private static partial Regex TooriQuotativeRegex();

    // A trailing hiragana long vowel (じゃ+あ) glued onto a following katakana word: Sudachi shifts the
    // boundary one char right (じゃ+あア→interjection ああ + ヒル), shredding アヒル. Re-assert the boundary.
    [GeneratedRegex(@"(?<=[ぁ-ゖ][あぁ])(?=[ァ-ヴ])")]
    private static partial Regex VowelTailKatakanaBoundaryRegex();

    // もし (if/perhaps) directly before a katakana run: Sudachi prefers the spurious token もしカ (2133220)
    // and steals the leading mora of an OOV katakana name (もしカ|ティア instead of もし|カティア), which the
    // name lookup then can't resolve. もし never forms a compound with a following katakana, so force the
    // boundary — this recovers ANY katakana name after もし, not one hardcoded entry. もしも/もしか/もしかして
    // are followed by hiragana, so the katakana lookahead leaves them untouched.
    [GeneratedRegex(@"もし(?=[ァ-ヴ])")]
    private static partial Regex MoshiKatakanaBoundaryRegex();

    // A case particle を/へ directly before a quotative って: Sudachi fuses them into a bogus verb token
    // (あんなことを、って → こと + をって[Verb], which is then dropped). No Japanese word contains をって/へって,
    // so forcing the boundary is always correct — generalises the literal にって split for the safe particles.
    [GeneratedRegex(@"(?<=[をへ])(?=って)")]
    private static partial Regex CaseParticleTteRegex();

    // Colloquial っしょ (=でしょ) after an i-adjective (すごい|っしょ, いい|っしょ). ColloquialSshoRegex's
    // (?<!い) guard protects 一緒 but also blocks adjective+っしょ; this branch splits when a hiragana
    // precedes the final い, excluding と so 〜と一緒/ずっと一緒 stay whole.
    [GeneratedRegex(@"(?<=[ぁ-ゖ]い)(?<!とい)っしょ[ーう]?(?=[\s\n]|$)")]
    private static partial Regex IAdjSshoRegex();

    // こいつ/そいつ/あいつ/どいつ and place pronouns ここ/そこ/あそこ/どこ + って: Sudachi shreds the
    // demonstrative (こい|つっ|て; あそこって → あ|そ|こっ|て). Force the boundary so the pronoun stays
    // whole and って is the particle (こいつ + ってば).
    [GeneratedRegex(@"([こそあど]いつ|ここ|そこ|あそこ|どこ)って")]
    private static partial Regex DemonstrativePronounTteRegex();

    // 景気づけよ → 景気づけ (景気付け 2010780) + よ; NOT the volitional 景気づけよう.
    [GeneratedRegex(@"景気づけよ(?!う)")]
    private static partial Regex KeikizukeYoRegex();

    // pronoun + plural ら + って: Sudachi OOV-swallows らって… (キミ|らってやっぱり). Force the boundary
    // before って after a pronoun's plural ら so ら stays the suffix and って the particle.
    [GeneratedRegex(@"(?<=(?:キミ|きみ|君|僕|ぼく|俺|おれ|お前|おまえ|あいつ|こいつ|そいつ|あなた|彼|彼女|私|わたし|あたし|うち)ら)(?=って)")]
    private static partial Regex PronounRaTteRegex();

    // Rough-speech elongated ある after a particle (覚えがあらァ, 金ならあらぁ → がある/ならある).
    [GeneratedRegex(@"(が|なら)あら[ァぁ]")]
    private static partial Regex ElongatedAruRegex();

    // Colloquial copula っす (=です) after an i-adjective (いい|っす, うまい|っす): split so っす resolves
    // to the copula 2269410 instead of being swallowed into a noun (いいっすか → 交喙/イスカ bird,
    // いいっすわ → すわ). Gated on what can follow the copula — a sentence-final particle (か/よ/ね/ぞ/な/
    // わ/ぜ/さ), a connective (けど/し/もん/から/が), punctuation, or clause end — so っす mid-word stays
    // untouched.
    [GeneratedRegex(@"(?<=[ぁ-ゖ]い)(?<!とい)っす(?=[かよねぞなわぜさ、。！？…」]|けど|し|もん|から|が|[\s\n]|$)")]
    private static partial Regex IAdjSsuRegex();

    private void PreprocessText(ref string text, bool preserveStopToken, out int rawContentCharCount)
    {
        text = text.Replace("<", " ").Replace(">", " ").Replace("〝", " ").Replace("〟", " ");
        text = text.ToFullWidthDigits();
        text = NonJapaneseCharRegex().Replace(text, "");

        rawContentCharCount = CountContentChars(text);

        if (!preserveStopToken)
            text = text.Replace(_stopToken, "");

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
        // が/ならあらァ: rough-speech elongated ある (覚えがあらァ, 金ならあらぁ). Must run before the
        // script-crossing small-vowel split detaches the ァ and strands あら as the interjection. The
        // preceding particle keeps the clause-initial exclamation あらぁ untouched.
        text = ElongatedAruRegex().Replace(text, "$1ある");
        text = ScriptCrossingSmallVowelRegex().Replace(text, $"{_stopToken}$1$2");
        text = SameScriptSmallVowelRunRegex().Replace(text, "");
        text = ShoutedImperativeSmallVowelRegex().Replace(text, "$1");
        // After the small-vowel deletions so a shielded lookbehind cannot misfire
        // (おぉぉーーい must reduce to おーい, not おい).
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
        // やるって: quotative って fragments the verb やる into や+る. Keep やる whole (run after the
        // はやる split so 流行る is unaffected).
        text = text.Replace("やるって", $"やる{_stopToken}って");
        // なんとなくって: the って is quotative after the adverb なんとなく — without the split the tail
        // re-analyses as なんと + なくって (the ない te-form).
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

        // Forced boundaries where Sudachi mis-cuts a colloquial/compound run.
        // (すいませんでした is handled lexically by the existing user_dic すいません 表現 entry; すみません
        //  needs the split because the kana string collides with the verb 済む — 済みませんでした.)
        text = text
            .Replace("すみませんでした", $"すみません{_stopToken}でした")  // すみ|ませんでした → すみません|でした (kana-only; verb 済 is kanji)
            .Replace("この世界", $"この{_stopToken}世界")                  // この世+界 → この|世界
            .Replace("だけって", $"だけ{_stopToken}って")                  // 広がっ+ただけ phantom けっ → だけ|って
            .Replace("ははーん", "ははん")                                // は+はーん(ハーン khan) → ははん(2096970)
            .Replace("にって", $"に{_stopToken}って")                     // にっ+てこ blob → に + って + こと
            .Replace("なんかいな", $"なんか{_stopToken}いな")             // なんか+い stolen → なんか + いない
            .Replace("繋がりって", $"繋がり{_stopToken}って")             // 繋|が|り shredded by って → 繋がり + って
            .Replace("んったら", $"ん{_stopToken}ったら")                 // ちゃ|んっ|たら → ちゃん + ったら
            .Replace("にいる", $"に{_stopToken}いる")                     // にいる(name 5408860) → に + いる(居る)
            .Replace("さっきこ", $"さっき{_stopToken}こ")                 // さっきこ→name さきこ(咲子) via sokuon-norm → さっき + こ(この/これ/ここ)
            .Replace("ないっていう", $"ない{_stopToken}っていう")          // って must not attach left into 〜ない expr
            // Sudachi's lexicon has the kana-row nouns (ガ行, ハ行…); in hiragana running text the
            // particle + 行〜 reading is the only real one (母が行かせまい, 聖域には行けっこない),
            // and the split boundary is also correct before every other 行-word (が行方, は行事).
            // The genuine row nouns stay reachable through their katakana spellings.
            .Replace("が行", $"が{_stopToken}行")                         // ガ行(1040670) fusion → が + 行〜
            .Replace("は行", $"は{_stopToken}行")                         // ハ行(1096940) fusion → は + 行〜
            // 金のこ (金ノコ, hacksaw abbr) swallows the start of 金のこと; gated on the full のこと
            // tail so a real hacksaw (金のこで切る) keeps its entry.
            .Replace("金のこと", $"金{_stopToken}のこと")                  // お金のこと → 金 + の + こと
            // 誰's だあれ kana form must not eat the copula of a preceding なんだ/何だ ("なんだあれ"
            // = なんだ + あれ). Keyed on the full なんだ/何だ so a genuine child-speech だあれ after an
            // ん-final nominal (お姉さんだあれ？) keeps 誰; a standalone だあれ？ is untouched too.
            .Replace("なんだあれ", $"なんだ{_stopToken}あれ")              // なんだあれ → なんだ + あれ
            .Replace("何だあれ", $"何だ{_stopToken}あれ")                  // 何だあれは → 何だ + あれ + は
            // Sudachi's てく contraction steals the く of a following くだせえ (勘弁して|く|だ|せえ);
            // the boundary keeps the te-form whole so the slurred 下さい can resolve as one token.
            .Replace("てくだせえ", $"て{_stopToken}くだせえ")              // ~してくだせえ → ~して + くだせえ
            // Dictionary-form verb + っす copula (わかるっす): Sudachi fuses るっす into a noun shard
            // and the verb loses its final mora. る never ends a word before っす otherwise.
            .Replace("るっす", $"る{_stopToken}っす")                      // わかるっすよ → わかる + っす + よ
            // です steals the す of a following 済まして (顔ですましている); the sequence です+まし
            // only exists as で + 澄まし/済まし in prose, never as polite です+まして.
            .Replace("ですまし", $"で{_stopToken}すまし")                  // ~ですましている → で + すまして + いる
            // ったらありゃしない ("nothing more ... than this") after an i-adjective: the いっ shard
            // otherwise reads as 行ったら. The full-expression tail keeps 会いに行ったら untouched.
            .Replace("いったらありゃしない", $"い{_stopToken}ったらありゃしない")
            // Counter つ + もらう: Sudachi cuts ２つ|も|らって, feeding らって to the ラッテ loanword.
            // つもらっ has no other reading (積もる's te-form is 積もって).
            .Replace("つもらっ", $"つ{_stopToken}もらっ")
            // Desiderative たい before a quotative って: Sudachi hands the い to 行って
            // (知りた|いって, 会いた|いって, 見た|いって). Splitting is correct in every reading —
            // an adjective tail (重たいって) and kana 鯛って take the same cut.
            .Replace("たいって", $"たい{_stopToken}って")
            // Dictionary-form verb + っていう (あるっていうなら): Sudachi fuses るっていう into a blob
            // that drops; る never ends a word before っていう otherwise.
            .Replace("るっていう", $"る{_stopToken}っていう")
            // 病は気から + quotative って: Sudachi's fused からって ("just because") consumes the
            // proverb's tail; the boundary restores から + って after the topic-marked 気.
            .Replace("は気からって", $"は気から{_stopToken}って");
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
        // Boundary anchors so connection costs can't drag these user_dic entries apart:
        // や+連れて行く must not eat やつれ (操れ=あやつれ excluded), 小木+曽って must not eat
        // 小木曽, 耳にする+する must not eat するすると
        text = YatsureRegex().Replace(text, $"{_stopToken}やつれ");
        // quotative と + かぶりを振る: SpecialCases とか otherwise steals the か (と|か|ぶり)
        text = text.Replace("とかぶりを振", $"と{_stopToken}かぶりを振");
        // Sudachi has 虫を殺す as one lattice token (the rare "control one's temper" idiom);
        // fiction overwhelmingly means literal insect-killing — keep it compositional
        text = text.Replace("虫を殺", $"虫を{_stopToken}殺");
        text = text.Replace("小木曽", $"小木曽{_stopToken}");
        text = text.Replace("するすると", $"{_stopToken}するすると");
        text = text.Replace("ぶっち切", "ぶち切");
        text = EllipsisBeforeEmphaticTsuRegex().Replace(text, "");
        text = EmphaticTsuRegex().Replace(text, $"{_stopToken}$1");
        // Split the intensifying prefix ぶっ from 壊れ AFTER EmphaticTsuRegex (脳味噌|が|ぶっ|壊れた).
        // Doing it before would leave っ in front of the stop token (not the 壊 kanji), so EmphaticTsuRegex
        // would cut ぶ|っ and Sudachi would merge the preceding が+ぶ→がぶ (then dropped as a kana name).
        // ぶっ壊れる has no JMDict entry, so ぶっ (2698210) stays a standalone prefix + 壊れた.
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
            // 前大戦: Sudachi cuts 前大(surname Maeo)+戦 → force 前 + 大戦
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
        text = text.Replace("…\r", "。\r").Replace("…\n", "。\n");
    }

    private static int CountContentChars(string text)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c is >= '぀' and <= 'ゟ'   // hiragana
                  or >= '゠' and <= 'ヿ'    // katakana (incl. ー)
                  or >= '一' and <= '龯'    // CJK
                  or '々'                       // 々
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

    private List<SentenceInfo> SplitIntoSentences(string text, List<WordInfo> wordInfos)
    {
        // Normalise text - remove line breaks for consistent sentence boundaries
        text = text.Replace("\r", "").Replace("\n", "");

        // Phase 1: Build sentences AND track their start positions in the normalised text
        // This allows O(1) sentence lookup by position instead of repeated IndexOf calls
        var sentenceData = new List<(SentenceInfo info, int startPos)>();
        var sb = new StringBuilder();
        bool seenEnder = false;
        int sentenceStartPos = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];
            sb.Append(current);

            if (_sentenceEnders.Contains(current))
            {
                seenEnder = true;
                continue;
            }

            if (seenEnder)
            {
                if (_sentenceEnders.Contains(current))
                    continue;

                // Flush sentence (without the last character which belongs to next)
                var sentenceText = sb.ToString(0, sb.Length - 1);
                sentenceData.Add((new SentenceInfo(sentenceText), sentenceStartPos));

                // Next sentence starts at current character position
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

        // Phase 2: Assign words using precomputed offsets
        // Token offsets were computed once from raw Sudachi output (before pipeline stages),
        // then propagated through all merge/split stages. This avoids fragile IndexOf matching
        // that breaks when stages modify token Text (e.g., RepairVowelElongation strips ー).
        int sentenceIdx = 0;

        foreach (var word in wordInfos)
        {
            if (string.IsNullOrEmpty(word.Text) || word.PartOfSpeech == PartOfSpeech.BlankSpace)
                continue;

            if (word.StartOffset < 0 || word.EndOffset < 0)
                continue;

            int wordPos = word.StartOffset;
            int wordEnd = word.EndOffset;

            // Advance to the correct sentence based on word position
            while (sentenceIdx < sentenceData.Count - 1)
            {
                int nextSentenceStart = sentenceData[sentenceIdx + 1].startPos;
                if (wordPos < nextSentenceStart)
                    break;
                sentenceIdx++;
            }

            var (sentence, sentenceStart) = sentenceData[sentenceIdx];
            int sentenceEnd = sentenceStart + sentence.Text.Length;

            // Handle words that span sentence boundaries - merge sentences
            while (wordEnd > sentenceEnd && sentenceIdx + 1 < sentenceData.Count)
            {
                var nextSentence = sentenceData[sentenceIdx + 1].info;
                sentence.Text += nextSentence.Text;
                sentenceData.RemoveAt(sentenceIdx + 1);
                sentenceEnd = sentenceStart + sentence.Text.Length;
            }

            // Calculate position within the sentence and add word
            int posInSentence = wordPos - sentenceStart;
            int spanLength = wordEnd - wordPos;
            sentence.Words.Add((word, posInSentence, spanLength));
        }

        return sentenceData.Select(s => s.info).ToList();
    }
}
