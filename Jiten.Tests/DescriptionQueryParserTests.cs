using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Xunit;

namespace Jiten.Tests;

public class DescriptionQueryParserTests
{
    [Theory]
    [InlineData("a visual novel about ninja", "ninja", MediaType.VisualNovel)]
    [InlineData("Visual Novels with a ninja protagonist", "with a ninja protagonist", MediaType.VisualNovel)]
    [InlineData("cooking competition anime", "cooking competition", MediaType.Anime)]
    [InlineData("an anime where the protagonist keeps dying", "the protagonist keeps dying", MediaType.Anime)]
    [InlineData("web novel about a villainess", "a villainess", MediaType.WebNovel)]
    [InlineData("light novel isekai with a lazy hero", "isekai with a lazy hero", MediaType.Novel)]
    [InlineData("audio drama about a lighthouse keeper", "a lighthouse keeper", MediaType.Audio)]
    [InlineData("a medical drama in an emergency room", "medical in an emergency room", MediaType.Drama)]
    [InlineData("アニメで探偵もの、主人公がアホ", "探偵もの、主人公がアホ", MediaType.Anime)]
    [InlineData("田舎町でゆっくり進む恋愛の小説", "田舎町でゆっくり進む恋愛", MediaType.Novel)]
    [InlineData("なろう系の追放もの", "追放もの", MediaType.WebNovel)]
    [InlineData("a VN about ninja", "ninja", MediaType.VisualNovel)]
    [InlineData("VNs with a ninja protagonist", "with a ninja protagonist", MediaType.VisualNovel)]
    [InlineData("otome game set in a boarding school", "set in a boarding school", MediaType.VisualNovel)]
    [InlineData("an LN about dungeon exploring", "dungeon exploring", MediaType.Novel)]
    [InlineData("WN where the villainess wins", "the villainess wins", MediaType.WebNovel)]
    [InlineData("j-drama about a bank employee", "a bank employee", MediaType.Drama)]
    [InlineData("drama CD about a lighthouse keeper", "a lighthouse keeper", MediaType.Audio)]
    [InlineData("乙女ゲーで執事が出てくるやつ", "執事が出てくるやつ", MediaType.VisualNovel)]
    [InlineData("劇場版で泣ける話", "泣ける話", MediaType.Movie)]
    public void Extracts_the_media_type_and_leaves_the_rest(string query, string expectedText, MediaType expectedType)
    {
        var parsed = DescriptionQueryParser.Parse(query);
        parsed.MediaType.Should().Be(expectedType);
        parsed.Text.Should().Be(expectedText);
    }

    [Theory]
    [InlineData("slow-burn romance in a rural town")]
    [InlineData("a death game with a twist")]
    [InlineData("gamers who fall in love")]
    [InlineData("探偵もの、主人公がアホ")]
    public void Leaves_queries_without_a_type_word_untouched(string query)
    {
        var parsed = DescriptionQueryParser.Parse(query);
        parsed.MediaType.Should().BeNull();
        parsed.Text.Should().Be(query);
    }

    [Theory]
    [InlineData("onmyoji", "onmyoji")]
    [InlineData("Onmyouji", "onmyoji")]
    [InlineData("onmyōji", "onmyoji")]
    [InlineData("Yuuki Yuuna", "yuki yuna")]
    public void Folds_romanisation_variants_together(string input, string expected)
    {
        DescriptionKeywords.Fold(input).Should().Be(expected);
    }

    [Fact]
    public void Keywords_skip_function_words_and_keep_kanji_runs()
    {
        DescriptionKeywords.Extract("a lonely girl who can see ghosts").Should().Equal("lonely", "girl", "ghosts");
        DescriptionKeywords.Extract("陰陽師が妖怪を倒す話").Should().Equal("陰陽師", "妖怪");
    }

    [Theory]
    [InlineData("a story about an onmyoji", "a story about an onmyoji (yin-yang master who exorcises spirits and curses in old Japan)")]
    [InlineData("Onmyoji", "Onmyoji (yin-yang master who exorcises spirits and curses in old Japan)")]
    [InlineData("陰陽師が妖怪を倒す", "陰陽師（呪術で妖怪や怨霊を祓う平安時代の術師）が妖怪（日本の伝承に出てくる化け物や霊）を倒す")]
    [InlineData("a blue sky", "a blue sky")]
    [InlineData("problems", "problems")]
    public void Glossary_expands_terms_the_model_cannot_read(string input, string expected)
    {
        DescriptionQueryGlossary.Expand(input).Should().Be(expected);
    }

    [Fact]
    public void A_bare_type_word_keeps_the_original_text_to_rank_on()
    {
        var parsed = DescriptionQueryParser.Parse("anime");
        parsed.MediaType.Should().Be(MediaType.Anime);
        parsed.Text.Should().Be("anime");
    }
}
