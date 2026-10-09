using FluentAssertions;
using Jiten.Core.Data;

namespace Jiten.Tests;

/// <summary>Mirrors getEdgeFlow in Jiten.Web/test/franchiseBuilder.spec.ts; the builder and the server cycle check must agree.</summary>
public class DeckRelationshipStoryFlowTests
{
    [Theory]
    [InlineData(DeckRelationshipType.Sequel)]
    [InlineData(DeckRelationshipType.Fandisc)]
    [InlineData(DeckRelationshipType.Spinoff)]
    [InlineData(DeckRelationshipType.SideStory)]
    public void SequelLikeEdges_FlowFromTargetToSource(DeckRelationshipType type)
    {
        DeckRelationship.StoryFlow(type, 2, 1).Should().Be((1, 2));
    }

    [Fact]
    public void Adaptation_FlowsFromSourceToTarget()
    {
        DeckRelationship.StoryFlow(DeckRelationshipType.Adaptation, 1, 2).Should().Be((1, 2));
    }

    [Theory]
    [InlineData(DeckRelationshipType.Alternative)]
    [InlineData(DeckRelationshipType.SameSeries)]
    [InlineData(DeckRelationshipType.SameSetting)]
    public void UndirectedTypes_HaveNoFlow(DeckRelationshipType type)
    {
        DeckRelationship.StoryFlow(type, 1, 2).Should().BeNull();
    }
}
