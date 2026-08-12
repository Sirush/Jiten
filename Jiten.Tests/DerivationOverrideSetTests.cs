using System.Text;
using FluentAssertions;
using Jiten.Core.Data.JMDict;
using Xunit;

namespace Jiten.Tests;

public class DerivationOverrideSetTests : IDisposable
{
    private readonly List<string> _files = [];

    public void Dispose()
    {
        foreach (var file in _files)
            File.Delete(file);
    }

    private string WriteFile(string entries)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, $$"""{"overrides": [{{entries}}]}""", new UTF8Encoding(false));
        _files.Add(path);
        return path;
    }

    [Fact]
    public void RecategorizeEntry_ParsesItsNewCategory()
    {
        var set = DerivationOverrideSet.Load(WriteFile("""
            {"baseWordId": 1, "derivedWordId": 2, "category": "potential",
             "verdict": "Recategorize", "newCategory": "transitivity_pair"}
            """));

        set.TryGet(1, 2, DerivationCategory.Potential, out var over).Should().BeTrue();
        over.Verdict.Should().Be(DerivationVerdict.Recategorize);
        over.NewCategory.Should().Be(DerivationCategory.TransitivityPair);
        over.Direction.Should().BeNull();
    }

    [Fact]
    public void RecategorizeEntry_WithoutANewCategory_IsRejected()
    {
        var set = DerivationOverrideSet.Load(WriteFile("""
            {"baseWordId": 1, "derivedWordId": 2, "category": "potential", "verdict": "Recategorize"}
            """));

        set.Count.Should().Be(0);
        set.UnknownCategoryCount.Should().Be(1);
    }

    [Fact]
    public void DirectionField_SurvivesARecategorize()
    {
        var set = DerivationOverrideSet.Load(WriteFile("""
            {"baseWordId": 3, "derivedWordId": 4, "category": "causative_doublet",
             "verdict": "Recategorize", "newCategory": "potential", "direction": "OneWayOnly"}
            """));

        set.TryGet(3, 4, DerivationCategory.CausativeDoublet, out var over).Should().BeTrue();
        over.Direction.Should().Be(DerivationDirection.BaseToDerivedOnly);
    }

    [Fact]
    public void LegacyRecategorizeField_IsCountedInsteadOfSilentlyIgnored()
    {
        var set = DerivationOverrideSet.Load(WriteFile("""
            {"baseWordId": 5, "derivedWordId": 6, "category": "potential",
             "verdict": "Exclude", "recategorize": "transitive_intransitive"}
            """));

        set.LegacyRecategorizeCount.Should().Be(1);
        set.Count.Should().Be(1);
    }

    [Fact]
    public void MissingFile_Throws_RatherThanBuildingWithoutTheVerdicts()
    {
        var missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

        var load = () => DerivationOverrideSet.Load(missing);

        load.Should().Throw<DerivationOverrideSet.MissingOverrideFileException>();
    }

    [Fact]
    public void ShippedOverrideFile_CarriesNoLegacyRecategorizeField()
    {
        var set = DerivationOverrideSet.Load();

        set.Count.Should().BeGreaterThan(0);
        set.LegacyRecategorizeCount.Should().Be(0);
        set.UnknownCategoryCount.Should().Be(0);
        set.UnknownVerdictCount.Should().Be(0);
    }
}
