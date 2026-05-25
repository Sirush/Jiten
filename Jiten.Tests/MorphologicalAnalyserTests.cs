/// Tests taken from https://github.com/tshatrov/ichiran/blob/master/tests.lisp

using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Jiten.Tests;

using Xunit;
using FluentAssertions;

public class MorphologicalAnalyserTests
{
    private static IDbContextFactory<JitenDbContext>? _contextFactory;

    private async Task<IEnumerable<string>> Parse(string text)
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

        var result = await Jiten.Parser.Parser.ParseText(_contextFactory, text);
        return result.Select(w => w.OriginalText);
    }


    public static IEnumerable<object[]> SegmentationCases()
    {
        yield return ["ご注文はうさぎですか", new[] { "ご注文", "は", "うさぎ", "ですか" }];
        yield return ["しませんか", new[] { "しません", "か" }];
        yield return ["ドンマイ", new[] { "ドンマイ" }];
        yield return ["みんな土足でおいで", new[] { "みんな", "土足で", "おいで" }];
        yield return ["おもわぬオチ提供中", new[] { "おもわぬ", "オチ", "提供", "中" }];
        yield return ["わたし", new[] { "わたし" }];
        yield return ["お姉ちゃんにまかせて地球まるごと", new[] { "お姉ちゃん", "に", "まかせて", "地球", "まるごと" }];
        yield return ["名人になってるはず", new[] { "名人", "に", "なってる", "はず" }];
        yield return ["いいとこ", new[] { "いいとこ" }];
        yield return ["はしゃいじゃう", new[] { "はしゃいじゃう" }];
        yield return ["分かっちゃうのよ", new[] { "分かっちゃう", "の", "よ" }];
        yield return ["懐かしく新しいまだそしてまた", new[] { "懐かしく", "新しい", "まだ", "そして", "また" }];
        yield return ["あたりまえみたいに思い出いっぱい", new[] { "あたりまえ", "みたい", "に", "思い出", "いっぱい" }];
        yield return ["何でもない日々とっておきのメモリアル", new[] { "何でもない", "日々", "とっておき", "の", "メモリアル" }];
        yield return ["しつれいしなければならないんです", new[] { "しつれいしなければ", "ならない", "んです" }];
        yield return ["だけど気付けば馴染んじゃってる", new[] { "だけど", "気付けば", "馴染んじゃってる" }];
        yield return ["飲んで笑っちゃえば", new[] { "飲んで", "笑っちゃえば" }];
        yield return ["なんで", new[] { "なんで" }];
        yield return ["遠慮しないでね", new[] { "遠慮しないで", "ね" }];
        yield return ["出かけるまえに", new[] { "出かける", "まえに" }];
        yield return ["感じたいでしょ", new[] { "感じたい", "でしょ" }];
        yield return ["まじで", new[] { "まじ", "で" }];
        yield return ["その山を越えたとき", new[] { "その", "山", "を", "越えた", "とき" }];
        yield return ["遊びたいのに", new[] { "遊びたい", "のに" }];
        yield return ["しながき", new[] { "しながき" }];
        yield return ["楽しさ求めて", new[] { "楽しさ", "求めて" }];
        yield return ["日常のなかにも", new[] { "日常", "の", "なかにも" }];
        yield return ["ほんとは好きなんだと", new[] { "ほんと", "は", "好き", "なんだと" }];
        yield return ["内緒なの", new[] { "内緒", "なの" }];
        yield return ["魚が好きじゃない", new[] { "魚", "が", "好き", "じゃない" }];
        yield return ["物語になってく", new[] { "物語", "に", "なってく" }];
        yield return ["書いてきてくださった", new[] { "書いてきてくださった" }];
        yield return ["今日は何の日", new[] { "今日", "は", "何の", "日" }];
        yield return ["何から話そうか", new[] { "何", "から", "話そう", "か" }];
        yield return ["進化してく友情", new[] { "進化してく", "友情" }];
        yield return ["私に任せてくれ", new[] { "私", "に", "任せてくれ" }];
        yield return ["時までに帰ってくると約束してくれるのなら外出してよろしい", new[] { "時", "まで", "に", "帰ってくる", "と", "約束してくれる", "の", "なら", "外出して", "よろしい" }];
        yield return ["雨が降りそうな気がします", new[] { "雨が降りそう", "な", "気がします" }];
        yield return ["新しそうだ", new[] { "新しそう", "だ" }];
        yield return ["本を読んだりテレビを見たりします", new[] { "本", "を", "読んだり", "テレビ", "を", "見たり", "します" }];
        yield return ["今日母はたぶんうちにいるでしょう", new[] { "今日", "母", "は", "たぶん", "うち", "に", "いる", "でしょう" }];
        yield return ["赤かったろうです", new[] { "赤かったろう", "です" }];
        yield return ["そう呼んでくれていい", new[] { "そう", "呼んでくれて", "いい" }];
        yield return ["払わなくてもいい", new[] { "払わなくて", "も", "いい" }];
        yield return ["体に悪いと知りながらタバコをやめることはできない", new[] { "体に悪い", "と", "知りながら", "タバコをやめる", "こと", "は", "できない" }];
        yield return ["いつもどうり", new[] { "いつもどうり" }];
        yield return ["微笑みはまぶしすぎる", new[] { "微笑み", "は", "まぶしすぎる" }];
        yield return ["うかつすぎる", new[] { "うかつすぎる" }];
        yield return ["優しすぎそのうえカッコいいの", new[] { "優しすぎ", "そのうえ", "カッコいい", "の" }];
        yield return ["内心ドキッとした", new[] { "内心", "ドキッと", "した" }];
        yield return ["ドキっとしたからって", new[] { "ドキっと", "した", "からって" }];
        yield return ["実はさっきチラッと見えたんだけど", new[] { "実は", "さっき", "チラッと", "見えた", "んだ", "けど" }];
        yield return ["この本は複雑すぎるから", new[] { "この", "本", "は", "複雑", "すぎる", "から" }];
        yield return ["かわいいです", new[] { "かわいい", "です" }];
        yield return ["なんだから", new[] { "なん", "だから" }];
        yield return ["名付けたい", new[] { "名付けたい" }];
        yield return ["切なくなってしまう", new[] { "切なく", "なってしまう" }];
        yield return ["誰かいなくなった", new[] { "誰か", "いなくなった" }];
        yield return ["思い出すな", new[] { "思い出す", "な" }];
        yield return ["かなって思ったら", new[] { "かな", "って", "思ったら" }];
        yield return ["法律にかなっているさま", new[] { "法律", "に", "かなっている", "さま" }];
        yield return ["こんなもんでいいかな", new[] { "こんな", "もん", "で", "いい", "かな" }];
        yield return ["ことすら難しい", new[] { "こと", "すら", "難しい" }];
        yield return ["投下しました", new[] { "投下しました" }];
        yield return ["そんなのでいいと思ってるの", new[] { "そんな", "ので", "いい", "と", "思ってる", "の" }];
        yield return ["だけが墓参りしてた", new[] { "だけ", "が", "墓参り", "してた" }];
        yield return ["はいいんだけどな", new[] { "は", "いい", "んだ", "けど", "な" }];
        yield return ["反論は認めません", new[] { "反論", "は", "認めません" }];
        yield return ["見たような気がする", new[] { "見た", "ような気がする" }];
        yield return ["幽霊を見たような顔つきをしていた", new[] { "幽霊", "を", "見た", "ような", "顔つき", "を", "していた" }];
        yield return ["元気になる", new[] { "元気", "に", "なる" }];
        yield return ["半端なかった", new[] { "半端なかった" }];
        yield return ["一人ですね", new[] { "一人", "です", "ね" }];
        yield return ["行事がある", new[] { "行事", "が", "ある" }];
        yield return ["当てられたものになる", new[] { "当てられた", "ものになる" }];
        yield return ["ことができず", new[] { "ことができず" }];
        yield return ["一生一度だけの忘られぬ約束", new[] { "一生", "一度だけ", "の", "忘られぬ", "約束" }];
        yield return ["やらずにこの路線でよかったのに", new[] { "やらずに", "この", "路線", "で", "よかった", "のに" }];
        yield return ["歌ってしまいそう", new[] { "歌ってしまいそう" }];
        yield return ["しまいそう", new[] { "しまいそう" }];
        yield return ["何ですか", new[] { "何", "ですか" }];
        yield return ["浮かれたいから", new[] { "浮かれたい", "から" }];
        yield return ["なくなっちゃう", new[] { "なくなっちゃう" }];
        yield return ["になりそうだけど", new[] { "に", "なりそう", "だけど" }];
        yield return ["これは辛い選択になりそうだな", new[] { "これ", "は", "辛い", "選択", "に", "なりそう", "だ", "な" }];
        yield return ["はっきりしそうだな", new[] { "はっきり", "しそう", "だ", "な" }];
        yield return ["泣きそうなんだけど", new[] { "泣きそう", "なんだ", "けど" }];
        yield return ["これですね", new[] { "これ", "です", "ね" }];
        yield return ["忘れなく", new[] { "忘れなく" }];
        yield return ["じゃないですか", new[] { "じゃない", "ですか" }];
        yield return ["純粋さ健気さ", new[] { "純粋さ", "健気さ" }];
        yield return ["着てたからね", new[] { "着てた", "から", "ね" }];
        yield return ["仕出かすからだと思います", new[] { "仕出かす", "から", "だ", "と", "思います" }];
        yield return ["ここ……からだよね？", new[] { "ここ", "から", "だよね" }];
        yield return ["みんながした", new[] { "みんな", "が", "した" }];
        yield return ["ほうが速いと", new[] { "ほう", "が", "速い", "と" }];
        yield return ["注意してください", new[] { "注意してください" }];
        yield return ["昨日といいどうしてこう", new[] { "昨日", "といい", "どうして", "こう" }];
        yield return ["いっぱいきそう", new[] { "いっぱい", "きそう" }];
        yield return ["仲良しになったら", new[] { "仲良し", "に", "なったら" }];
        yield return ["全くといっていい", new[] { "全く", "と", "いって", "いい" }];
        yield return ["発狂しそうなんだ", new[] { "発狂しそう", "なんだ" }];
        yield return ["引き上げられた", new[] { "引き上げられた" }];
        yield return ["をつかむため", new[] { "を", "つかむ", "ため" }];
        yield return ["ときが自分", new[] { "とき", "が", "自分" }];
        yield return ["もうこころ", new[] { "もう", "こころ" }];
        yield return ["届けしたら", new[] { "届け", "したら" }];
        yield return ["届出さなかったの", new[] { "届", "出さなかった", "の" }];
        yield return ["お答えする", new[] { "お", "答え", "する" }];
        yield return ["お答えしましょう", new[] { "お", "答え", "しましょう" }];
        yield return ["おまえら低いんだよ", new[] { "おまえら", "低い", "んだ", "よ" }];
        yield return ["すべてがかかっていると思いながら", new[] { "すべて", "が", "かかっている", "と", "思いながら" }];
        yield return ["がいないとこの", new[] { "が", "いない", "と", "この" }];
        yield return ["エロいと思っちゃう", new[] { "エロい", "と", "思っちゃう" }];
        yield return ["変わり映えしない", new[] { "変わり映え", "しない" }];
        yield return ["あなたがいなきゃこんな計画思いつかなかった", new[] { "あなた", "が", "いなきゃ", "こんな", "計画", "思いつかなかった" }];
        yield return ["見たかったです", new[] { "見たかった", "です" }];
        yield return ["出来て楽しかったな", new[] { "出来て", "楽しかった", "な" }];
        yield return ["つかってください", new[] { "つかってください" }];
        yield return ["誰もが思ってた", new[] { "誰も", "が", "思ってた" }];
        yield return ["参考にしたらしい", new[] { "参考にした", "らしい" }];
        yield return ["狙いやすそうで", new[] { "狙い", "やすそう", "で" }];
        yield return ["予定はございませんので", new[] { "予定", "は", "ございません", "ので" }];
        yield return ["犬はトラックにはねられた", new[] { "犬", "は", "トラック", "に", "はねられた" }];
        yield return ["仕事してください", new[] { "仕事してください" }];
        yield return ["おいかけっこしましょ", new[] { "おいかけっこ", "しましょ" }];
        yield return ["イラストカードが付きます", new[] { "イラスト", "カード", "が", "付きます" }];
        yield return ["じゃないかしら", new[] { "じゃない", "かしら" }];
        yield return ["いつか本当に", new[] { "いつか", "本当に" }];
        yield return ["言い方もします", new[] { "言い方", "も", "します" }];
        yield return ["何でこれ", new[] { "何で", "これ" }];
        yield return ["こういう物語ができるんだ", new[] { "こういう", "物語", "が", "できる", "んだ" }];
        yield return ["といったところでしょうか", new[] { "といった", "ところ", "でしょうか" }];
        yield return ["広めたいと思っている", new[] { "広めたい", "と", "思っている" }];
        yield return ["やらしいです", new[] { "やらしい", "です" }];
        yield return ["荒いとこもある", new[] { "荒い", "とこ", "も", "ある" }];
        yield return ["あったかいとこ行こう", new[] { "あったかい", "とこ", "行こう" }];
        yield return ["ぶっちゃけ話", new[] { "ぶっちゃけ", "話" }];
        yield return ["いけないわー", new[] { "いけない", "わ" }];
        yield return ["社長としてやっていけないわ", new[] { "社長", "として", "やっていけない", "わ" }];
        yield return ["よくわかんないけど", new[] { "よく", "わかんない", "けど" }];
        yield return ["ほうがいいんじゃないの", new[] { "ほうがいい", "ん", "じゃない", "の" }];
        yield return ["こんなんじゃ", new[] { "こんなん", "じゃ" }];
        yield return ["増やしたほうがいいな", new[] { "増やした", "ほうがいい", "な" }];
        yield return ["屈しやすいものだ", new[] { "屈し", "やすい", "もの", "だ" }];
        yield return ["目をもっている", new[] { "目", "を", "もっている" }];
        yield return ["これが君のなすべきものだ", new[] { "これ", "が", "君", "の", "なすべき", "もの", "だ" }];
        yield return ["泥棒をつかまえた", new[] { "泥棒", "を", "つかまえた" }];
        yield return ["金もないし友達もいません", new[] { "金", "も", "ない", "し", "友達", "も", "いません" }];
        yield return ["出来たからほら見てよ", new[] { "出来た", "から", "ほら", "見て", "よ" }];
        yield return ["眠いからもう寝るね", new[] { "眠い", "から", "もう", "寝る", "ね" }];
        yield return ["見本通りに", new[] { "見本", "通り", "に" }];
        yield return ["不適応", new[] { "不", "適応" }];
        yield return ["良いそうです", new[] { "良いそう", "です" }];
        yield return ["むらむらとわいた", new[] { "むらむら", "と", "わいた" }];
        yield return ["否定しちゃいけない", new[] { "否定しちゃ", "いけない" }];
        yield return ["観たいです", new[] { "観たい", "です" }];
        yield return ["あんたはわからん", new[] { "あんた", "は", "わからん" }];
        yield return ["見られたくないとこ", new[] { "見られたくない", "とこ" }];
        yield return ["三十八", new[] { "三十", "八" }];
        yield return ["五十七歳", new[] { "五十", "七", "歳" }];
        yield return ["六十一歳", new[] { "六十", "一", "歳" }];
        yield return ["九十九人", new[] { "九十", "九", "人" }];
        yield return ["三千万", new[] { "三千", "万" }];
        yield return ["百二十三", new[] { "百", "二十", "三" }];
        yield return ["四百五十六", new[] { "四百", "五十", "六" }];
        yield return ["エロそうだヤバそうだ", new[] { "エロそう", "だ", "ヤバそう", "だ" }];
        yield return ["睡眠を十分にとってください", new[] { "睡眠", "を", "十分", "にとって", "ください" }];
        yield return ["そうなんだけど", new[] { "そう", "なんだ", "けど" }];
        yield return ["進んでない", new[] { "進んでない" }];
        yield return ["一回だけであとは言わない", new[] { "一回", "だけ", "で", "あと", "は", "言わない" }];
        yield return ["ご親切に恐縮しております", new[] { "ご親切に", "恐縮しております" }];
        yield return ["官吏となっておる者がある", new[] { "官吏", "と", "なっておる", "者", "が", "ある" }];
        yield return ["間違えておられたようですね", new[] { "間違えて", "おられた", "ようです", "ね" }];
        yield return ["人気のせいな", new[] { "人気", "の", "せい", "な" }];
        yield return ["コレはアレ", new[] { "コレ", "は", "アレ" }];
        yield return ["上に文字があったり", new[] { "上", "に", "文字", "が", "あったり" }];
        yield return ["言っただろ", new[] { "言った", "だろ" }];
        // yield return ["嵐が起ころうとしている", new[] { "嵐", "が", "起ころうとしている" }];
        yield return ["知らないでしょう", new[] { "知らない", "でしょう" }];
        yield return ["読まないでしょう", new[] { "読まない", "でしょう" }];
        yield return ["来ないでしょう", new[] { "来ない", "でしょう" }];
        yield return ["何もかもがめんどい", new[] { "何もかも", "が", "めんどい" }];
        yield return ["なにもかもがめんどい", new[] { "なにもかも", "が", "めんどい" }];
        yield return ["あいつ規制されりゃいいのに", new[] { "あいつ", "規制されりゃ", "いい", "のに" }];
        yield return ["塗ってみようと思って", new[] { "塗ってみよう", "と", "思って" }];
        yield return ["肩を並べられなかった", new[] { "肩を並べられなかった" }];
        yield return ["じゃなくて良かった", new[] { "じゃなくて", "良かった" }];
        yield return ["申し訳なさそう", new[] { "申し訳なさそう" }];
        yield return ["決まってたし", new[] { "決まってた", "し" }];
        yield return ["決まっている", new[] { "決まっている" }];
        yield return ["恐れ入りました", new[] { "恐れ入りました" }];
        yield return ["はうまい", new[] { "は", "うまい" }];
        yield return ["弾け飛びました", new[] { "弾け飛びました" }];
        yield return ["ぶっこんでいるようで", new[] { "ぶっこんでいる", "よう", "で" }];
        yield return ["であるようだ", new[] { "である", "ようだ" }];
        yield return ["なかったようで安心した", new[] { "なかった", "よう", "で", "安心した" }];
        yield return ["食べようか", new[] { "食べよう", "か" }];
        yield return ["じゃないけど下手に", new[] { "じゃない", "けど", "下手", "に" }];
        yield return ["的にそうではない", new[] { "的", "に", "そう", "ではない" }];
        yield return ["恋の終わりではなく", new[] { "恋", "の", "終わり", "ではなく" }];
        yield return ["問題ではなかった", new[] { "問題", "ではなかった" }];
        yield return ["入り込めなかった", new[] { "入り込めなかった" }];
        yield return ["がいまいちなんだよ", new[] { "が", "いまいち", "なんだ", "よ" }];
        yield return ["脱がしにかかってる", new[] { "脱がし", "に", "かかってる" }];
        yield return ["必死になってる", new[] { "必死になってる" }];
        yield return ["臆病風に吹かれていた", new[] { "臆病風に吹かれていた" }];
        yield return ["安心させた", new[] { "安心させた" }];
        yield return ["人が好きそうだ", new[] { "人", "が", "好きそう", "だ" }];
        yield return ["もっていこうとする", new[] { "もっていこう", "とする" }];
        yield return ["増やして", new[] { "増やして" }];
        yield return ["ぜいたくで", new[] { "ぜいたく", "で" }];
        yield return ["したくらいで", new[] { "した", "くらい", "で" }];
        // TODO: Sudachi greedily absorbs も into もう — needs resegmentation or user_dic fix
        // yield return ["でもうまく人", new[] { "でも", "うまく", "人" }];
        yield return ["好き嫌いもしないように", new[] { "好き嫌い", "も", "しない", "ように" }];
        yield return ["のどこが思える", new[] { "の", "どこ", "が", "思える" }];
        yield return ["調子にのらないほうが", new[] { "調子にのらない", "ほう", "が" }];
        yield return ["こなさそう", new[] { "こなさそう" }];
        yield return ["伸びてこなさそう", new[] { "伸びてこなさそう" }];
        yield return ["出来なさそう", new[] { "出来なさそう" }];
        yield return ["食べなさそう", new[] { "食べなさそう" }];
        yield return ["手にとって", new[] { "手にとって" }];
        yield return ["早く杯を手にしてほしいのだがな", new[] { "早く", "杯", "を", "手にして", "ほしい", "の", "だが", "な" }];
        yield return ["私にとっては少しおかしいです", new[] { "私", "にとって", "は", "少し", "おかしい", "です" }];
        yield return ["パーティーは", new[] { "パーティー", "は" }];
        yield return ["彼以上のばかはいない", new[] { "彼", "以上", "の", "ばか", "は", "いない" }];
        yield return ["君がいないと淋しい", new[] { "君", "が", "いない", "と", "淋しい" }];
        yield return ["思いきって", new[] { "思いきって" }];
        yield return ["思いきっている", new[] { "思いきっている" }];
        yield return ["大事になります", new[] { "大事", "に", "なります" }];
        yield return ["元気にします", new[] { "元気", "に", "します" }];
        yield return ["ご迷惑おかけしてすみません", new[] { "ご迷惑", "おかけして", "すみません" }];
        yield return ["不便をおかけすることを謝ります", new[] { "不便", "を", "おかけする", "こと", "を", "謝ります" }];
        yield return ["お手数おかけし申し訳ないが", new[] { "お手数", "おかけし", "申し訳ない", "が" }];
        yield return ["私はあなたにお手数をおかけました", new[] { "私", "は", "あなた", "に", "お手数", "を", "お", "かけました" }];
        yield return ["ここにおかけなさい", new[] { "ここ", "に", "お", "かけなさい" }];
        yield return ["弾き出されてる", new[] { "弾き出されてる" }];
        yield return ["ぶっちゃけ", new[] { "ぶっちゃけ" }];
        yield return ["賢人たち", new[] { "賢人", "たち" }];
        yield return ["差ついた", new[] { "差", "ついた" }];
        yield return ["ですら", new[] { "ですら" }];
        yield return ["でさえ", new[] { "でさえ" }];
        yield return ["みごとにやってのける", new[] { "みごと", "に", "やってのける" }];
        yield return ["いる", new[] { "いる" }];
        yield return ["お下がり", new[] { "お下がり" }];
        yield return ["みんなにうらやましがられている", new[] { "みんな", "に", "うらやましがられている" }];
        yield return ["悪がられて", new[] { "悪がられて" }];
        yield return ["期待されがちなので男女", new[] { "期待され", "がち", "なので", "男女" }];
        yield return ["とぎれがちに話す", new[] { "とぎれがち", "に", "話す" }];
        yield return ["さほど", new[] { "さほど" }];
        yield return ["大きさほどもある", new[] { "大きさ", "ほど", "も", "ある" }];
        yield return ["しかいない", new[] { "しか", "いない" }];
        yield return ["掴めていない", new[] { "掴めていない" }];
        yield return ["振り回されたいな", new[] { "振り回されたい", "な" }];
        yield return ["さぼっている", new[] { "さぼっている" }];
        yield return ["のままで来る", new[] { "の", "まま", "で", "来る" }];
        yield return ["彼はどなりすぎて声をからした", new[] { "彼", "は", "どなりすぎて", "声", "を", "からした" }];
        yield return ["そうしたいからしただけだ", new[] { "そうしたい", "から", "した", "だけ", "だ" }];
        yield return ["推し続けている", new[] { "推し", "続けている" }];
        yield return ["少し直せたら", new[] { "少し", "直せたら" }];
        yield return ["良いほう", new[] { "良い", "ほう" }];
        yield return ["いいえ", new[] { "いいえ" }];
        yield return ["割り当てられた", new[] { "割り当てられた" }];
        yield return ["綺麗だけど近よりがたいよね", new[] { "綺麗", "だけど", "近より", "がたい", "よね" }];
        yield return ["そうなんじゃない", new[] { "そう", "なん", "じゃない" }];
        yield return ["なんというかすみません", new[] { "なんというか", "すみません" }];
        // TODO: めんどくそがる (colloquial めんどくさがる) not in JMDict
        // yield return ["めんどくそがる", new[] { "めんどくそがる" }];
        yield return ["がなんで終わった", new[] { "が", "なんで", "終わった" }];
        yield return
        [
            "てか最近ファン層は円盤すら買わないからそいつらから金とるってのは無謀",
            new[] { "てか", "最近", "ファン層", "は", "円盤", "すら", "買わない", "から", "そいつら", "から", "金", "とる", "ってのは", "無謀" }
        ];
        yield return ["とろいな", new[] { "とろい", "な" }];
        yield return ["なんでもかんでも", new[] { "なんでもかんでも" }];
        yield return ["しないかい", new[] { "しない", "かい" }];
        // yield return
        // [
        //     "参拝しちゃいかんという人がいます",
        //     new[] { "参拝しちゃ", "いかん", "という", "人", "が", "います" }
        // ];
        // yield return ["人をひやかしちゃいやよ", new[] { "人", "を", "ひやかしちゃ", "いや", "よ" }];
        yield return ["しちゃいたい", new[] { "しちゃいたい" }];
        yield return ["けがなどをしないように", new[] { "けが", "など", "を", "しない", "ように" }];
        // 買い支える is not in JMDict, so it splits to noun 買い支え + たい
        yield return ["買い支えたいと思う", new[] { "買い支え", "たい", "と", "思う" }];
        yield return ["おじゃましています", new[] { "おじゃましています" }];
        yield return ["とかいらんから", new[] { "とか", "いらん", "から" }];
        yield return ["ということだろうけど", new[] { "という", "こと", "だろう", "けど" }];
        yield return ["のはわからなくもない", new[] { "の", "は", "わからなく", "も", "ない" }];
        yield return ["変わっていくだろう", new[] { "変わっていく", "だろう" }];
        yield return ["見せなきゃいけなくなって", new[] { "見せなきゃ", "いけなく", "なって" }];
        yield return ["私じゃなくなるような瞬間があって", new[] { "私", "じゃ", "なくなる", "ような", "瞬間", "が", "あって" }];
        yield return ["効いててかなりぬくい", new[] { "効いてて", "かなり", "ぬくい" }];
        yield return ["撮影してていつもは", new[] { "撮影してて", "いつも", "は" }];
        yield return ["むしろいないほうが珍しい", new[] { "むしろ", "いない", "ほう", "が", "珍しい" }];
        yield return ["旅行にいきたい", new[] { "旅行", "に", "いきたい" }];
        yield return ["見ててこんな話あったっけ", new[] { "見てて", "こんな", "話", "あった", "っけ" }];
        yield return ["いじめとかある", new[] { "いじめ", "とか", "ある" }];
        yield return ["となったらしい", new[] { "となった", "らしい" }];
        yield return ["基地外が必死過ぎ", new[] { "基地外", "が", "必死", "過ぎ" }];
        yield return ["調整のせいとか", new[] { "調整", "の", "せい", "とか" }];
        yield return ["はっしていない", new[] { "はっしていない" }];
        yield return ["無理さえしなければ", new[] { "無理", "さえ", "しなければ" }];
        yield return ["ところで", new[] { "ところで" }];
        yield return ["外に出て", new[] { "外", "に", "出て" }];
        yield return ["大人しそうな顔", new[] { "大人しそう", "な", "顔" }];
        yield return ["おとなしそうなようすにだまされた", new[] { "おとなしそう", "な", "ようす", "に", "だまされた" }];
        yield return ["勝手に入る", new[] { "勝手に", "入る" }];
        yield return ["後継ぎする", new[] { "後継ぎ", "する" }];
        yield return ["なすまん", new[] { "な", "すまん" }];
        yield return ["強いんだね", new[] { "強い", "んだ", "ね" }];
        yield return ["次がある", new[] { "次", "が", "ある" }];
        yield return ["のせいですね", new[] { "の", "せい", "です", "ね" }];
        yield return ["それただの怪しい人ですし", new[] { "それ", "ただ", "の", "怪しい", "人", "です", "し" }];
        yield return ["ごときが知る", new[] { "ごとき", "が", "知る" }];
        yield return ["山にはさまれて", new[] { "山", "に", "はさまれて" }];
        // TODO: Too specific - Sudachi misparsing とかすんで as とか + すんで
        // yield return ["物がぼんやりとかすんで見える", new[] { "物", "が", "ぼんやり", "と", "かすんで", "見える" }];
        yield return ["どなた様でございましょうか", new[] { "どなた", "様", "でございましょう", "か" }];
        yield return ["読んでくださりありがとうございました", new[] { "読んでくださり", "ありがとうございました" }];
        yield return ["ふざけんな", new[] { "ふざけんな" }];
        yield return ["観終わってた", new[] { "観", "終わってた" }];
        yield return ["意味深終わり", new[] { "意味深", "終わり" }];
        yield return ["今日とて居残りです", new[] { "今日", "とて", "居残り", "です" }];
        yield return ["堪能させていただきます", new[] { "堪能させて", "いただきます" }];
        yield return ["わからんからそう思った", new[] { "わからん", "から", "そう", "思った" }];
        yield return ["うちからそうなっても", new[] { "うち", "から", "そう", "なって", "も" }];
        yield return ["上映会やな", new[] { "上映", "会", "や", "な" }];
        yield return ["以上書いてください", new[] { "以上", "書いてください" }];
        yield return ["してしまったのがいまだに忘れられないし", new[] { "してしまった", "の", "が", "いまだに", "忘れられない", "し" }];
        yield return ["彼ははんぱじゃなく", new[] { "彼", "は", "はんぱじゃなく" }];
        yield return ["許さないじゃなくてさ", new[] { "許さない", "じゃなくて", "さ" }];
        yield return ["じゃなかったです", new[] { "じゃなかった", "です" }];
        yield return ["彼女は苦しげにうめいて横たわった", new[] { "彼女", "は", "苦しげ", "に", "うめいて", "横たわった" }];
        yield return ["こんな幼げな少女", new[] { "こんな", "幼げ", "な", "少女" }];
        yield return ["寂しげな表情", new[] { "寂しげな", "表情" }];
        yield return ["嬉しげに笑う", new[] { "嬉しげ", "に", "笑う" }];
        yield return ["嬉しさで胸が熱くなる", new[] { "嬉しさ", "で", "胸が熱くなる" }];
        yield return ["わたしにはちょっとわかりかねますので", new[] { "わたし", "には", "ちょっと", "わかりかねます", "ので" }];
        yield return ["腕をつかまれて路地", new[] { "腕", "を", "つかまれて", "路地" }];
        yield return ["別にマイナスにならん", new[] { "別に", "マイナス", "に", "ならん" }];
        yield return ["遊びばかりはだめだよ", new[] { "遊び", "ばかり", "は", "だめ", "だ", "よ" }];
        yield return ["最中でも", new[] { "最中", "でも" }];
        yield return ["小動物好き物好き", new[] { "小動物", "好き", "物好き" }];
        yield return ["知れないですか", new[] { "知れない", "ですか" }];
        yield return ["かも知れないですね", new[] { "かも知れない", "です", "ね" }];
        yield return ["匙ですくう", new[] { "匙", "で", "すくう" }];
        yield return ["デカかったクドくない", new[] { "デカかった", "クドくない" }];
        yield return ["決めたらしい教われたらしい", new[] { "決めた", "らしい", "教われた", "らしい" }];
        yield return ["臆病なくせにとてもよい仲間だった", new[] { "臆病な", "くせに", "とても", "よい", "仲間", "だった" }];
        yield return ["あのねあのさ", new[] { "あのね", "あのさ" }];
        yield return ["これまでになかったような名優", new[] { "これまで", "に", "なかった", "ような", "名優" }];
        yield return ["確かめてちゃんと", new[] { "確かめて", "ちゃんと" }];
        yield return ["ことにしましょうってなった", new[] { "ことにしましょう", "って", "なった" }];
        yield return ["見てござる", new[] { "見て", "ござる" }];
        yield return ["彼がいうことはわけがわからない", new[] { "彼", "が", "いう", "こと", "は", "わけがわからない" }];
        yield return ["わけのわからないことをくどくど言う", new[] { "わけのわからない", "こと", "を", "くどくど", "言う" }];
        yield return ["ごくまれに", new[] { "ごくまれ", "に" }];
        yield return ["癒やされたかった", new[] { "癒やされたかった" }];
        yield return ["７時には帰ってきなさい", new[] { "７時", "には", "帰ってきなさい" }];
        yield return ["人はいますか", new[] { "人", "は", "います", "か" }];
        yield return ["トマトづくし", new[] { "トマト", "づくし" }];
        yield return ["見えざる関係性", new[] { "見えざる", "関係性" }];
        yield return ["だめだったら", new[] { "だめ", "だったら" }];
        yield return ["万事不都合の無いようにはからってくれ", new[] { "万事", "不都合", "の", "無い", "ように", "はからってくれ" }];
        yield return ["ではみなさん", new[] { "では", "みなさん" }];
        // TODO: Too specific - Sudachi misparsing とはがね as とは + が + ね
        // yield return ["鉄とはがね", new[] { "鉄", "と", "はがね" }];
        yield return ["抹茶とは", new[] { "抹茶", "とは" }];
        yield return ["当たりとはずれ", new[] { "当たり", "と", "はずれ" }];
        yield return ["工夫がされる", new[] { "工夫", "が", "される" }];
        yield return ["うまいことしたね", new[] { "うまいこと", "した", "ね" }];
        yield return
        [
// １４ is OOV, not in JMDict - only 人 counter is matched
            "ことしは新成人１４人のうち８人が避難先などから村の村民会館に集まりました",
            new[] { "ことし", "は", "新成人", "人", "の", "うち", "８人", "が", "避難先", "など", "から", "村", "の", "村民", "会館", "に", "集まりました" }
        ];
        yield return ["鬱が悪化する", new[] { "鬱", "が", "悪化する" }];
        yield return ["一部が手に入ればことし１年の願いがかなうとされています", new[] { "一部", "が", "手に入れば", "ことし", "１年", "の", "願い", "が", "かなう", "とされています" }];
        yield return ["汗を流しました", new[] { "汗を流しました" }];
        yield return ["気がついてる", new[] { "気がついてる" }];
        yield return ["ガスがついている", new[] { "ガス", "が", "ついている" }];
        yield return ["再開通", new[] { "再", "開通" }];
        yield return ["謝罪はあったにせよ", new[] { "謝罪", "は", "あった", "にせよ" }];
        yield return ["うそではないにしろ", new[] { "うそ", "ではない", "にしろ" }];
        yield return ["再生しているのではないかという兆候もある", new[] { "再生している", "の", "ではないか", "という", "兆候", "も", "ある" }];
        yield return ["会長になるのではないかという呼び声も高い", new[] { "会長", "に", "なる", "の", "ではないか", "という", "呼び声", "も", "高い" }];
        yield return ["普段着てる服", new[] { "普段", "着てる", "服" }];
        yield return ["エレガントなお洋服", new[] { "エレガントな", "お", "洋服" }];
        yield return ["老いてなお元気なこと", new[] { "老いて", "なお", "元気な", "こと" }];
        yield return ["何も口にせぬ", new[] { "何も", "口にせぬ" }];
        yield return ["切ねぇ", new[] { "切ねぇ" }];
        yield return ["何故人気がある", new[] { "何故", "人気がある" }];
        yield return ["バラしちゃってる", new[] { "バラしちゃってる" }];
        yield return ["気を使わせている", new[] { "気を使わせている" }];
        // TODO: 一段上 is a valid expression, needs better context-aware handling
        // yield return ["一段上がる", new[] { "一段", "上がる" }];
        yield return ["一段落ちる", new[] { "一段", "落ちる" }];
        yield return ["恐怖ですくむ", new[] { "恐怖", "で", "すくむ" }];
        yield return ["全員がたちすくみました", new[] { "全員", "が", "たちすくみました" }];
        yield return ["雪がないため", new[] { "雪", "が", "ない", "ため" }];
        yield return ["雪がなく", new[] { "雪", "が", "なく" }];
        yield return ["零れ落ちてる", new[] { "零れ落ちてる" }];
        yield return ["使い物にならんだろ", new[] { "使い物", "に", "ならん", "だろ" }];
        yield return ["私とならんで走った", new[] { "私", "と", "ならんで", "走った" }];
        yield return ["のうえに", new[] { "の", "うえ", "に" }];
        yield return ["皇位についたが", new[] { "皇位", "に", "ついた", "が" }];
        yield return ["疱瘡がついたか", new[] { "疱瘡", "が", "ついた", "か" }];
        yield return ["折りたたみ式ついたて", new[] { "折りたたみ式", "ついたて" }];
        yield return ["いろいろな部分をもんだりこすったりすること", new[] { "いろいろな", "部分", "を", "もんだり", "こすったり", "する", "こと" }];
        yield return ["たまにはいいもんだよ", new[] { "たまに", "は", "いい", "もん", "だ", "よ" }];
        yield return ["歩みをはやめるのだった", new[] { "歩み", "を", "はやめる", "の", "だった" }];
        yield return ["たばこはやめると誓います", new[] { "たばこ", "は", "やめる", "と", "誓います" }];
        yield return
        [
            "私個人の生活についてとやかくうるさくいうのはやめてください",
            new[] { "私", "個人", "の", "生活", "について", "とやかく", "うるさく", "いう", "の", "は", "やめてください" }
        ];
        yield return ["こもりがちな人", new[] { "こもり", "がちな", "人" }];
        yield return ["長くはかからないでしょう", new[] { "長く", "は", "かからない", "でしょう" }];
        yield return ["人はいないでしょうね", new[] { "人", "は", "いない", "でしょう", "ね" }];
        yield return ["人はいないですね", new[] { "人", "は", "いない", "です", "ね" }];
        yield return ["猛者どもの集い", new[] { "猛者", "ども", "の", "集い" }];
        yield return ["うまいかまずいか", new[] { "うまい", "か", "まずい", "か" }];
        yield return ["守衛にとがめられた", new[] { "守衛", "に", "とがめられた" }];
        yield return ["問い合わせがたくさん", new[] { "問い合わせ", "が", "たくさん" }];
        yield return ["楽しみがたくさん", new[] { "楽しみ", "が", "たくさん" }];
        yield return ["ふくろうは", new[] { "ふくろう", "は" }];
        yield return ["筋をもんでくれ", new[] { "筋", "を", "もんでくれ" }];
        yield return ["いわきからさいたままで", new[] { "いわき", "から", "さいたま", "まで" }];
        yield return ["新型コロナウイルス", new[] { "新型コロナウイルス" }];
        yield return ["新型コロナウィルス", new[] { "新型コロナウィルス" }];
        yield return ["映画を見るとか食事をするとか", new[] { "映画", "を", "見る", "とか", "食事", "を", "する", "とか" }];
        yield return ["さもうれしそうに笑う", new[] { "さも", "うれしそう", "に", "笑う" }];
        yield return ["出しなに客が来る", new[] { "出しな", "に", "客", "が", "来る" }];
        yield return ["出しながら飛んで", new[] { "出しながら", "飛んで" }];
        yield return ["正直言いたい", new[] { "正直", "言いたい" }];
        yield return ["おとめにふさわしい振る舞い", new[] { "おとめ", "に", "ふさわしい", "振る舞い" }];
        yield return ["気がないのよ", new[] { "気がない", "の", "よ" }];
        yield return ["口論のあげくに殴り合いになった", new[] { "口論", "の", "あげく", "に", "殴り合い", "に", "なった" }];
        yield return ["お手数おかけします", new[] { "お手数", "おかけします" }];
        yield return ["３０分後におかけ直しください", new[] { "３０分", "後", "に", "お", "かけ直し", "ください" }];
        yield return ["わかりきった", new[] { "わかりきった" }];
        yield return ["最良の方法は何だと思いますか", new[] { "最良", "の", "方法", "は", "何", "だ", "と", "思います", "か" }];
        yield return ["どうせいやがらせでする", new[] { "どうせ", "いやがらせ", "で", "する" }];
        yield return ["芝居もどきのせりふを言う", new[] { "芝居", "もどき", "の", "せりふ", "を", "言う" }];
        yield return ["がんもどきという食品", new[] { "がんもどき", "という", "食品" }];
        yield return ["落ちこぼれている", new[] { "落ちこぼれている" }];
        yield return ["忙しくてろくに更新もできず", new[] { "忙しくて", "ろくに", "更新", "も", "できず" }];
        yield return ["だまってろって", new[] { "だまってろ", "って" }];
        yield return ["しっぽく蕎麦", new[] { "しっぽく", "蕎麦" }];
        // TODO: Too specific - Sudachi misparsing はしっぽ as 端っぽ (edge/tip)
        // yield return ["猫はしっぽをぴんと立てて歩いた", new[] { "猫", "は", "しっぽ", "を", "ぴんと", "立てて", "歩いた" }];
        yield return ["やる気はない", new[] { "やる気", "は", "ない" }];
        yield return ["あけましておめでとうございます", new[] { "あけましておめでとうございます" }];
        yield return ["おれたちは行くのにおまえたちは行かぬ", new[] { "おれたち", "は", "行く", "のに", "おまえたち", "は", "行かぬ" }];
        yield return ["よろしくおねがいします", new[] { "よろしくおねがいします" }];
        yield return ["気を遣ってくれてるのかと思ってました", new[] { "気を遣ってくれてる", "の", "か", "と", "思ってました" }];
        yield return ["太陽をかたどったしるし", new[] { "太陽", "を", "かたどった", "しるし" }];
        yield return ["間違えていらっしゃるのかしら", new[] { "間違えて", "いらっしゃる", "の", "かしら" }];
        yield return ["ヤツはいそうにないな", new[] { "ヤツ", "は", "いそう", "に", "ない", "な" }];
        yield return ["確認をとっています", new[] { "確認", "を", "とっています" }];
        // １０万人 splits as １０万 + 人 (not in JMDict as compound); 存在せず combines
        yield return
        [
            "人口１０万人以上の都市の中で唯一旅客を扱う鉄道駅が存在せず",
            new[] { "人口", "１０万", "人", "以上", "の", "都市", "の", "中", "で", "唯一", "旅客", "を", "扱う", "鉄道駅", "が", "存在せず" }
        ];
        yield return ["だしはおいしい", new[] { "だし", "は", "おいしい" }];
        yield return ["だして", new[] { "だして" }];
        yield return ["だしといて", new[] { "だしといて" }];
        yield return ["見てあの平民の娘泣きだしましたわ", new[] { "見て", "あの", "平民", "の", "娘", "泣きだしました", "わ" }];
        yield return ["割り切れたら", new[] { "割り切れたら" }];
        yield return ["あり得なかったり", new[] { "あり得なかったり" }];
        yield return ["代わり映え", new[] { "代わり映え" }];
        yield return ["器用なのですぐ上達しますよ", new[] { "器用", "なので", "すぐ", "上達します", "よ" }];
        yield return ["おにいちゃん", new[] { "おにいちゃん" }];
        yield return ["動画につまってる", new[] { "動画", "に", "つまってる" }];
        yield return ["出来そう", new[] { "出来そう" }];
        yield return ["その上着貸してください", new[] { "その", "上着", "貸してください" }];
        yield return ["幸多き", new[] { "幸", "多き" }];
        yield return ["きっと気に入っていつかまた来てくれるよ", new[] { "きっと", "気に入って", "いつか", "また", "来てくれる", "よ" }];
        yield return ["私がいそうな場所知ってたんだから", new[] { "私", "が", "いそう", "な", "場所", "知ってた", "んだ", "から" }];
        yield return ["うまくハメられた", new[] { "うまく", "ハメられた" }];
        yield return ["してるとこだから", new[] { "してる", "とこ", "だから" }];
        yield return ["下記のとおりです", new[] { "下記", "の", "とおり", "です" }];
        yield return ["すぐに終わってしまった", new[] { "すぐに", "終わってしまった" }];
        yield return ["間違いない", new[] { "間違いない" }];
        yield return ["として", new[] { "として" }];
        yield return ["自分でも信じられないような気分だった", new[] { "自分でも", "信じられない", "ような", "気分", "だった" }];
        yield return ["必要な", new[] { "必要な" }];
        yield return ["大切な", new[] { "大切な" }];
        yield return ["飽き始める", new[] { "飽き", "始める" }];
        yield return ["聞き回っていた", new[] { "聞き", "回っていた" }];
        yield return ["教えてあげましょう", new[] { "教えてあげましょう" }];
        yield return ["でもなければ難しいだろう無ければ飽きを自覚しにくい", new[] { "でもなければ", "難しい", "だろう", "無ければ", "飽き", "を", "自覚", "しにくい" }];
        yield return ["引っ張り上げて貰って", new[] { "引っ張り上げて貰って" }];
        yield return ["信じて貰えなかった", new[] { "信じて貰えなかった" }];
        // Classical ワ行 て-form (連用形-一般 + て) must not merge with preceding verb
        yield return ["繕って貰いて", new[] { "繕って", "貰いて" }];
        yield return ["生きて行けばいい", new[] { "生きて行けば", "いい" }];
        yield return ["持てそうだ", new[] { "持てそう", "だ" }];
        yield return ["引けなくなってしまって", new[] { "引けなく", "なってしまって" }];
        yield return ["ぶつけるべき", new[] { "ぶつける", "べき" }];
        yield return ["助けてもらえる", new[] { "助けてもらえる" }];
        yield return ["近づいて来ている", new[] { "近づいて来ている" }];
        yield return ["教えてくれるだろうけれど", new[] { "教えてくれる", "だろう", "けれど" }];
        yield return ["通用しない果てしない遠慮しない", new[] { "通用", "しない", "果てしない", "遠慮", "しない" }];
        yield return ["痛み出したり", new[] { "痛み", "出したり" }];
        yield return ["急にこれを食べさせられちゃったって言われてもちょっと困るなあ", new[] { "急に", "これ", "を", "食べさせられちゃった", "って", "言われて", "も", "ちょっと", "困る", "なあ" }];
        // 指弾 is a vs (suru-verb) so 指弾する is correctly combined
        yield return ["俺は奴の民主主義ぶった欺瞞を指弾する", new[] { "俺", "は", "奴", "の", "民主主義", "ぶった", "欺瞞", "を", "指弾する" }];
        yield return ["俺はどこか背徳的な昂揚感", new[] { "俺", "は", "どこか", "背徳的な", "昂揚感" }];
        // め derogatory suffix is filtered — only the noun remains
        yield return ["欠陥品め", new[] { "欠陥品" }];
        // め kept as part of an adjectival compound (大きめ = "on the large side")
        yield return ["大きめの箱を買った", new[] { "大きめ", "の", "箱", "を", "買った" }];
        yield return ["小さめの服", new[] { "小さめ", "の", "服" }];
        // 奴め recognized as a single compound entry in JMDict (not split)
        yield return ["この奴め", new[] { "この", "奴め" }];
        yield return ["本人たちは面白いと思ったのかもしれない", new[] { "本人", "たち", "は", "面白い", "と", "思った", "の", "かもしれない" }];
        yield return ["わかりかねさせられない", new[] { "わかりかねさせられない" }];
        yield return ["読み切れなかった", new[] { "読み切れなかった" }];
        yield return ["話し合っている", new[] { "話し合っている" }];
        // ん negative contraction tests
        yield return ["知らんだ", new[] { "知らん", "だ" }];
        yield return ["わからんよ", new[] { "わからん", "よ" }];
        yield return ["言わんでくれ", new[] { "言わんでくれ" }];
        // Past tense んだ tests (む/ぬ/ぶ/ぐ verbs)
        yield return ["睨んだが", new[] { "睨んだ", "が" }];
        yield return ["読んだけど", new[] { "読んだ", "けど" }];
        yield return ["飲んだから", new[] { "飲んだ", "から" }];
        yield return ["遊んだし", new[] { "遊んだ", "し" }];
        yield return ["客を待ってるんだけど", new[] { "客", "を", "待ってる", "んだ", "けど" }];
        yield return ["学生さんだって", new[] { "学生", "さん", "だって" }];
        yield return ["ちょっと休憩ーなんて言って", new[] { "ちょっと", "休憩", "なんて", "言って" }];
        yield return ["なんて女だって思われる", new[] { "なんて", "女", "だって", "思われる" }];
        yield return ["絶対に戻らなきゃいけない", new[] { "絶対", "に", "戻らなきゃ", "いけない" }];
        yield return ["とてもいい品が買えました", new[] { "とても", "いい", "品", "が", "買えました" }];
        // JMDict compound noun tests (Mode B refactor)
        // Single JMDict entries - should remain as one token
        yield return ["人種差別", new[] { "人種差別" }];
        yield return ["総合病院", new[] { "総合病院" }];
        yield return ["ソビエト連邦", new[] { "ソビエト連邦" }];
        yield return ["胚性幹細胞", new[] { "胚性幹細胞" }];
        yield return ["国際連合", new[] { "国際連合" }];
        yield return ["高等学校", new[] { "高等学校" }];
        yield return ["原子力発電所", new[] { "原子力発電所" }];
        yield return ["環境問題", new[] { "環境問題" }];
        // Long compound chains - should split into JMDict-valid components
        yield return ["人種差別撤廃宣言", new[] { "人種差別", "撤廃", "宣言" }];
        yield return ["ソビエト連邦人民代議員大会", new[] { "ソビエト連邦", "人民", "代議員", "大会" }];
        yield return ["西横浜国際総合病院", new[] { "西横浜", "国際", "総合病院" }];
        // Proper nouns with particles - should not over-combine across particles
        yield return ["人道の港", new[] { "人道", "の", "港" }];
        // JMnedict celebrity names - keep as single token (in JMnedict)
        yield return ["加藤紀子", new[] { "加藤紀子" }];
        yield return ["わかってねえじゃねえか", new[] { "わかってねえ", "じゃねえ", "か" }];
        // Colloquial ねえ negative after te/de-form — Sudachi splits ねえ into ね+え or tags as noun (姉)
        yield return ["入ってねえのに", new[] { "入ってねえ", "のに" }];
        yield return ["飲んでねえのに", new[] { "飲んでねえ", "のに" }];
        yield return ["入ってねえだろ", new[] { "入ってねえ", "だろ" }];
        yield return ["知ってねえの", new[] { "知ってねえ", "の" }];
        // Slang negatives where verb stem ends in え — ええ user_dic entry must not absorb the え
        yield return ["食えねえんだよ", new[] { "食えねえ", "んだ", "よ" }];
        yield return ["見えねえよ", new[] { "見えねえ", "よ" }];
        yield return ["やれねえじゃん", new[] { "やれねえ", "じゃん" }];
        // ええ as standalone Kansai interjection before a slang negative verb
        yield return ["ええ、知らねえよ", new[] { "ええ", "知らねえ", "よ" }];
        yield return ["なんにもしたくないときもある", new[] { "なんにも", "したくない", "とき", "も", "ある" }];
        yield return ["いやあんま外出ないから", new[] { "いや", "あんま", "外", "出ない", "から" }];
        yield return ["外出ない", new[] { "外", "出ない" }];
        yield return ["家出なかった", new[] { "家", "出なかった" }];
        yield return ["普通は驚いたり恐がったり無視したりするものなのに", new[] { "普通", "は", "驚いたり", "恐がったり", "無視したり", "する", "もの", "なのに" }];
        // Vowel elongation tests - verb + う elongation
        // Pattern 1: Token ending in るう misparsed as adjective ウ音便 (e.g., かるう → 軽い)
        yield return ["ぶつかるう", new[] { "ぶつかる" }];  // う is gated (meaningless elongation)
        yield return ["とまるう", new[] { "とまる" }];
        // Pattern 2: Standalone るう token misparsed as name
        yield return ["わかるう", new[] { "わかる" }];
        yield return ["やるう", new[] { "やる" }];
        yield return ["あたるう", new[] { "あたる" }];
        yield return ["はしるう", new[] { "はしる" }];
        // Vowel elongation tests - verb past tense + あ elongation
        // Pattern 3: Token + たあ misparsed as particle と
        yield return ["おきたあ", new[] { "おきた", "あ" }];  // おき + たあ → おきた + あ (past of 起きる)
        yield return ["でたあ", new[] { "でた", "あ" }];  // で + たあ → でた + あ (past of 出る)
        yield return ["ねたあ", new[] { "ねた", "あ" }];  // ね + たあ → ねた + あ (past of 寝る)
        // Pattern 4: Token ending in た + ああ where token is misparsed as non-verb
        yield return ["いきたああ", new[] { "いきた" }];  // ああ is gated (archaic kanji 嗚呼, meaningless elongation)
        // Vowel elongation tests - verb + ー (long vowel mark)
        // Pattern 5: Verb + separate ー token (handled by RepairLongVowelTokens in Parser)
        yield return ["ぶつかるー", new[] { "ぶつかるー" }];
        yield return ["わかるー", new[] { "わかるー" }];
        // Emphatic っ tests - sokuon at clause boundaries causing misparses
        // っ is filtered as SupplementarySymbol, so it won't appear in output
        yield return ["止まらないっ", new[] { "止まらない" }];  // Sudachi misparsed as 止まら + な + いっ (行く)
        yield return ["これでどうですかっ", new[] { "これ", "で", "どう", "ですか" }];  // Sudachi misparsed で + すかっ
        yield return ["だめっ", new[] { "だめ" }];  // Simple case - っ should be separated
        yield return ["行くっ", new[] { "行く" }];  // Verb + emphatic っ
        yield return ["止まらないっ！", new[] { "止まらない" }];  // With punctuation
        yield return ["だめっ、それは違う", new[] { "だめ", "それ", "は", "違う" }];  // Mid-sentence emphatic っ
        yield return ["理解っ、しましたぁっ！", new[] { "理解", "しましたぁ" }];  // Kanji + emphatic っ — 理解 (rikai) not 理解る (wakaru)
        yield return ["ごめんなさいっごめんなさいっ次は我慢する", new[] { "ごめんなさい", "ごめんなさい", "次", "は", "我慢する" }];  // Emphatic っ before voiced kana (ご) and kanji (次)
        yield return ["するからっ", new[] { "する", "から" }];  // Emphatic っ at EOS after particle
        yield return ["我慢っ、する", new[] { "我慢", "する" }];  // Emphatic っ before punctuation (existing behavior)
        yield return ["しょうがないな", new[] { "しょうがない", "な" }];
        yield return ["この手紙を書いた", new[] { "この", "手紙", "を", "書いた" }];
        // Emphatic ぶっち → ぶち normalisation (colloquial gemination)
        yield return ["ぶっち切れてる", new[] { "ぶち切れてる" }];  // ぶっち切れる → ぶち切れる (to become enraged)
        // 少女の手 misparse fix - Sudachi was parsing as 少 (prefix) + 女の手 (expression)
        yield return ["少女の手によって", new[] { "少女", "の", "手", "によって" }];
        yield return ["各党幹部による応援演説", new[] { "各", "党幹部", "による", "応援演説" }];
        // 手を抜く compound expression - Sudachi classifies 手 as suffix, but should match exp entry
        yield return ["手を抜いているんですか", new[] { "手を抜いている", "んです", "か" }];
        yield return ["水魔法", new[] { "水", "魔法" }];
        // ておく (te-form + おく subsidiary verb) should combine, not match おいた (mischief)
        yield return ["それはまだ秘密にしておいたほうが", new[] { "それ", "は", "まだ", "秘密", "に", "しておいた", "ほう", "が" }];
        yield return ["姉さんの所にちゃんと届けておいたから", new[] { "姉さん", "の", "所", "に", "ちゃんと", "届けておいた", "から" }];
        yield return ["いっぱいおいたしてるもの", new[] { "いっぱい", "おいたしてる", "もの" }];
        yield return ["全てをやる", new[] { "全て", "を", "やる" }];
        // おい (interjection) + まだ (adverb) - user_dic low-cost entries prevent お+いまだ misparse
        yield return ["おいまだかよ", new[] { "おい", "まだ", "か", "よ" }];
        // てやれ (imperative of auxiliary やる) - Sudachi tags やれ as interjection, should combine with て-form
        yield return ["なら逃がしてやれ監禁する理由などないのだから", new[] { "なら", "逃がしてやれ", "監禁する", "理由", "など", "ない", "の", "だから" }];
        yield return ["続きがある", new[] { "続き", "が", "ある" }];
        // Long vowel mark (ー) tests
        // Trailing ー after hiragana/kanji: lattice-level lookup finds the word without ー,
        // original surface preserved (ー included in output)
        yield return ["あなたー", new[] { "あなたー" }];
        yield return ["おまえー", new[] { "おまえー" }];
        yield return ["わたしー", new[] { "わたしー" }];
        yield return ["ばかー", new[] { "ばかー" }];
        yield return ["うそー", new[] { "うそー" }];
        yield return ["すごいー", new[] { "すごいー" }];
        // Multi-word sentence: あなたー must not merge with following words (no たーそこ tokens)
        yield return ["あなたーそこにいるの", new[] { "あなたー", "そこ", "に", "いる", "の" }];
        // Must not regress: these are valid JMDict entries with ー — ー is part of the word
        yield return ["すげー", new[] { "すげー" }];
        yield return ["やべー", new[] { "やべー" }];
        yield return ["うるせー", new[] { "うるせー" }];
        yield return ["かわいー", new[] { "かわいー" }];
        yield return ["コーヒー", new[] { "コーヒー" }];
        // Bar run normalisation: multiple ー collapse to single ー, surface preserved
        yield return ["あなたーー", new[] { "あなたー" }];
        // Kanji + ー: lattice finds 休憩 without ー, surface preserved
        yield return ["休憩ー", new[] { "休憩ー" }];
        yield return ["この辺りで物々交換しておかないとな", new[] { "この辺り","で","物々交換","しておかない","と","な" }];
        yield return ["その案件について", new[] { "その","案件","について" }];
        // 板につく (idiomatic compound) — に+つい+て should NOT merge into particle について
        yield return ["板について", new[] { "板について" }];
        yield return ["喜んだだろうね", new[] { "喜んだ","だろう","ね" }];
        // Comma (、) is kept inline for Sudachi context — そこまで splits into そこ+まで in broader context
        yield return ["そこまで、それは違う", new[] { "そこ","まで","それ","は","違う" }];
        // ホント + katakana noun: Sudachi merges adjacent katakana, PreprocessText splits them
        yield return ["ホントバカだな", new[] { "ホント","バカ","だ","な" }];
        yield return ["ホントダメだな", new[] { "ホント","ダメ","だ","な" }];
        // Conjugated compound expressions via CombineCompounds
        yield return ["らちが明かん", new[] { "らちが明かん" }];
        yield return ["ことなきを得た", new[] { "ことなきを得た" }];
        yield return ["恩着せがましくて", new[] { "恩着せがましく", "て" }];
        // なん + だろ should not be greedily merged into なんだろ after na-adjective
        yield return ["おんなじなんだろ", new[] { "おんなじ", "なん", "だろ" }];
        // Tilde as vowel elongation — adj-i stems resolved to dictionary form
        yield return ["ヤバ～", new[] { "ヤバい" }];  // ～ (fullwidth tilde) normalised to ー, then adj-i stem resolved
        yield return ["スゴ〜", new[] { "スゴい" }];  // 〜 (wave dash) same treatment
        yield return ["ヤバー", new[] { "ヤバい" }];  // Direct ー also resolved for short katakana adj-i stems
        // Non-adj tilde — just dropped as emphasis, stem matched normally
        yield return ["バカ～", new[] { "バカ" }];
        // て particle + んだ should NOT merge into てんだ (転舵) — ん is contracted いる
        yield return ["怒られてんだよ", new[] { "怒られて", "んだ", "よ" }];
        // はやる split: は (topic particle) + やる, not 流行る — preserved when preceded by が
        yield return ["僕はやることをやるだけだ", new[] { "僕", "は", "やる", "こと", "を", "やる", "だけ", "だ" }];
        // Copula である (past) + かのように (expression "as if")
        yield return ["自分の運命であったかのように", new[] { "自分", "の", "運命", "であった", "かのように" }];
        // Interjection うわっ with katakana ッ should not be split by emphatic っ handling
        yield return ["うわッ人呼んでる", new[] { "うわッ", "人", "呼んでる" }];
        // ておく contraction + と particle: Sudachi misparsing とくと as adverb 篤と
        yield return ["よく見とくといいよ", new[] { "よく", "見とく", "と", "いい", "よ" }];
        yield return ["食べとくといいよ", new[] { "食べとく", "と", "いい", "よ" }];
        // ておく contraction + よう: Sudachi misparsing とくよう as na-adj 徳用
        yield return ["片づけとくように言ってたんだけど", new[] { "片づけ", "とく", "ように言ってた", "んだ", "けど" }];
        // だな misparsed as 棚 (shelf) — should split into だ + な (copula + filler particle)
        yield return ["だな気をつけねーと", new[] { "だ", "な", "気をつけねー", "と" }];
        yield return ["集まってもらったのはだな新生の結成式を執り行うためだ", new[] { "集まってもらった", "の", "は", "だ", "な", "新生", "の", "結成", "式", "を", "執り行う", "ため", "だ" }];
        // 来イ (katakana imperative) normalised to 来い
        yield return ["ダッタラオマエガ来イ", new[] { "ダッタラ", "オマエ", "ガ", "来い" }];
        // N日間 split into N日 + 間 (not numeral + 日間 "daytime")
        yield return ["三日間だけだが", new[] { "三日", "間", "だけ", "だが" }];
        yield return ["何日間もずっと", new[] { "何日", "間", "も", "ずっと" }];
        yield return ["約五日間", new[] { "約", "五日", "間" }];
        yield return ["四日間に", new[] { "四日", "間", "に" }];

        // Vowel elongation ー after る-verbs — Sudachi splits as prefix + る(OOV) + ー
        // RepairVowelElongation merges back into verb and drops ー
        yield return ["手伝って来るー", new[] { "手伝って", "来るー" }];
        yield return ["ダンジョンに潜るー", new[] { "ダンジョン", "に", "潜るー" }];
        yield return ["ここにおるー", new[] { "ここ", "に", "おるー" }];
        yield return ["きっと写るーっ", new[] { "きっと", "写る" }];
        // 何本 split into 何 + 本 (counter) — Sudachi treats as surname ナニモト or single noun ナンボン
        yield return ["なら光これは何本", new[] { "なら", "光", "これ", "は", "何", "本" }];
        yield return ["何本ぐらいにしようかな", new[] { "何", "本", "ぐらい", "に", "しよう", "かな" }];
        yield return ["何本オレンジジュースを飲むかってこと", new[] { "何", "本", "オレンジジュース", "を", "飲む", "か", "って", "こと" }];
        // 玩具店 — suffix 店 combines with 玩具 after user dic reading fix (オモチャ)
        yield return ["玩具店に行った", new[] { "玩具店", "に", "行った" }];
        // でも fusion: copula で(助動詞) + も merged into でも by ProcessSpecialCases
        yield return ["元気でもないし", new[] { "元気", "でもない", "し" }];
        yield return ["からでも尊大な態度", new[] { "から", "でも", "尊大な", "態度" }];
        // でも fusion must NOT merge verb te-form で + も
        yield return ["選んでも一緒よ", new[] { "選んで", "も", "一緒", "よ" }];
        // ごとし (如く) should NOT be merged into preceding verb by CombineAuxiliary
        yield return ["気附かぬ如くゆっくり", new[] { "気附かぬ", "如く", "ゆっくり" }];
        // Standalone ぬ at sentence start is stripped (leading auxiliary without a preceding verb)
        yield return ["ぬ如く", new[] { "如く" }];
        // Adjective stem + ぶり suffix recombined (Sudachi splits 久しぶり after よっ interjection)
        yield return ["よっ久しぶり", new[] { "よっ", "久しぶり" }];
        // おつもり → お (prefix) + つもり (intention), not おつもり (last drink of sake, 2850128)
        yield return ["するおつもりですか", new[] { "する", "お", "つもり", "ですか" }];
        yield return ["庇護するおつもりでいる", new[] { "庇護する", "お", "つもり", "で", "いる" }];
        yield return ["どうするおつもりです", new[] { "どう", "する", "お", "つもり", "です" }];
        // 首落とさねえ — Sudachi greedily matches 首落(ち) as compound noun, breaking 落とさねえ
        yield return ["首落とさねえ", new[] { "首", "落とさねえ" }];
        // Quotative って + explanatory んだ — don't merge adjective + って when followed by んだ
        yield return ["悪いってんだ", new[] { "悪い", "って", "んだ" }];
        yield return ["俺のどこが悪いってんだ", new[] { "俺", "の", "どこ", "が", "悪い", "って", "んだ" }];
        yield return ["どうしろってんだ", new[] { "どう", "しろ", "って", "んだ" }];
        // Trailing 」 causes Sudachi to tokenize んだ as 感動詞, must not merge って+んだ→ってんだ
        yield return ["この森になんの用があるってんだ」", new[] { "この", "森", "に", "なん", "の", "用がある", "って", "んだ" }];
        // やつら should not be split into や + つら
        yield return ["やつらの思うつぼだ", new[] { "やつら", "の", "思うつぼ", "だ" }];
        yield return ["やつらに見つかるな", new[] { "やつら", "に", "見つかる", "な" }];
        // Kana compound nouns — adjective stem + suffix/noun should recombine
        yield return ["あのでかぶつを倒すぞ", new[] { "あの", "でかぶつ", "を", "倒す", "ぞ" }];
        yield return ["あのながものは邪魔だ", new[] { "あの", "ながもの", "は", "邪魔", "だ" }];
        yield return ["やすものを買った", new[] { "やすもの", "を", "買った" }];
        // Compound verb ～ねば (negative conditional) — mixed kanji/hiragana dict form must resolve
        yield return ["選びださねば", new[] { "選びださねば" }];
        // Colloquial っしょ (contraction of でしょう) — split from preceding word
        yield return ["さすがにサボりすぎっしょー", new[] { "さすが", "に", "サボりすぎ", "っしょ" }];
        yield return ["マジっしょ", new[] { "マジ", "っしょ" }];
        // いっしょ (一緒) must NOT be affected by っしょ splitting
        yield return ["いっしょに遊ぼう", new[] { "いっしょに", "遊ぼう" }];
        // Repeated verb phrases — CombineVerbDependant must not merge identical tokens
        yield return ["ししてないしてない", new[] { "してない", "してない" }];
        // RepairOrphanedAuxiliary — Sudachi merges noun+verb into compound noun, orphaning the conjugation
        yield return ["足蹴られた", new[] { "足", "蹴られた" }];  // 足蹴(noun) + られた(aux) → 足 + 蹴られた(passive past)
        yield return ["肉食う", new[] { "肉", "食う" }];  // 肉食(noun) + う(filler) → 肉 + 食う(verb)
        // こと + って must not merge — ことる (Kotor, place name) is not a verb
        yield return ["自分のことって自分では分からない", new[] { "自分", "の", "こと", "って", "自分", "では", "分からない" }];

        // となり with trailing comma — Sudachi splits と+なり before comma; CombineToNaru should re-merge
        yield return ["体験はトラウマとなり、大変だった", new[] { "体験", "は", "トラウマ", "となり", "大変", "だった" }];

        // ごめんなさいね — Sudachi fuses interjection+particle; repair stage should split
        yield return ["ごめんなさいね？", new[] { "ごめんなさい", "ね" }];
        // Multi-char trailing particles fused onto interjections — repair must prefer the longer match
        yield return ["ごめんなさいよね", new[] { "ごめんなさい", "よね" }];
        yield return ["ごめんなさいなあ", new[] { "ごめんなさい", "なあ" }];
        yield return ["ごめんなさいのよ", new[] { "ごめんなさい", "の","よ" }];
        yield return ["ありがとうよね", new[] { "ありがとう", "よね" }];

        // Trailing-particle fallback over-split prevention:
        // から/けれど/のに at clause end must remain single tokens, not split into character pairs.
        yield return ["聞こえてるから", new[] { "聞こえてる", "から" }];
        yield return ["したいけれど", new[] { "したい", "けれど" }];
        yield return ["見えないのに", new[] { "見えない", "のに" }];
        yield return ["できないから", new[] { "できない", "から" }];
        yield return ["そういえばここって", new[] { "そういえば", "ここ", "って" }];
        // Single-kana Symbol tokens (stutters) must be filtered — ゆ before … tagged as 記号 by Sudachi
        yield return ["ゆ……夢は", new[] { "夢", "は" }];
        // チックショー: colloquial geminated form of ちくしょう — Sudachi splits into チック(suffix)+ショー
        // but user_dic overrides to produce a single 感動詞 token
        yield return ["チックショー", new[] { "チックショー" }];
        // I-adjective stem resegmentation: Sudachi fuses adj-i stems in slang compounds
        // ださ → ださ+い in lookups fallback enables the split; きも → きも matches 肝 directly
        yield return ["ダサキモ", new[] { "ダサ", "キモ" }];
        yield return ["百合豚ダサキモ眼鏡", new[] { "百合", "豚", "ダサ", "キモ", "眼鏡" }];
        // として/からして/クセして compound particle combination
        yield return ["護衛として彼女に付き従う", new[] { "護衛", "として", "彼女", "に", "付き従う" }];
        yield return ["憮然として言う", new[] { "憮然", "として", "言う" }];
        yield return ["好みからして", new[] { "好み", "からして" }];
        yield return ["クセして", new[] { "クセして" }];
        // しようとして: Sudachi splits し(conjunction)+ようとして(expression) → must recombine
        yield return ["しようとして", new[] { "しようとして" }];
        // Kansai-ben negative せん: suru-noun + せん should combine
        yield return ["卑下せんでもいい", new[] { "卑下せん", "でも", "いい" }];
        // いかんせん must stay as a single token (如何せん), not split into いかん + せん
        yield return ["いかんせん、距離が離れすぎている", new[] { "いかんせん", "距離", "が", "離れすぎている" }];

        yield return ["ないなかった", new[] { "ない", "なかった" }];
        yield return ["箱庭の外に行き場なんてないなかったんだ", new[] { "箱庭", "の", "外", "に", "行き場", "なんて", "ない", "なかった", "んだ" }];

        // は+ね should separate as topic particle + emphatic particle, not merge into はね (feather)
        yield return ["僕はねこう見えても式部卿なんだよ", new[] { "僕", "は", "ね", "こう見えても", "式部", "卿", "なんだ", "よ" }];
        yield return ["俺はね同じ気持ちなのにそれを伝えないなんて", new[] { "俺", "は", "ね", "同じ", "気持ち", "なのに", "それ", "を", "伝えない", "なんて" }];
        yield return ["私はねここに存在していることが酷く恥ずかしい", new[] { "私", "は", "ね", "ここ", "に", "存在している", "こと", "が", "酷く", "恥ずかしい" }];
        yield return ["南アルプス市", new[] { "南アルプス", "市" }];
        yield return ["よかったなラムシャーリー様までもうすぐだ", new[] { "よかった", "ラム", "シャーリー", "様", "まで", "もうすぐ", "だ" }];
        yield return ["それはたまたまで", new[] { "それ", "は", "たまたま", "で" }];
        yield return ["生年未公開", new[] { "生年", "未公開" }];
        yield return ["ポチろうとした瞬間に鼻血が出てな", new[] { "ポチろう", "と", "した", "瞬間", "に", "鼻血が出て", "な" }];

        // 朝+verb should not combine into 朝見 (ちょうけん) via false compound deconjugation
        yield return ["朝見る", new[] { "朝", "見る" }];
        yield return ["今朝見たときよりも、炎が小さくなっている。", new[] { "今朝", "見た", "とき", "より", "も", "炎", "が", "小さく", "なっている" }];

        // Compound expression with conjugated final verb: お目にかかれる (potential of お目にかかる)
        yield return ["お目にかかれるとは思ってもみませんでした", new[] { "お目にかかれる", "とは", "思って", "も", "みませんでした" }];

        // 少し must not be split into 少 + し after なら (Sudachi user_dic fix)
        yield return ["それにキャンプなら少しは", new[] { "それ", "に", "キャンプ", "なら", "少し", "は" }];

        // Expressive internal ー in hiragana tokens should be stripped (なーい → ない)
        yield return ["じゃなーい", new[] { "じゃない" }];
        yield return ["聞こえなーい", new[] { "聞こえ", "ない" }];
        yield return ["作ってなーい", new[] { "作ってない" }];

        // Trailing ー must be preserved — colloquial forms (すげー, うるせー) are valid words
        yield return ["すげー", new[] { "すげー" }];
        yield return ["うるせー", new[] { "うるせー" }];

        // おーい is a real JMDict entry (interjection), ー must not be stripped
        yield return ["おーい", new[] { "おーい" }];

        // Verb 連用形 + 合い compounds: should stay combined when compound verb exists in JMDict
        yield return ["殺し合い", new[] { "殺し合い" }];
        yield return ["掴み合い", new[] { "掴み合い" }];
        yield return ["じゃれ合い", new[] { "じゃれ合い" }];
        yield return ["引っ張り合い", new[] { "引っ張り合い" }];

        // Expression tokens must not be resegmented — 気がついて is 気がつく in te-form
        yield return ["気がついてしまう", new[] { "気がついて", "しまう" }];

        // もの + たち must not be merged into 物断ち (abstinence)
        yield return ["生き延びたものたちへの糧となる", new[] { "生き延びた", "もの", "たち", "へ", "の", "糧", "となる" }];

        // Verb-like suffix かねる must merge with its conjugations, not stay as orphaned 鐘 (bell)
        yield return ["壊してしまいかねない", new[] { "壊してしまい", "かねない" }];
        yield return ["決めかねている", new[] { "決め", "かねている" }];
        yield return ["耐えかねて", new[] { "耐え", "かねて" }];

        // Colloquial てらん (contraction of ていられ) — repair stage merges らん+ない, deconjugator handles n-slang
        yield return ["やってらんないだろ", new[] { "やってらんない", "だろ" }];
        yield return ["やってらんねえ", new[] { "やってらんねえ" }];
        yield return ["負けてらんねぇぞー", new[] { "負けてらんねぇ", "ぞ" }];
        yield return ["飲んでらんねえ", new[] { "飲んでらんねえ" }];

        // Unknown compound split via resegmentation — single kanji + suffix
        yield return ["春擬き", new[] { "春", "擬き" }];

        // とぼける / ねぼける mis-segmented by Sudachi (と/ね split as particles)
        yield return ["惚とぼけている様子は無かったし", new[] { "惚とぼけている", "様子", "は", "無かった", "し" }];
        yield return ["素でとぼけた行動をとる", new[] { "素で", "とぼけた", "行動をとる" }];
        yield return ["この期に及んでとぼける俺である", new[] { "この期に及んで", "とぼける", "俺", "である" }];
        yield return ["何ねぼけてんのよ", new[] { "何", "ねぼけてん", "の", "よ" }];
        yield return ["何ねぼけた事言ってんだ", new[] { "何", "ねぼけた", "事", "言って", "んだ" }];

        // 第一次/一次/第二次/二次 — ordinal counters should not be split
        yield return ["第一次魔王討伐", new[] { "第一次", "魔王", "討伐" }];
        yield return ["立つ鳥の声一次の日の朝まだき", new[] { "立つ鳥", "の", "声", "一次", "の", "日", "の", "朝まだき" }];
        yield return ["余のせいで第二次神魔大戦まであるんだけどーっ", new[] { "余", "のせいで", "第二次", "神", "魔", "大戦", "まで", "ある", "んだ", "けど" }];
        yield return ["しかも何で二次性徴の部分", new[] { "しかも", "何で", "二次", "性徴", "の", "部分"}];
        yield return ["足元気をつけろ！", new[] { "足元", "気をつけろ" }];

        // Katakana interjection + ッ should not fuse with following word
        yield return ["フンッバカバカしい先に帰るぞ", new[] { "フンッ", "バカバカしい", "先に", "帰る", "ぞ" }];

        // たわけ should split into た+わけ after verb contexts, not match 戯け
        yield return ["術があるったわけではない", new[] { "術", "が", "あるった", "わけではない" }];
        yield return ["どうしてたわけ", new[] { "どうして", "わけ" }];
        yield return ["チクッたわけじゃない", new[] { "チクッ", "わけじゃない" }];

        // Legitimate たわけ (戯け) should be preserved
        yield return ["この大たわけがっ", new[] { "この", "大", "たわけ", "が" }];

        // === Gemination (っ) should not be split from compounds ===
        yield return ["辺りは真っ暗", new[] { "辺り", "は", "真っ暗" }];
        yield return ["手を突っ込んで", new[] { "手", "を", "突っ込んで" }];
        yield return ["炬燵に顔を突っ込んでみたら", new[] { "炬燵", "に", "顔", "を", "突っ込んでみたら" }];
        yield return ["すぐに奥へ引っ込むけど、", new[] { "すぐに", "奥", "へ", "引っ込む", "けど" }];
        yield return ["黒沢は俺や金田の腕を引っ張って寄せてくる。", new[] { "黒沢", "は", "俺", "や", "金田", "の", "腕", "を", "引っ張って", "寄せてくる" }];
        yield return ["話題を引っ張る。", new[] { "話題", "を", "引っ張る" }];
        yield return ["ローブが木の枝に引っ掛かるのにもうんざりとした様子だった。", new[] { "ローブ", "が", "木", "の", "枝", "に", "引っ掛かる", "のに", "も", "うんざり", "と", "した", "様子", "だった" }];
        yield return ["と素っ頓狂な", new[] { "と", "素っ頓狂な" }];
        yield return ["膨れっ面", new[] { "膨れっ面" }];
        yield return ["でぶっ倒れてる", new[] { "で", "ぶっ倒れてる" }];
        yield return ["でぶっ飛ばす", new[] { "で", "ぶっ飛ばす" }];

        // === Long vowel ー at end of sentence — original surface preserved ===
        yield return ["よそってあげるー", new[] { "よそってあげるー" }];
        yield return ["出てくれるー", new[] { "出てくれるー" }];
        yield return ["思われてるー", new[] { "思われてるー" }];

        // === Sudachi mis-segmentation ===
        yield return ["恋ってすごいですねー", new[] { "恋", "って", "すごい", "です", "ね" }];
        yield return ["恋って何色ですか？", new[] { "恋", "って", "何色", "ですか" }];

        // === Colloquial ゆう for いう (normalized, then combined by pipeline) ===
        yield return ["どーゆうこと", new[] { "どーいう", "こと" }];
        yield return ["どうゆうこと", new[] { "どういう", "こと" }];
        yield return ["そーゆうことは", new[] { "そーいう", "こと", "は" }];

        // === Colloquial conjugations ===
        yield return ["出てこん", new[] { "出てこん" }];
        yield return ["入っとらん", new[] { "入っとらん" }];
        yield return ["殺ス", new[] { "殺ス" }];

        // === すっごく/すっごい — original surface preserved via sokuon-skip ===
        yield return ["舐められてるのもすっごく", new[] { "舐められてる", "の", "も", "すっごく" }];
        yield return ["足、すっごい熱いから", new[] { "足", "すっごい", "熱い", "から" }];

        // === Expressions that should be single words ===
        yield return ["ビクともしません", new[] { "ビクともしません" }];
        yield return ["びくともしません", new[] { "びくともしません" }];
        yield return ["ビクともしねぇ", new[] { "ビクともしねぇ" }];
        yield return ["ビクともせん", new[] { "ビクともせん" }];
        yield return ["ゲームってのはいつもそういう性質のもの。", new[] { "ゲーム", "ってのは", "いつも", "そういう", "性質", "の", "もの" }];

        // === 母性的 is JMDict 2870498 (adj-na, "motherly") — keep as single token ===
        yield return ["母性的な", new[] { "母性的な" }];

        // === 要するに should be combined as a single expression ===
        yield return ["要するに負ける要素なんざない訳で", new[] { "要するに", "負ける", "要素", "なんざ", "ない", "訳", "で" }];

        // === 守銭奴 should be a single noun (め is consumed as a suffix) ===
        yield return ["守銭奴め", new[] { "守銭奴" }];

        // === この前 should be combined as a single adverb ===
        yield return ["ついこの前えん罪で監禁されたばっかだしな", new[] { "つい", "この前", "えん罪", "で", "監禁された", "ばっか", "だ", "し", "な" }];

        // === 頚木 should be a single word (variant of 頸木/くびき, user_dic normalizes kanji) ===
        yield return ["頚木を超えて", new[] { "頸木", "を", "超えて" }];

        // === 前出すぎ should split as 前 + 出すぎ, not 前出 + すぎ ===
        yield return ["おまえだけ明らかに前出すぎだ", new[] { "おまえ", "だけ", "明らか", "に", "前", "出すぎ", "だ" }];

        // === ふざっけんな — original surface preserved via sokuon-skip ===
        yield return ["ふざっけんな！", new[] { "ふざっけんな" }];

        // === 早くー — original surface preserved ===
        yield return ["ルーデウス早くー！", new[] { "ルー", "デウス", "早くー" }];

        // === Pending: need user_dic.xml entries (Sudachi splits these incorrectly) ===
        // からかう: Sudachi splits as から(particle) + かう(verb)
        yield return ["俺のことからかうから信用ない", new[] { "俺", "の", "こと", "からかう", "から", "信用", "ない" }];
        yield return ["満足すべき", new[] { "満足す", "べき" }];
        // 涎たらす: Sudachi splits たらしたら as たら(conditional) + したら
        yield return ["でも、涎たらしたら怒ります", new[] { "でも", "涎", "たらしたら", "怒ります" }];
        // ですー: Sudachi handles via prolonged sound lookup, original surface preserved
        yield return ["困るですー", new[] { "困る", "ですー" }];
        yield return ["一緒に回りたいですー！", new[] { "一緒に", "回りたい", "ですー" }];

        // Shouldn't be split as 死ん-だの
        yield return ["自分が死んだのを誰か", new[] { "自分","が","死んだ","の","を","誰か" }];
        yield return ["いつ死んだの", new[] { "いつ","死んだ","の" }];

        yield return ["共通しているのは、重要なエリア区画のみになるか。",
            new[] { "共通している", "の", "は", "重要な", "エリア", "区画", "のみ", "に", "なる", "か" }];
        yield return ["その様子を眺めながら、今の話について何となく考えを巡らせる。",
            new[] { "その", "様子", "を", "眺めながら", "今", "の", "話", "について", "何となく", "考えを巡らせる" }];
        yield return ["こんな…任務も中途半端なまんま全滅なんて…",
            new[] { "こんな", "任務", "も", "中途半端", "な", "まんま", "全滅", "なんて" }];
        yield return ["人海戦術は常に行っているもの足りないのは量じゃなくて質のほう",
            new[] { "人海戦術", "は", "常に", "行っている", "もの足りない", "の", "は", "量", "じゃなくて", "質", "の", "ほう" }];

        // === Hiragana recombination (RecombineHiraganaTokens) ===
        // Sudachi splits ふんばったり into ふん(interjection) + ばったり(adverb); recombine → 踏ん張る
        yield return ["ふんばったりすると", new[] { "ふんばったり", "する", "と" }];
        // Negative: お(prefix) + かけ(verb) must NOT recombine
        yield return ["私はあなたにお手数をおかけました",
            new[] { "私", "は", "あなた", "に", "お手数", "を", "お", "かけました" }];
        // Negative: いい(adj) + もん(noun) must NOT recombine
        yield return ["たまにはいいもんだよ", new[] { "たまに", "は", "いい", "もん", "だ", "よ" }];
        // Negative: もの(noun) + たち(suffix) must NOT recombine
        yield return ["ものたちへ", new[] { "もの", "たち", "へ" }];

        // === Currently failing — tracked in MISPARSES_TO_FIX.txt ===
        // 張っ付いてん should stay together (currently split as 張+付いてん, loses っ)
        yield return ["耳早いわねえ何なのあんたマジでずっとＢＢＳに張っ付いてんの？",
            new[] { "耳", "早い", "わねえ", "何なの", "あんた", "マジ", "で", "ずっと", "ＢＢＳ", "に", "張っ付いてん", "の" }];
        // 引っ掛かる should stay as one token (currently split as 引+掛かる)
        yield return ["が逆にそこが引っ掛かる、一貫し過ぎてるからな",
            new[] { "が", "逆に", "そこ", "が", "引っ掛かる", "一貫", "し過ぎてる", "から", "な" }];
        // 来なすった — archaic なさる past; compound merge with 来 not handled
        yield return ["ほうどこから来なすった？",
            new[] { "ほう", "どこ", "から", "来なすった" }];
        // ああまた — Sudachi mis-splits as あ+あまた; should be ああ+また
        yield return ["ああまたこれか", new[] { "また", "これ", "か" }];  // ああ is gated (archaic kanji 嗚呼)
        // 事を運ぶ idiom — should merge as a single expression
        yield return ["スザンナに見つからないように事を運ばないと",
            new[] { "スザンナ", "に", "見つからない", "ように", "事を運ばない", "と" }];

        // 面の皮 — resegmenter picks 面の as Name instead of 面/の/皮
        yield return ["さすがに分厚すぎるだろ、面の皮",
            new[] { "さすがに", "分厚すぎる", "だろ", "面", "の", "皮" }];
        yield return ["マンセーしすぎ", new[] { "マンセー", "しすぎ" }];
        // ならびに — should be merged as a single conjunction (JMDict 1521550)
        yield return ["リヴァイ班ならびにハンジ班は鎧の巨人をしとめよ",
            new[] { "リヴァイ", "班", "ならびに", "ハンジ", "班", "は", "鎧", "の", "巨人", "を", "しとめよ" }];
        // 本の — Sudachi emits as prenominal 形動 (matching JMDict 1011740 "mere"), should be 本/の
        yield return ["この本の制作に携わってくださった皆様",
            new[] { "この", "本", "の", "制作", "に", "携わってくださった", "皆様" }];
        // 残虐非道 — should merge as a single compound (JMDict 2170720)
        yield return ["連中はまさしく悪魔で残虐非道なヤツらだったよ",
            new[] { "連中", "は", "まさしく", "悪魔", "で", "残虐非道な", "ヤツら", "だった", "よ" }];
        // 意気軒昂 — yojijukugo, should merge as single compound (JMDict 1156440)
        yield return ["彼は意気軒昂な様子だった",
            new[] { "彼", "は", "意気軒昂な", "様子", "だった" }];
        // 中の人 — Sudachi swallows の+人 into a 中の人 expression, breaking 世界中 compound
        yield return ["マーレや世界中の人と話し合って誤解を解けば",
            new[] { "マーレ", "や", "世界中", "の", "人", "と", "話し合って", "誤解", "を", "解けば" }];
        // に劣らず — should merge as compound expression (JMDict 2834137)
        yield return ["私に劣らずなかなか方向音痴のようです",
            new[] { "私", "に劣らず", "なかなか", "方向音痴", "の", "ようです" }];
        // それよか — should merge as single expression (JMDict 2207840)
        yield return ["それよかもっと銃火器の訓練して",
            new[] { "それよか", "もっと", "銃火器", "の", "訓練して" }];
        // 我先に — resegmenter splits 我先 into 我+先 (JMDict 2220580 我先 exists)
        yield return ["我先に逃げるなんてありえないの",
            new[] { "我先に", "逃げる", "なんて", "ありえない", "の" }];
        // まずいわね — Sudachi treats いわね as a person name (固有名詞)
        yield return ["やっぱりまずいわねこのお酒",
            new[] { "やっぱり", "まずい", "わね", "この", "お酒" }];
        // 数えきれない — Sudachi steals 数 into 言葉数 compound, breaking 数えきれない
        yield return ["こっちはそんな言葉数えきれないほど聞いているんだ",
            new[] { "こっち", "は", "そんな", "言葉", "数えきれない", "ほど", "聞いている", "んだ" }];

        // しょうがねぇ colloquial — currently split as しょう+が+ねぇ
        yield return ["ったくしょうがねぇなぁ",
            new[] { "ったく", "しょうがねぇ", "なぁ" }];
        yield return ["しょうがねぇときってのがあらァ",
            new[] { "しょうがねぇ", "とき", "って", "の", "が", "ある" }];
        // でしょう auxiliary — currently split as で+しょう, and ファ is also being eaten from ファルマ
        yield return ["あっ、何でしょうファルマ様",
            new[] { "あっ", "何", "でしょう", "ファルマ", "様" }];
        yield return ["……自称、でしょうに",
            new[] { "自称", "でしょう", "に" }];
        // 自称 written in kana — currently じ is dropped entirely, leaving only しょう
        yield return ["じしょう未来人さん",
            new[] { "じしょう", "未来人", "さん" }];
        // 師匠 written in kana — currently ししょう split as し+しょう
        yield return ["おれのししょうがいってた",
            new[] { "おれ", "の", "ししょう", "が", "いってた" }];
        // 亜人 compound — currently split as 亜 + 人たち (prefix not combined)
        yield return ["道具を使う人間や亜人たちが誕生した。",
            new[] { "道具", "を", "使う", "人間", "や", "亜人", "たち", "が", "誕生した" }];
        yield return ["苦痛に悶え苦しむよりは幾分マシだった",
            new[] { "苦痛", "に", "悶え", "苦しむ", "より", "は", "幾分", "マシ", "だった" }];
        // Godan volitional with colloquial ー elongation: 泳ごー should merge to 泳ごう (one token)
        // rather than split as 泳 + ご + ー. RepairVowelElongation pattern.
        yield return ["手治ったら一緒に泳ごーね。",
            new[] { "手", "治ったら", "一緒に", "泳ごう", "ね" }];

        //  太鼓持ち+かい — Sudachi splits as 太鼓持 + ちかい (mis-analysed as 近い adj).
        yield return ["生徒会長の太鼓持ちかい？",
            new[] { "生徒会長", "の", "太鼓持ち", "かい" }];
        // 半人前 — Sudachi glues で onto 人前, producing 半 + 人前で + も.
        yield return ["例え半人前でも俺は魔術師なんだから",
            new[] { "例え", "半人前", "でも", "俺", "は", "魔術師", "なんだ", "から" }];
        // 貴様+に — Sudachi tags 貴 as prefix and glues 様+に → 貴 + 様に.
        yield return ["貴様に用はないアサシン",
            new[] { "貴様", "に", "用", "は", "ない", "アサシン" }];
        // 放っておく — Sudachi pre-glues おけばいい. Should split as 放って + おけば + いい
        // (放っておく is v5k; おけば is its provisional; いい follows as separate adjective).
        yield return ["放っておけばいい。",
            new[] { "放っておけば", "いい" }];
        // なれ (命令/可能 of 成る) mis-tagged as 代名詞 (archaic pronoun 汝 "thou").
        // First case: 冷静になれ forms a valid compound (冷静になる exp, 2557400), so the bare token
        // collapses into the compound; segmentation reflects the compound match.
        yield return ["それで冷静になれって、どんな修行僧よ。",
            new[] { "それで", "冷静になれって", "どんな", "修行僧", "よ" }];
        yield return ["誰だって、なろうと思えば、なれんだよ。",
            new[] { "誰", "だって", "なろう", "と", "思えば", "なれ", "んだ", "よ" }];
        yield return ["誰かの特別になんてなれやしない。",
            new[] { "誰か", "の", "特別", "に", "なんて", "なれ", "や", "しない" }];
        yield return ["官の営による一大事業",
            new[] { "官", "の", "営", "による", "一大", "事業" }];
        yield return ["もと御同期の方であるそうで",
            new[] { "もと", "御", "同期", "の", "方", "であるそう", "で" }];
        yield return ["なんでももと同期が相手なら",
            new[] { "なんでも", "もと", "同期", "が", "相手", "なら" }];
        yield return ["眼差し向けるであろう",
            new[] { "眼差し", "向ける", "であろう" }];
        
        yield return ["心あるものが", new[] {"心ある","もの","が" }];
        yield return ["信仰心ある人間", new[] {"信仰心","ある","人間" }];

        // === Volitional + vowel elongation — original surface preserved ===
        yield return ["遊ぼー", new[] { "遊ぼー" }];

        // === Compound verb not in Sudachi ===
        yield return ["節くれだった", new[] { "節くれだった" }];
        yield return ["節くれだっていた", new[] { "節くれだっていた" }];

        // === Onomatopoeia ん-boundary ===
        yield return ["こほんと咳払いした", new[] { "こほん", "と", "咳払いした" }];

        // === Compound particle かい ===
        yield return ["負傷したのかい？", new[] { "負傷した", "の", "かい" }];

        // === Expression 露にする ===
        yield return ["露にして", new[] { "露にして" }];

        // === Sudachi false compound: 今手 is not a name ===
        yield return ["今手に持っている", new[] { "今", "手", "に", "持っている" }];

        // === Sudachi splits 体力 + 力無い: 体力 should stay together ===
        yield return ["体力無さすぎです", new[] { "体力", "無さすぎ", "です" }];

        // === Sudachi splits 創造主 as 創造+主たる ===
        yield return ["創造主たる", new[] { "創造主", "たる" }];

        // === Colloquial どした = どうした ===
        yield return ["どしたんだい", new[] { "どう", "した", "んだ" }];
        yield return ["どして俺がいると", new[] { "どうして", "俺", "が", "いる", "と" }];

        // === Yojijukugo 完全無欠 should be 1 word (na-adj + な merges naturally) ===
        yield return ["完全無欠な", new[] { "完全無欠な" }];

        // === 言いすぎた: noun+た should prefer verb form (structural: transition rule drops た after noun) ===
        yield return ["少し言いすぎたようだ許せ", new[] { "少し", "言いすぎた", "ようだ", "許せ" }];

        // === 賽は投げられた is a set phrase (alea iacta est) — merged as expression, か stays separate ===
        yield return ["賽は投げられた…か", new[] { "賽は投げられた", "か" }];

        // === Classical なれど should combine ===
        yield return ["身なれどご助力したい", new[] { "身", "なれど", "ご", "助力したい" }];

        // === 諸君 should be 1 word, not 諸+君たち ===
        yield return ["諸君たち", new[] { "諸君", "たち" }];

        // === 飛び道具 should be 1 word, not 飛び+道具 ===
        yield return ["俺に飛び道具は効かんぞ", new[] { "俺", "に", "飛び道具", "は", "効かん", "ぞ" }];

        // === ゴブリン should be 1 word, not ゴ+ブ+ン ===
        yield return ["ゴブリンスレイヤー", new[] { "ゴブリン", "スレイヤー" }];

        // === なんてひどい should split as なんて+ひどい, not なん+てひどい ===
        yield return ["なんてひどい…", new[] { "なんて", "ひどい" }];

        // === 槍ぶすま should be 1 word (line of spears) ===
        yield return ["この人数なら槍ぶすまで対応できる", new[] { "この", "人数", "なら", "槍ぶすま", "で", "対応できる" }];

        // === 地母神 should be 1 word, 様 separate ===
        yield return ["地母神様にお供えしたいのです。", new[] { "地母神", "様", "に", "お", "供え", "したい", "の", "です" }];

        // === Stuttering: 3+ identical kana runs should not produce matches ===
        yield return ["ぼぼぼぼぼ", new string[] { }];
        yield return ["そして数秒後、ぼぼぼぼぼぼん", new[] { "そして", "数秒", "後", "ん" }];
        yield return ["ぼっ、ぼぼぼぼぼぼくはぼくは学園都市第六位", new[] { "は", "ぼく", "は", "学園都市", "第六", "位" }];

        // === Stuttering: comma-separated reps (3+ with breaks) ===
        yield return ["ぼ、ぼぼ僕一人で", new[] { "僕", "一人", "で" }];

        // === Stuttering: repeated digraph mora (じょ = 2 kana but 1 mora) ===
        yield return ["じょじょじょ、冗談だよ！", new[] { "冗談", "だ", "よ" }];

        // === Stuttering gate: 2-char repeated kana dropped when next token reading matches ===
        yield return ["恋愛ですかぼぼ、僕は、", new[] { "恋愛", "ですか", "僕", "は" }];

        // === Stuttering gate: single mora stutter before word with matching reading ===
        yield return ["じょ、助教授", new[] { "助教授" }];
        yield return ["じょ、冗談", new[] { "冗談" }];

        // === Medical compound: Sudachi greedily matches 右上 instead of 右 + 上腕骨 ===
        yield return ["右上腕骨不完全骨折", new[] { "右", "上腕骨", "不完全骨折" }];

        // は particle before katakana/hiragana words must not be dropped as stutter
        yield return ["挨拶は、ハードルが", new[] { "挨拶", "は", "ハードル", "が" }];
        yield return ["今は、はっきりと感じる", new[] { "今", "は", "はっきり", "と", "感じる" }];
        // は as genuine stutter should still be dropped
        yield return ["は、はい？", new[] { "はい" }];
        // repeated はい separated by ellipsis should NOT merge into はいはい (crawling)
        yield return ["はい……はい、分かりました。", new[] { "はい", "分かりました" }];

        // === 呼ぶ-family: verb stems must not be split by resegmentation ===
        yield return ["呼ばれもしない", new[] { "呼ばれ", "も", "しない" }];
        yield return ["ランスの呼びつけを無視して", new[] { "ランス", "の", "呼びつけ", "を", "無視して" }];
        yield return ["もしもセキュリティを呼ばれでもしたら", new[] { "もしも", "セキュリティ", "を", "呼ばれ", "でも", "したら" }];
        yield return ["何だったら俺を呼びつけでもいいし", new[] { "何", "だったら", "俺", "を", "呼びつけ", "でも", "いい", "し" }];

        // Long vowel mark inside verb: 呼ーぼう → 呼ぼう (volitional of 呼ぶ)
        yield return ["フィルチを呼ーぼう", new[] { "フィルチ", "を", "呼ぼう" }];

        // 呼ばわる past tense — Sudachi splits 呼+ばった(grasshopper), should be single verb token
        yield return ["彼女は強く戸を敲きつけて更に大きく呼ばった。", new[] { "彼女", "は", "強く", "戸", "を", "敲きつけて", "更に", "大きく", "呼ばった" }];

        // Short katakana words ending in ー must not be absorbed into preceding compound
        yield return ["もう一度レバーを引いてみる。", new[] { "もう一度", "レバー", "を", "引いてみる" }];
        yield return ["もう一度ニャーがいなくなる可能性も", new[] { "もう一度", "ニャー", "が", "いなくなる", "可能性", "も" }];
        yield return ["明後日、もう一度ドリーのところに行って欲しい。", new[] { "明後日", "もう一度", "ドリー", "の", "ところ", "に", "行って", "欲しい" }];
        
        yield return ["誰が", new[] { "誰", "が" }];

        // 上の四人 — amount-combined token (四人) must not let CombineCompounds
        // match 上の人 (boss) via the counter's inherited DictionaryForm
        yield return ["察知した上の四人は", new[] { "察知した", "上", "の", "四人", "は" }];

        // ぶっ壊れる not in JMDict — split into prefix ぶっ + 壊れた
        yield return ["ぶっ壊れた", new[] { "ぶっ", "壊れた" }];

        // でもちょっと — Sudachi splits as で+もちょっ+と (OOV); PreprocessText forces correct boundary
        yield return ["でもちょっとは俺がいなくなって悲しいでしょ？", new[] { "でも", "ちょっと", "は", "俺", "が", "いなくなって", "悲しい", "でしょ" }];

        // お内儀 — Sudachi outputs as one token; no JMDict match; resegmentation picks お内+儀 (wrong)
        yield return ["その方はお館様のお内儀で", new[] { "その", "方", "は", "お館様", "の", "お", "内儀", "で" }];

        // 翌々日朝 — Sudachi splits as 翌々+日朝 (Japan-North Korea); should be 翌々日+朝
        yield return ["新聞等の報道規制は、翌々日朝には解除する。", new[] { "新聞", "等", "の", "報道", "規制", "は", "翌々日", "朝", "には", "解除する" }];

        // 服着て — Sudachi misclassifies 着 as noun suffix (ギ) after clothing noun; should be verb 着る te-form
        yield return ["あんな服着て歌うの？", new[] { "あんな", "服", "着て", "歌う", "の" }];

        // どうあっても — expression "whatever happens", not adverb どう + te-form of 獰悪 + particle も
        yield return ["どうあっても覆すことはできないのだから", new[] { "どうあっても", "覆す", "こと", "は", "できない", "の", "だから" }];

        // 国際連合教育科学文化機関 — 6-token compound noun (UNESCO), needs >5 token window
        yield return ["国際連合教育科学文化機関", new[] { "国際連合教育科学文化機関" }];

        // 連邦国支部長 — resegmentation should split 支部長 as 支部+長 (branch+chief), not 支+部長
        yield return ["連邦国支部長", new[] { "連邦", "国", "支部長" }];

        // ってば should combine from って+ば into a single particle (WordId 2130420)
        yield return ["……あーあ、馬鹿らしいことで悩んでたわ、私ってば。", new[] { "あーあ", "馬鹿らしい", "こと", "で", "悩んでた", "わ", "私", "ってば" }];

        // こう+ついてってる — adverb こう must not merge with verb ついてってる to form 交通
        yield return ["ミミにいろんな勲章がこうついてってる感覚は…", new[] { "ミミ", "に", "いろんな", "勲章", "が", "こう", "ついてってる", "感覚", "は" }];

        // こと+そうだ — plain noun こと must not combine with そう (hearsay copula, not appearance/seeming)
        yield return ["できることそうだ。", new[] { "できる", "こと", "そう", "だ" }];

        // 元気そう — na-adjective noun must still combine with そう (appearance/seeming)
        yield return ["元気そうだ", new[] { "元気そう", "だ" }];

        // Colloquial ておこう contraction: ためとこう = ためておこう (let's save)
        yield return ["よーしお小遣いためとこう！", new[] { "よーし", "お小遣い", "ためとこう" }];

        // ぶち (adverb intensifier) + verb → compound verb ぶちキレる
        yield return ["ぶちキレてる！", new[] { "ぶちキレてる" }];

        // 誕生日会 (birthday party) should be a single token
        yield return ["まだ？お誕生日会遅れるよ", new[] { "まだ", "お", "誕生日会", "遅れる", "よ" }];

        // すまなかった should not be split by OOV splitter
        yield return ["余計な心配をさせてすまなかった", new[] { "余計な", "心配", "を", "させて", "すまなかった" }];

        // いるか (dolphin) should not form when か is an embedded question particle before というと
        yield return ["どうしてこんな所にいるかというと、それはまあ、ちょっとした話…。", new[] { "どうして", "こんな", "所", "に", "いる", "か", "という", "と", "それ", "は", "まあ", "ちょっと", "した", "話" }];

        // 走り高跳び should be a single token
        yield return ["あなた日が落ちるまでずっと走り高跳びやってたことがあるでしょ", new[] { "あなた", "日", "が", "落ちる", "まで", "ずっと", "走り高跳び", "やってた", "ことがある", "でしょ" }];

        // うちら should not be split into う + ちらと
        yield return ["うちらと同じく認識阻害の魔法で守られてるさかい", new[] { "うちら", "と", "同じく", "認識", "阻害", "の", "魔法", "で", "守られてる", "さかい" }];

        // いけず (na-adj, unkind) should not be parsed as いける negative form
        yield return ["いけずやわ", new[] { "いけず", "や", "わ" }];

        // いい子ぶって should combine when compound exists in JMDict
        yield return ["何今さらいい子ぶってんだよ", new[] { "何", "今さら", "いい子ぶって", "んだ", "よ" }];

        // 私戦(しせん) should split into 私 + 戦いたく when followed by verb continuation
        yield return ["私戦いたくなんてないんです", new[] { "私", "戦いたく", "なんて", "ない", "んです" }];

        // そう + だった should not merge into そうだった (争奪)
        yield return ["そうだったんですか", new[] { "そう", "だった", "んです", "か" }];

        // おおきに (Kansai "thank you") should not be split into お + おきに
        yield return ["煙幕焚いてもろておおきにな", new[] { "煙幕", "焚いてもろて", "おおきに", "な" }];

        // 倒しちゃいます should be a single token (ちゃう contraction of てしまう)
        yield return ["倒しちゃいます", new[] { "倒しちゃいます" }];

        // ではなかろうか should be a single token, not split with なかろうか matching 中廊下
        yield return ["少し飲み過ぎではなかろうか", new[] { "少し", "飲み過ぎ", "ではなかろうか" }];
        yield return ["恥ずかしい振る舞いではなかろうか", new[] { "恥ずかしい", "振る舞い", "ではなかろうか" }];

        // 用事 should not be swallowed into 私用 + 事
        yield return ["すみません私用事ができました", new[] { "すみません", "私", "用事", "が", "できました" }];

        // 着なきゃ should be one token (着る + contracted conditional)
        yield return ["本当にこれ着なきゃダメ", new[] { "本当", "に", "これ", "着なきゃ", "ダメ" }];

        // 長かった should be one token (長い past), not split from 列長 compound
        yield return ["列長かった？", new[] { "列", "長かった" }];

        // はいてる should be one token (履く progressive), not は particle + 凍てる
        yield return ["でも、きっとパンツはいてる", new[] { "でも", "きっと", "パンツ", "はいてる" }];

        // 第一人者 as single token — user dic entry
        yield return ["第一人者", new[] { "第一人者" }];

        // 突然変異 as single token — user dic entry
        yield return ["突然変異", new[] { "突然変異" }];

        // 丸暗記 as single token — user dic entry
        yield return ["答え丸暗記させればさすがに何とかなるだろ", new[] { "答え", "丸暗記させれば", "さすがに", "何とかなる", "だろ" }];

        // 会話をする — Sudachi fuses 話をする as MWE, breaking 会話
        yield return ["会話をする時間", new[] { "会話", "を", "する", "時間" }];

        // 打たれ強い — compound adjective, should not split into 打たれ + 強さ
        yield return ["打たれ強さ", new[] { "打たれ強さ" }];

        // 全く以って — single expression (全く以て)
        yield return ["全く以って", new[] { "全く以って" }];

        // 逃れ得ぬ — 得る is a suffix meaning "able to", should NOT merge with preceding verb when compound not in JMDict
        yield return ["逃れ得ぬ運命", new[] { "逃れ", "得ぬ", "運命" }];

        // はねつける — Sudachi splits as は+ね+つけ+た; user_dic entry forces single verb token
        yield return ["自国の指導者をからかわれたドイツから厳重な抗議があったが、米国は頑としてはねつけた。",
            new[] { "自国", "の", "指導者", "を", "からかわれた", "ドイツ", "から", "厳重な", "抗議", "が", "あった", "が", "米国", "は", "頑として", "はねつけた" }];

        // おはよ — colloquial おはよう, Sudachi splits as お+は+よ; user_dic entry forces single token
        yield return ["おはよ", new[] { "おはよ" }];

        yield return ["やさぐれた感じを受ける", new[] { "やさぐれた", "感じ", "を", "受ける" }];

        // 言うことを聞く (to obey) — user_dic entry makes Sudachi tokenize as single verb
        yield return ["よく言うことを聞いてるね", new[] { "よく", "言うことを聞いてる", "ね" }];

        // 慈しみ合う not in JMDict — Sudachi compounds it, split stage should decompose into 慈しみ + 合ってる
        yield return ["そのお互いに見つめ合って分かり合って慈しみ合ってるような瞳は",
            new[] { "その", "お互い", "に", "見つめ合って", "分かり合って", "慈しみ", "合ってる", "ような", "瞳", "は" }];

        // 思い人 + 様 — Sudachi must prefer 思い人 (user_dic) over 思い + 人様
        yield return ["思い人様", new[] { "思い人", "様" }];
    }

    [Theory]
    [MemberData(nameof(SegmentationCases))]
    public async Task SegmentationTest(string text, string[] expectedResult)
    {
        (await Parse(text)).Should().Equal(expectedResult);
    }

}
