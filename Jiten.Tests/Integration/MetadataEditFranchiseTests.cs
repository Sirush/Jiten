using System.Net;
using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditFranchiseTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DbAsync(async db =>
        {
            await db.Decks.Where(d => d.DeckId == ThirdDeckId).ExecuteUpdateAsync(s => s.SetProperty(d => d.FranchiseId, OtherFranchiseId));
            db.DeckRelationships.Add(new DeckRelationship
            {
                SourceDeckId = ThirdDeckId, TargetDeckId = OtherDeckId, RelationshipType = DeckRelationshipType.Sequel
            });
            await db.SaveChangesAsync();
        });
    }

    private Task<int?> FranchiseOfAsync(int deckId) =>
        DbAsync(db => db.Decks.Where(d => d.DeckId == deckId).Select(d => d.FranchiseId).FirstAsync());

    [Fact]
    public async Task The_picker_resolves_a_deck_to_its_franchise()
    {
        var franchise = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{OtherDeckId}/franchise-ref", Users.Author));
        franchise["franchiseId"]!.GetValue<int>().Should().Be(OtherFranchiseId);
        franchise["deckCount"]!.GetValue<int>().Should().Be(2);

        var unfranchised = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/franchise-ref", Users.Author));
        unfranchised["franchiseId"].Should().BeNull();
        unfranchised["originalTitle"]!.GetValue<string>().Should().Be("きらきら");
    }

    [Fact]
    public async Task Picking_a_deck_without_a_franchise_starts_one_with_both_decks()
    {
        await DbAsync(db => db.DeckRelationships.ExecuteDeleteAsync());
        await DbAsync(db => db.Decks.Where(d => d.DeckId == ThirdDeckId).ExecuteUpdateAsync(s => s.SetProperty(d => d.FranchiseId, (int?)null)));

        var result = await SubmitOkAsync(Users.PlusFan, new { franchiseDeckId = ThirdDeckId, sources = new[] { Source } });
        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        var proposed = queue["items"]![0]!["changes"]![0]!["after"]!;
        proposed["franchiseId"].Should().BeNull();
        proposed["withDeckId"]!.GetValue<int>().Should().Be(ThirdDeckId);

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(result)}/approve", Users.Editor, new { }));

        var started = await FranchiseOfAsync(DeckId);
        started.Should().NotBeNull();
        (await FranchiseOfAsync(ThirdDeckId)).Should().Be(started);
        var recorded = await DbAsync(db => db.MetadataEdits.Where(e => e.Id == EditId(result)).Select(e => e.ChangesJson).FirstAsync());
        recorded.Should().Contain($"\"franchiseId\":{started}");

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(result)}/revert", Users.Admin));
        (await FranchiseOfAsync(DeckId)).Should().BeNull();
        (await FranchiseOfAsync(ThirdDeckId)).Should().BeNull();
    }

    [Fact]
    public async Task Adding_a_deck_to_a_franchise_is_reviewed_and_joins_it_once_approved()
    {
        var result = await SubmitOkAsync(Users.PlusFan, new { franchiseDeckId = OtherDeckId, sources = new[] { Source } });
        Ints(result["pendingFields"]).Should().Equal((int)MetadataField.Franchise);

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        var effect = queue["items"]![0]!["franchiseEffect"]!;
        effect["joining"]![0]!["franchiseId"]!.GetValue<int>().Should().Be(OtherFranchiseId);
        effect["into"].Should().BeNull();

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(result)}/approve", Users.Editor, new { }));

        (await FranchiseOfAsync(DeckId)).Should().Be(OtherFranchiseId);
        (await FranchiseOfAsync(OtherDeckId)).Should().Be(OtherFranchiseId);
        var editable = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Author));
        editable["franchise"]!["franchiseId"]!.GetValue<int>().Should().Be(OtherFranchiseId);
    }

    [Fact]
    public async Task Reverting_takes_only_the_added_deck_out_of_the_franchise()
    {
        var result = await SubmitOkAsync(Users.Editor, new { franchiseDeckId = OtherDeckId, sources = new[] { Source } });
        (await FranchiseOfAsync(DeckId)).Should().Be(OtherFranchiseId);

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(result)}/revert", Users.Admin));

        (await FranchiseOfAsync(DeckId)).Should().BeNull();
        (await FranchiseOfAsync(OtherDeckId)).Should().Be(OtherFranchiseId);
        (await FranchiseOfAsync(ThirdDeckId)).Should().Be(OtherFranchiseId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Chained_pending_edits_end_in_one_franchise_whichever_is_approved_first(bool approveFirstEditFirst)
    {
        await DbAsync(db => db.DeckRelationships.ExecuteDeleteAsync());
        await DbAsync(db => db.Decks.ExecuteUpdateAsync(s => s.SetProperty(d => d.FranchiseId, (int?)null)));

        var first = EditId(await SubmitOkAsync(Users.PlusFan, new { franchiseDeckId = ThirdDeckId, sources = new[] { Source } }, DeckId));
        var second = EditId(await SubmitOkAsync(Users.Author, new { franchiseDeckId = OtherDeckId, sources = new[] { Source } }, ThirdDeckId));

        foreach (var id in approveFirstEditFirst ? new[] { first, second } : new[] { second, first })
            await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{id}/approve", Users.Editor, new { }));

        var franchise = await FranchiseOfAsync(DeckId);
        franchise.Should().NotBeNull();
        (await FranchiseOfAsync(OtherDeckId)).Should().Be(franchise);
        (await FranchiseOfAsync(ThirdDeckId)).Should().Be(franchise);
    }

    [Fact]
    public async Task An_edit_whose_deck_joined_a_franchise_meanwhile_adds_the_picked_deck_and_reverts_only_that()
    {
        await DbAsync(db => db.DeckRelationships.ExecuteDeleteAsync());
        await DbAsync(db => db.Decks.ExecuteUpdateAsync(s => s.SetProperty(d => d.FranchiseId, (int?)null)));

        var first = EditId(await SubmitOkAsync(Users.PlusFan, new { franchiseDeckId = ThirdDeckId, sources = new[] { Source } }, DeckId));
        var second = EditId(await SubmitOkAsync(Users.Author, new { franchiseDeckId = OtherDeckId, sources = new[] { Source } }, ThirdDeckId));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{first}/approve", Users.Editor, new { }));

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        var item = queue["items"]![0]!;
        item["conflicts"]!.AsArray().Should().BeEmpty();
        item["franchiseEffect"]!["addedDeckTitle"]!.GetValue<string>().Should().Be("ほかの話");

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{second}/approve", Users.Editor, new { }));
        (await DbAsync(db => db.MetadataEdits.Where(e => e.Id == second).Select(e => e.DeckId).FirstAsync())).Should().Be(OtherDeckId);

        var franchise = await FranchiseOfAsync(DeckId);
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{second}/revert", Users.Admin));
        (await FranchiseOfAsync(OtherDeckId)).Should().BeNull();
        (await FranchiseOfAsync(DeckId)).Should().Be(franchise);
        (await FranchiseOfAsync(ThirdDeckId)).Should().Be(franchise);
    }

    [Fact]
    public async Task A_deck_already_in_a_franchise_cannot_be_moved()
    {
        var message = await ErrorAsync(SubmitAsync(Users.Editor, new { franchiseDeckId = OtherDeckId, sources = new[] { Source } }, OtherDeckId),
                                       HttpStatusCode.BadRequest);
        message.Should().Be("This deck is already in a franchise.");

        await ErrorAsync(SubmitAsync(Users.Editor, new { franchiseDeckId = MissingDeckId, sources = new[] { Source } }), HttpStatusCode.BadRequest);
        await ErrorAsync(SubmitAsync(Users.Editor, new { franchiseDeckId = DeckId, sources = new[] { Source } }), HttpStatusCode.BadRequest);
    }
}
