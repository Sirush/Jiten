using FluentAssertions;
using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Tests;

public class VndbAnimeRelationsTests
{
    [Fact]
    public void GetVndbAnimeRelations_KnownVn_ReturnsAdaptationRelationsForEachMalId()
    {
        // v4 = CLANNAD, which has several anime adaptations in the VNDB dump (incl. MAL 4181, After Story).
        var relations = MetadataProviderHelper.GetVndbAnimeRelations("v4");

        relations.Should().NotBeEmpty();
        relations.Should().OnlyContain(r =>
            r.LinkType == LinkType.Mal &&
            r.TargetMediaType == MediaType.Anime &&
            r.RelationshipType == DeckRelationshipType.Adaptation &&
            r.SwapDirection == false);

        // SwapDirection=false => DeckRelationship(VN, anime, Adaptation) => the VN is the source material.
        relations.Select(r => r.ExternalId).Should().Contain("4181");
    }

    [Fact]
    public void GetVndbAnimeRelations_UnknownVn_ReturnsEmpty()
    {
        MetadataProviderHelper.GetVndbAnimeRelations("v999999999").Should().BeEmpty();
        MetadataProviderHelper.GetVndbAnimeRelations("").Should().BeEmpty();
    }
}
