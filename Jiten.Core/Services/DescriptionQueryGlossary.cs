using System.Text.RegularExpressions;

namespace Jiten.Core.Services;

/// <summary>
/// Expands Japanese-culture terms the sentence model cannot read into words it can. The
/// tokenizer splits "onmyoji" into on/my/oji, so on its own the word carries no meaning; glossed
/// as "yin-yang master who exorcises spirits and curses" it lands next to the right descriptions.
/// Only the embedded text is expanded; the keyword boost still sees the original term.
/// </summary>
public static partial class DescriptionQueryGlossary
{
    private static readonly (string Term, string Gloss)[] Entries =
    [
        ("onmyoji", "yin-yang master who exorcises spirits and curses in old Japan"),
        ("onmyouji", "yin-yang master who exorcises spirits and curses in old Japan"),
        ("陰陽師", "呪術で妖怪や怨霊を祓う平安時代の術師"),
        ("yokai", "Japanese supernatural monsters and spirits"),
        ("youkai", "Japanese supernatural monsters and spirits"),
        ("妖怪", "日本の伝承に出てくる化け物や霊"),
        ("ayakashi", "Japanese ghosts and supernatural spirits"),
        ("mononoke", "vengeful spirits and supernatural beings"),
        ("oni", "Japanese demon ogre"),
        ("kitsune", "fox spirit that shapeshifts"),
        ("tanuki", "raccoon dog spirit that shapeshifts"),
        ("tengu", "long-nosed mountain demon"),
        ("kappa", "river monster from Japanese folklore"),
        ("shinigami", "god of death who reaps souls"),
        ("死神", "魂を刈り取る死の神"),
        ("miko", "shrine maiden"),
        ("巫女", "神社に仕える少女"),
        ("shinobi", "ninja assassin and spy"),
        ("kunoichi", "female ninja"),
        ("ronin", "masterless wandering samurai"),
        ("bushido", "samurai code of honour"),
        ("sengoku", "warring states period of feudal Japan"),
        ("bakumatsu", "final years of the Edo period before the Meiji restoration"),
        ("edo period", "feudal Japan under the Tokugawa shogunate"),
        ("heian", "Heian period of court nobles and spirits in old Kyoto"),
        ("isekai", "transported or reincarnated into another fantasy world"),
        ("異世界", "別の世界に転生したり召喚されたりする"),
        ("tensei", "reincarnated in another world"),
        ("iyashikei", "calm healing slice of life with no conflict"),
        ("癒し系", "穏やかで心が休まる日常"),
        ("nakige", "emotional story written to make the reader cry"),
        ("泣きゲー", "感動して泣ける物語"),
        ("utsuge", "bleak depressing story with a tragic ending"),
        ("moege", "cute girls and light romance with little plot"),
        ("charage", "character-driven romance with cute heroines"),
        ("kamige", "masterpiece"),
        ("tsundere", "girl who is hostile at first and warms up over time"),
        ("yandere", "lover whose affection turns obsessive and violent"),
        ("kuudere", "cold and quiet girl who slowly opens up"),
        ("hikikomori", "recluse who never leaves their room"),
        ("引きこもり", "部屋から出ない社会的ひきこもり"),
        ("otaku", "obsessive anime and game fan"),
        ("gyaru", "fashionable tanned party girl"),
        ("tokusatsu", "live-action superhero show with special effects and monsters"),
        ("特撮", "特殊効果を使ったヒーロー番組"),
        ("sentai", "colour-coded team of superheroes"),
        ("kaiju", "giant monster"),
        ("mecha", "giant piloted robots"),
        ("mahou shoujo", "magical girl who transforms to fight evil"),
        ("魔法少女", "変身して戦う少女"),
        ("seinen", "aimed at adult men"),
        ("josei", "aimed at adult women"),
        ("shounen", "action adventure for teenage boys"),
        ("shoujo", "romance for teenage girls"),
        ("harem", "one protagonist surrounded by several love interests"),
        ("otome", "romance where the heroine chooses between suitors"),
        ("bl", "boys love romance between men"),
        ("yaoi", "boys love romance between men"),
        ("yuri", "romance between girls"),
        ("百合", "女性同士の恋愛"),
        ("nikkei", "Japanese emigrants abroad"),
        ("koshien", "national high school baseball tournament"),
        ("甲子園", "高校野球の全国大会"),
        ("shogi", "Japanese chess"),
        ("go player", "player of the board game go"),
        ("rakugo", "traditional comic storytelling"),
        ("kabuki", "traditional Japanese theatre"),
        ("enka", "traditional Japanese ballad singing"),
        ("idol", "pop idol singer"),
        ("vtuber", "virtual streamer with an anime avatar"),
        ("yakuza", "Japanese organised crime gangsters"),
        ("kaiseki", "traditional multi-course Japanese cuisine"),
        ("izakaya", "Japanese pub"),
        ("onsen", "hot spring"),
        ("ryokan", "traditional Japanese inn"),
        ("shitamachi", "old working-class downtown neighbourhood"),
        ("salaryman", "office worker"),
        ("ol", "female office worker"),
        ("bunkasai", "school culture festival"),
        ("juku", "cram school"),
        ("ronin student", "student who failed entrance exams and studies for another year"),
    ];

    private static readonly Dictionary<string, string> Lookup = Entries.ToDictionary(e => e.Term, e => e.Gloss, StringComparer.OrdinalIgnoreCase);

    // One pass over all terms, longest first, so a gloss is never itself glossed.
    private static readonly Regex Terms = BuildPattern();

    private static Regex BuildPattern()
    {
        var ordered = Entries.Select(e => e.Term).OrderByDescending(t => t.Length).ToList();
        var latin = string.Join("|", ordered.Where(t => t.All(c => c < 128)).Select(Regex.Escape));
        var japanese = string.Join("|", ordered.Where(t => t.Any(c => c > 127)).Select(Regex.Escape));
        return new Regex($@"(?<![\p{{L}}\p{{N}}])(?<latin>{latin})(?![\p{{L}}\p{{N}}])|(?<jp>{japanese})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>Returns the text with every known term followed by its gloss in parentheses.</summary>
    public static string Expand(string text) =>
        Terms.Replace(text, m =>
        {
            var gloss = Lookup[m.Value];
            return m.Groups["latin"].Success ? $"{m.Value} ({gloss})" : $"{m.Value}（{gloss}）";
        });
}
