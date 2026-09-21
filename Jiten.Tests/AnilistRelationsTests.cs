using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Tests;

public class AnilistRelationsTests
{
    [Theory]
    [InlineData("SPIN_OFF", DeckRelationshipType.Spinoff)]
    [InlineData("SIDE_STORY", DeckRelationshipType.SideStory)]
    public void ResolveParentRelation_UsesParentBackEdge_ChildIsSource(string backEdge, DeckRelationshipType expected)
    {
        // The child is the deck being processed, so SwapDirection=false makes it the source of the primary edge.
        MetadataProviderHelper.ResolveParentRelation(backEdge).Should().Be((expected, false));
    }

    [Fact]
    public void ResolveParentRelation_NoBackEdge_FallsBackToSideStory()
    {
        MetadataProviderHelper.ResolveParentRelation(null).Should().Be((DeckRelationshipType.SideStory, false));
    }

    [Theory]
    [InlineData("SUMMARY")]
    [InlineData("CHARACTER")]
    [InlineData("OTHER")]
    public void ResolveParentRelation_UnmappedBackEdge_IsSkipped(string backEdge)
    {
        MetadataProviderHelper.ResolveParentRelation(backEdge).Should().BeNull();
    }
}

public class JikanRelationsTests
{
    [Theory]
    [InlineData("Spin-off", DeckRelationshipType.Spinoff)]
    [InlineData("Side story", DeckRelationshipType.SideStory)]
    public void ResolveJikanParentRelation_UsesParentBackEdge_ChildIsSource(string backEdge, DeckRelationshipType expected)
    {
        MetadataProviderHelper.ResolveJikanParentRelation(backEdge).Should().Be((expected, false));
    }

    [Fact]
    public void ResolveJikanParentRelation_NoBackEdge_FallsBackToSideStory()
    {
        MetadataProviderHelper.ResolveJikanParentRelation(null).Should().Be((DeckRelationshipType.SideStory, false));
    }

    [Theory]
    [InlineData("Summary")]
    [InlineData("Character")]
    [InlineData("Other")]
    public void ResolveJikanParentRelation_UnmappedBackEdge_IsSkipped(string backEdge)
    {
        MetadataProviderHelper.ResolveJikanParentRelation(backEdge).Should().BeNull();
    }
}
