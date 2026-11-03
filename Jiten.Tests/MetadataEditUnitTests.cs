using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core.Data.MetadataEdits;
using Xunit;

namespace Jiten.Tests;

public class MetadataEditUnitTests
{
    [Theory]
    [InlineData(MetadataField.OriginalTitle, "フツウノファンタジー")]
    [InlineData(MetadataField.RomajiTitle, "Kira☆Kira")]
    [InlineData(MetadataField.Description, "前島鹿之助は部室に入った。")]
    public void Strings_read_back_from_jsonb_equal_the_serialised_value(MetadataField field, string value)
    {
        var serialised = DeckMetadataSnapshot.ToElement(value);
        var fromJsonb = JsonDocument.Parse($"\"{value}\"").RootElement;

        serialised.GetRawText().Should().NotBe(fromJsonb.GetRawText(), "the serializer escapes non-ASCII and jsonb does not");
        DeckMetadataSnapshot.KeyEquals(field, serialised, fromJsonb).Should().BeTrue();
    }

    [Fact]
    public void Different_strings_still_differ()
    {
        DeckMetadataSnapshot.KeyEquals(MetadataField.OriginalTitle, DeckMetadataSnapshot.ToElement("きらきら"), DeckMetadataSnapshot.ToElement("キラキラ"))
            .Should().BeFalse();
        DeckMetadataSnapshot.KeyEquals(MetadataField.MediaType, DeckMetadataSnapshot.ToElement(7), DeckMetadataSnapshot.ToElement(4)).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://vndb.org/v1474?utm_source=x&utm_medium=y", "https://vndb.org/v1474")]
    [InlineData("https://example.com/a?id=3&fbclid=abc#section", "https://example.com/a?id=3#section")]
    [InlineData("https://example.com/a#:~:text=hello", "https://example.com/a")]
    [InlineData("https://www.youtube.com/watch?v=abc&si=share&t=42", "https://www.youtube.com/watch?v=abc&t=42")]
    [InlineData("https://youtu.be/abc?si=share", "https://youtu.be/abc")]
    [InlineData("https://www.amazon.co.jp/gp/product/4040000000?tag=aff-22&ref_=x", "https://www.amazon.co.jp/dp/4040000000")]
    [InlineData("https://www.amazon.com/s/ref=nb_sb?k=kira&tag=aff-20", "https://www.amazon.com/s")]
    [InlineData("https://www.google.com/url?sa=t&url=https%3A%2F%2Fvndb.org%2Fv1474%3Futm_source%3Dg", "https://vndb.org/v1474")]
    [InlineData("https://ncode.syosetu.com/n1234ab/", "https://ncode.syosetu.com/n1234ab/")]
    [InlineData("https://ja.wikipedia.org/wiki/キラ☆キラ", "https://ja.wikipedia.org/wiki/キラ☆キラ")]
    [InlineData("not a url", "not a url")]
    public void The_link_cleaner_drops_tracking_and_keeps_meaningful_parts(string input, string expected) =>
        LinkUrlCleaner.Clean(input).Should().Be(expected);

    [Theory]
    [InlineData("vndb.org/v1474", "https://vndb.org/v1474")]
    [InlineData("http://vndb.org/v1474", "http://vndb.org/v1474")]
    [InlineData("just words", "just words")]
    public void Bare_hosts_get_https(string input, string expected) => MetadataEditService.NormaliseUrl(input).Should().Be(expected);

    [Fact]
    public void The_alias_cap_only_blocks_additions()
    {
        var over = Enumerable.Range(1, 17).Select(i => $"a{i}").ToList();
        MetadataEditService.AddsPastAliasCap(over, over.Skip(1).ToList()).Should().BeFalse();
        MetadataEditService.AddsPastAliasCap(over, over.Skip(1).Append("new").ToList()).Should().BeTrue();
        MetadataEditService.AddsPastAliasCap(over.Take(14).ToList(), over.Take(15).ToList()).Should().BeFalse();
    }
}
