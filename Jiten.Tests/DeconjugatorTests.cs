using FluentAssertions;
using Jiten.Parser;

namespace Jiten.Tests;

public class DeconjugatorTests
{
    [Theory]
    [InlineData("終わってしまった", "終わる")]
    [InlineData("わからない", "わかる")]
    [InlineData("みて", "みる")]
    [InlineData("作る", "作る")]
    [InlineData("なかった", "ない")]
    [InlineData("選びださねば", "選びだす")]
    // Colloquial contractions: てしまう → ちゃう/ちまう/じゃう/じまう
    [InlineData("食べちゃった", "食べる")]
    [InlineData("飲んじゃった", "飲む")]
    [InlineData("食べちまった", "食べる")]
    [InlineData("飲んじまった", "飲む")]
    // Colloquial contractions: ていく → てく, ておく → とく
    [InlineData("食べてく", "食べる")]
    [InlineData("なってく", "なる")]
    [InlineData("飲んでく", "飲む")]
    [InlineData("食べとく", "食べる")]
    [InlineData("飲んどく", "飲む")]
    // Colloquial contractions: conditional けりゃ/きゃ, ては → ちゃ, では → じゃ
    [InlineData("食べなくちゃ", "食べる")]
    [InlineData("食べなけりゃ", "食べる")]
    [InlineData("行かなきゃ", "行く")]
    // Colloquial contractions: slang negative んない, adjective ねえ/ねぇ
    [InlineData("わかんない", "わかる")]
    [InlineData("つまんない", "つまる")]
    [InlineData("すごくねえ", "すごい")]
    [InlineData("でしょ", "です")]
    [InlineData("でしょう", "です")]
    [InlineData("行きまひょ", "行く")]
    // Na-adjective adnominal form (な is attributive, not casual request)
    [InlineData("和やかな", "和やか")]
    [InlineData("大切な", "大切")]
    [InlineData("静かな", "静か")]
    public void Deconjugation_ShouldContainExpectedDictionaryForm(string text, string expectedBase)
    {
        var deconjugator = new Deconjugator();

        var forms = deconjugator.Deconjugate(text);

        forms.Select(f => f.Text).Should().Contain(expectedBase);
    }

    [Fact]
    public void Deconjugation_ShouldBeDeterministicForSameInput()
    {
        var deconjugator = new Deconjugator();
        const string input = "終わってしまった";

        var first = deconjugator.Deconjugate(input);
        var second = deconjugator.Deconjugate(input);

        first.Select(FormatForm).Should().Equal(second.Select(FormatForm));
    }

    [Fact]
    public void Deconjugation_ShouldNotContainDuplicateForms()
    {
        var deconjugator = new Deconjugator();
        const string input = "終わってしまった";

        var forms = deconjugator.Deconjugate(input);
        var keys = forms.Select(FormatForm).ToList();

        keys.Distinct(StringComparer.Ordinal).Count().Should().Be(keys.Count);
    }

    [Theory]
    [InlineData("終わってしまった", 200)]
    [InlineData("食べさせられなかった", 250)]
    [InlineData("読ませられなかった", 250)]
    public void Deconjugation_ShouldRemainBoundedForComplexInputs(string input, int maxForms)
    {
        var deconjugator = new Deconjugator();

        var forms = deconjugator.Deconjugate(input);

        forms.Count.Should().BeLessOrEqualTo(maxForms);
    }

    [Fact]
    public void Cache_ShouldEvict_WhenCapacityIsExceeded()
    {
        var deconjugator = new Deconjugator(maxCacheEntries: 3);
        deconjugator.ClearCacheForTesting();

        // First 4 entries trigger rotation: gen0→gen1, gen0 becomes empty
        _ = deconjugator.Deconjugate("わからない");
        _ = deconjugator.Deconjugate("みて");
        _ = deconjugator.Deconjugate("なかった");
        _ = deconjugator.Deconjugate("終わってしまった");

        // Next 4 entries trigger second rotation: old gen1 evicted
        _ = deconjugator.Deconjugate("食べる");
        _ = deconjugator.Deconjugate("飲んだ");
        _ = deconjugator.Deconjugate("走った");
        _ = deconjugator.Deconjugate("書いた");

        var stats = deconjugator.GetCacheStats();
        stats.Evictions.Should().BeGreaterThan(0);
        stats.Stores.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Cache_ShouldTrackHitsAndMisses()
    {
        var deconjugator = new Deconjugator(maxCacheEntries: 10);
        deconjugator.ClearCacheForTesting();

        _ = deconjugator.Deconjugate("わからない"); // miss + store
        _ = deconjugator.Deconjugate("わからない"); // hit

        var stats = deconjugator.GetCacheStats();
        stats.Hits.Should().BeGreaterThan(0);
        stats.Misses.Should().BeGreaterThan(0);
    }

    private static string FormatForm(DeconjugationForm form)
    {
        var tags = string.Join(",", form.Tags);
        var process = string.Join(",", form.Process);
        var seen = string.Join(",", form.SeenText.OrderBy(x => x, StringComparer.Ordinal));
        return $"{form.Text}|{form.OriginalText}|{tags}|{process}|{seen}";
    }
}
