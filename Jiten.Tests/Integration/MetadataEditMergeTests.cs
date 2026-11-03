using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditMergeTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    private const int TagGamma = 33;
    private const string VndbUrl = "https://vndb.org/v1474";

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await DbAsync(async db =>
        {
            db.Tags.Add(new Tag { TagId = TagGamma, Name = "Gamma" });
            await db.SaveChangesAsync();
        });
    }

    private async Task<long> PendingAsync(string userId, object body)
    {
        var result = await SubmitOkAsync(userId, body);
        Ints(result["appliedFields"]).Should().BeEmpty();
        return EditId(result);
    }

    private Task<(HttpStatusCode Status, JsonNode? Body)> ApproveAsync(long editId, object? body = null) =>
        SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/approve", Users.Editor, body ?? new { });

    private async Task ApproveOkAsync(long editId, object? body = null) =>
        (await OkAsync(ApproveAsync(editId, body)))["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Approved);

    private Task<MetadataEditStatus> StatusAsync(long editId) =>
        DbAsync(db => db.MetadataEdits.Where(e => e.Id == editId).Select(e => e.Status).SingleAsync());

    private Task SeedAliasAsync(string alias) =>
        DbAsync(async db =>
        {
            db.DeckTitles.Add(new DeckTitle { DeckId = DeckId, Title = alias, TitleType = DeckTitleType.Alias, Deck = null! });
            await db.SaveChangesAsync();
        });

    private static object Tags(params (int TagId, int Percentage)[] tags) =>
        new { tags = tags.Select(t => new { tagId = t.TagId, percentage = t.Percentage }).ToArray(), sources = new[] { Source } };

    private static object Links(params (LinkType Type, string Url)[] links) =>
        new { links = links.Select(l => new { linkType = (int)l.Type, url = l.Url }).ToArray(), sources = new[] { Source } };

    [Fact]
    public async Task Two_pending_edits_adding_different_aliases_both_apply_without_a_conflict()
    {
        var first = await PendingAsync(Users.Author, new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });
        var second = await PendingAsync(Users.Checker, new { aliases = new[] { "KiraKira" }, sources = new[] { Source } });

        await ApproveOkAsync(first);
        (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor)))["items"]![0]!["conflicts"]!.AsArray()
            .Should().BeEmpty();
        await ApproveOkAsync(second);

        (await DeckAsync()).Titles.Select(t => t.Title).Should().BeEquivalentTo(["Kirakira", "KiraKira"]);
    }

    [Fact]
    public async Task Two_pending_edits_adding_different_genres_both_apply()
    {
        var first = await PendingAsync(Users.Author, new { genres = new[] { (int)Genre.Action, (int)Genre.Drama }, sources = new[] { Source } });
        var second = await PendingAsync(Users.Checker, new { genres = new[] { (int)Genre.Action, (int)Genre.Comedy }, sources = new[] { Source } });

        await ApproveOkAsync(first);
        await ApproveOkAsync(second);

        (await DeckAsync()).DeckGenres.Select(g => g.Genre).Should().BeEquivalentTo([Genre.Action, Genre.Comedy, Genre.Drama]);
    }

    [Fact]
    public async Task Two_pending_edits_adding_different_character_names_both_apply()
    {
        var first = await PendingAsync(Users.Author, new { addCharacterNames = new[] { "前島 鹿之助" }, sources = new[] { Source } });
        var second = await PendingAsync(Users.Checker, new { addCharacterNames = new[] { "きらり" }, sources = new[] { Source } });

        await ApproveOkAsync(first);
        await ApproveOkAsync(second);

        (await DeckAsync()).DictionaryEntries.Select(e => e.Surface).Should().BeEquivalentTo(["前島鹿之助", "前島", "鹿之助", "きらり"]);
    }

    [Fact]
    public async Task A_removal_and_an_addition_from_different_edits_both_apply()
    {
        await SeedAliasAsync("Old Alias");
        var removal = await PendingAsync(Users.Author, new { aliases = Array.Empty<string>(), sources = new[] { Source } });
        var addition = await PendingAsync(Users.Checker, new { aliases = new[] { "Old Alias", "New Alias" }, sources = new[] { Source } });

        await ApproveOkAsync(removal);
        await ApproveOkAsync(addition);

        (await DeckAsync()).Titles.Select(t => t.Title).Should().Equal("New Alias");
    }

    [Fact]
    public async Task An_edit_adding_an_alias_that_is_already_live_is_superseded()
    {
        var first = await PendingAsync(Users.Author, new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });
        var second = await PendingAsync(Users.Checker, new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });

        await ApproveOkAsync(first);

        (await StatusAsync(second)).Should().Be(MetadataEditStatus.Superseded);
        (await DeckAsync()).Titles.Select(t => t.Title).Should().Equal("Kirakira");
    }

    [Fact]
    public async Task A_tag_percentage_clash_needs_a_resolution_and_keeping_current_still_applies_the_other_additions()
    {
        var first = await PendingAsync(Users.Author, Tags((TagAlpha, 40), (TagBeta, 40)));
        var second = await PendingAsync(Users.Checker, Tags((TagAlpha, 40), (TagBeta, 80), (TagGamma, 50)));
        await ApproveOkAsync(first);

        var (status, body) = await ApproveAsync(second);
        status.Should().Be(HttpStatusCode.Conflict);
        body!["conflicts"]!.AsArray().Select(c => c!["field"]!.GetValue<int>()).Should().Equal((int)MetadataField.Tags);

        await ApproveOkAsync(second, new { resolutions = new[] { new { field = (int)MetadataField.Tags, keep = "current" } } });

        var tags = (await DeckAsync()).DeckTags.ToDictionary(t => t.TagId, t => (int)t.Percentage);
        tags.Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 40, [TagBeta] = 40, [TagGamma] = 50 });
    }

    [Fact]
    public async Task Resolving_a_tag_clash_with_the_edits_value_applies_its_percentage()
    {
        var first = await PendingAsync(Users.Author, Tags((TagAlpha, 40), (TagBeta, 40)));
        var second = await PendingAsync(Users.Checker, Tags((TagAlpha, 40), (TagBeta, 80), (TagGamma, 50)));
        await ApproveOkAsync(first);

        await ApproveOkAsync(second, new { resolutions = new[] { new { field = (int)MetadataField.Tags, keep = "edit" } } });

        var tags = (await DeckAsync()).DeckTags.ToDictionary(t => t.TagId, t => (int)t.Percentage);
        tags.Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 40, [TagBeta] = 80, [TagGamma] = 50 });
    }

    [Fact]
    public async Task Tag_edits_touching_different_tags_merge_without_a_conflict()
    {
        var first = await PendingAsync(Users.Author, Tags((TagAlpha, 10), (TagBeta, 60)));
        var second = await PendingAsync(Users.Checker, Tags((TagAlpha, 40), (TagBeta, 60), (TagGamma, 50)));

        await ApproveOkAsync(first);
        await ApproveOkAsync(second);

        var tags = (await DeckAsync()).DeckTags.ToDictionary(t => t.TagId, t => (int)t.Percentage);
        tags.Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 10, [TagBeta] = 60, [TagGamma] = 50 });
    }

    [Fact]
    public async Task Two_edits_changing_the_same_single_url_link_clash()
    {
        var first = await PendingAsync(Users.Author, Links((LinkType.Vndb, "https://vndb.org/v1475")));
        var second = await PendingAsync(Users.Checker, Links((LinkType.Vndb, "https://vndb.org/v1476")));
        await ApproveOkAsync(first);

        var (status, body) = await ApproveAsync(second);
        status.Should().Be(HttpStatusCode.Conflict);
        body!["conflicts"]!.AsArray().Select(c => c!["field"]!.GetValue<int>()).Should().Equal((int)MetadataField.Links);

        await ApproveOkAsync(second, new { resolutions = new[] { new { field = (int)MetadataField.Links, keep = "edit" } } });
        (await DbAsync(db => db.Set<Link>().Where(l => l.DeckId == DeckId).Select(l => l.Url).ToListAsync())).Should().Equal("https://vndb.org/v1476");
    }

    [Fact]
    public async Task Web_links_from_different_edits_merge_as_a_set()
    {
        var first = await PendingAsync(Users.Author, Links((LinkType.Vndb, VndbUrl), (LinkType.Web, "https://a.example.com")));
        var second = await PendingAsync(Users.Checker, Links((LinkType.Vndb, VndbUrl), (LinkType.Web, "https://b.example.com")));

        await ApproveOkAsync(first);
        await ApproveOkAsync(second);

        (await DbAsync(db => db.Set<Link>().Where(l => l.DeckId == DeckId).Select(l => l.Url).ToListAsync()))
            .Should().BeEquivalentTo([VndbUrl, "https://a.example.com", "https://b.example.com"]);
    }

    [Fact]
    public async Task A_link_change_and_a_web_addition_from_different_edits_both_apply()
    {
        var first = await PendingAsync(Users.Author, Links((LinkType.Vndb, "https://vndb.org/v1475")));
        var second = await PendingAsync(Users.Checker, Links((LinkType.Vndb, VndbUrl), (LinkType.Web, "https://a.example.com")));

        await ApproveOkAsync(first);
        await ApproveOkAsync(second);

        (await DbAsync(db => db.Set<Link>().Where(l => l.DeckId == DeckId).Select(l => l.Url).ToListAsync()))
            .Should().BeEquivalentTo(["https://vndb.org/v1475", "https://a.example.com"]);
    }

    [Fact]
    public async Task Relationship_edits_adding_different_edges_both_apply()
    {
        var first = await PendingAsync(Users.Author, new
        {
            relationships = new[] { new { sourceDeckId = DeckId, targetDeckId = OtherDeckId, relationshipType = (int)DeckRelationshipType.Sequel } },
            sources = new[] { Source }
        });
        var second = await PendingAsync(Users.Checker, new
        {
            relationships = new[] { new { sourceDeckId = ThirdDeckId, targetDeckId = DeckId, relationshipType = (int)DeckRelationshipType.Fandisc } },
            sources = new[] { Source }
        });

        await ApproveOkAsync(first);
        await ApproveOkAsync(second);

        (await DbAsync(db => db.DeckRelationships.Where(r => r.RelationshipType == DeckRelationshipType.Sequel || r.RelationshipType == DeckRelationshipType.Fandisc)
                                .Select(r => new { r.SourceDeckId, r.TargetDeckId }).ToListAsync()))
            .Should().BeEquivalentTo([new { SourceDeckId = DeckId, TargetDeckId = OtherDeckId }, new { SourceDeckId = ThirdDeckId, TargetDeckId = DeckId }]);
    }

    [Fact]
    public async Task Reverting_a_list_edit_removes_only_what_it_added()
    {
        var plusEdit = await SubmitOkAsync(Users.PlusFan, new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });
        Ints(plusEdit["appliedFields"]).Should().Equal((int)MetadataField.Aliases);
        var reviewerEdit = await SubmitOkAsync(Users.Reviewer, new { aliases = new[] { "Kirakira", "KiraKira" }, sources = new[] { Source } });
        Ints(reviewerEdit["appliedFields"]).Should().Equal((int)MetadataField.Aliases);

        var result = await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(plusEdit)}/revert", Users.Editor));

        Ints(result["revertedFields"]).Should().Equal((int)MetadataField.Aliases);
        Ints(result["skippedFields"]).Should().BeEmpty();
        (await DeckAsync()).Titles.Select(t => t.Title).Should().Equal("KiraKira");
    }

    [Fact]
    public async Task Reverting_a_list_removal_re_adds_only_what_it_removed()
    {
        await SeedAliasAsync("Old Alias");
        var removal = await SubmitOkAsync(Users.PlusFan, new { aliases = Array.Empty<string>(), sources = new[] { Source } });
        Ints(removal["appliedFields"]).Should().Equal((int)MetadataField.Aliases);
        await SubmitOkAsync(Users.Reviewer, new { aliases = new[] { "New Alias" }, sources = new[] { Source } });

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(removal)}/revert", Users.Editor));

        (await DeckAsync()).Titles.Select(t => t.Title).Should().BeEquivalentTo(["Old Alias", "New Alias"]);
    }

    [Fact]
    public async Task Reverting_a_list_edit_whose_additions_are_already_gone_is_refused()
    {
        var plusEdit = await SubmitOkAsync(Users.PlusFan, new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });
        await SubmitOkAsync(Users.Editor, new { aliases = Array.Empty<string>(), sources = new[] { Source } });

        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(plusEdit)}/revert", Users.Editor), HttpStatusCode.Conflict))
            .Should().Be("Every field in this edit was changed again since, so there's nothing to revert.");
    }

    private Task<Dictionary<int, int>> TagMapAsync() =>
        DeckAsync().ContinueWith(t => t.Result.DeckTags.ToDictionary(x => x.TagId, x => (int)x.Percentage));

    [Fact]
    public async Task Reverting_a_tag_addition_skips_the_tag_once_someone_re_tuned_it()
    {
        var addition = EditId(await SubmitOkAsync(Users.Editor, Tags((TagAlpha, 40), (TagBeta, 60), (TagGamma, 50))));
        await SubmitOkAsync(Users.Admin, Tags((TagAlpha, 40), (TagBeta, 60), (TagGamma, 70)));

        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{addition}/revert", Users.Admin), HttpStatusCode.Conflict))
            .Should().Be("Every field in this edit was changed again since, so there's nothing to revert.");
        (await TagMapAsync())[TagGamma].Should().Be(70);
    }

    [Fact]
    public async Task Reverting_a_tag_edit_restores_the_untouched_tags_and_skips_the_re_tuned_one()
    {
        var edit = await SubmitOkAsync(Users.Editor, new
        {
            tags = new[] { new { tagId = TagAlpha, percentage = 20 }, new { tagId = TagBeta, percentage = 60 }, new { tagId = TagGamma, percentage = 50 } },
            aliases = new[] { "Kirakira" }, sources = new[] { Source }
        });
        await SubmitOkAsync(Users.Admin, Tags((TagAlpha, 20), (TagBeta, 60), (TagGamma, 70)));

        var result = await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(edit)}/revert", Users.Admin));

        Ints(result["revertedFields"]).Should().BeEquivalentTo([(int)MetadataField.Aliases, (int)MetadataField.Tags]);
        (await TagMapAsync()).Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 40, [TagBeta] = 60, [TagGamma] = 70 });
        (await DeckAsync()).Titles.Should().BeEmpty();
    }

    [Fact]
    public async Task Reverting_a_tag_addition_alongside_another_field_reverts_only_the_other_field()
    {
        var edit = await SubmitOkAsync(Users.Editor, new
        {
            tags = new[] { new { tagId = TagAlpha, percentage = 40 }, new { tagId = TagBeta, percentage = 60 }, new { tagId = TagGamma, percentage = 50 } },
            aliases = new[] { "Kirakira" }, sources = new[] { Source }
        });
        await SubmitOkAsync(Users.Admin, Tags((TagAlpha, 40), (TagBeta, 60), (TagGamma, 70)));

        var result = await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(edit)}/revert", Users.Admin));

        Ints(result["revertedFields"]).Should().Equal((int)MetadataField.Aliases);
        Ints(result["skippedFields"]).Should().Equal((int)MetadataField.Tags);
        (await TagMapAsync())[TagGamma].Should().Be(70);
        (await DeckAsync()).Titles.Should().BeEmpty();
    }

    [Fact]
    public async Task Approving_the_removal_of_a_re_tuned_tag_needs_a_resolution_and_keeping_current_keeps_it()
    {
        var removal = await PendingAsync(Users.Author, Tags((TagAlpha, 40), (TagGamma, 50)));
        await SubmitOkAsync(Users.Editor, Tags((TagAlpha, 40), (TagBeta, 70)));

        var (status, body) = await ApproveAsync(removal);
        status.Should().Be(HttpStatusCode.Conflict);
        body!["conflicts"]!.AsArray().Select(c => c!["field"]!.GetValue<int>()).Should().Equal((int)MetadataField.Tags);

        await ApproveOkAsync(removal, new { resolutions = new[] { new { field = (int)MetadataField.Tags, keep = "current" } } });
        (await TagMapAsync()).Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 40, [TagBeta] = 70, [TagGamma] = 50 });
    }

    [Fact]
    public async Task Approving_the_removal_of_a_re_tuned_tag_with_the_edits_value_removes_it()
    {
        var removal = await PendingAsync(Users.Author, Tags((TagAlpha, 40), (TagGamma, 50)));
        await SubmitOkAsync(Users.Editor, Tags((TagAlpha, 40), (TagBeta, 70)));

        await ApproveOkAsync(removal, new { resolutions = new[] { new { field = (int)MetadataField.Tags, keep = "edit" } } });
        (await TagMapAsync()).Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 40, [TagGamma] = 50 });
    }

    [Fact]
    public async Task Removing_a_tag_nobody_re_tuned_applies_without_a_conflict()
    {
        var removal = await PendingAsync(Users.Author, Tags((TagAlpha, 40)));
        await SubmitOkAsync(Users.Editor, Tags((TagAlpha, 10), (TagBeta, 60)));

        await ApproveOkAsync(removal);
        (await TagMapAsync()).Should().BeEquivalentTo(new Dictionary<int, int> { [TagAlpha] = 10 });
    }
}
