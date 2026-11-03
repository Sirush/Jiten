using System.Net;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditSubmitTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    private sealed record Viewer(string Role, bool HasDisplayName, bool EmailConfirmed);

    private async Task<Viewer> ViewerAsync(string userId)
    {
        var v = (await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", userId)))["viewer"]!;
        return new Viewer(v["role"]!.GetValue<string>(), v["hasDisplayName"]!.GetValue<bool>(), v["emailConfirmed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Api_keys_cannot_use_any_metadata_edit_route()
    {
        async Task<HttpStatusCode> WithApiKey(HttpMethod method, string url, string userId, object? body = null)
        {
            var request = new HttpRequestMessage(method, url).WithUser(userId);
            request.Headers.Add("X-Test-ApiKey", "1");
            if (body != null)
                request = request.WithJsonContent(body);
            return (await Client.SendAsync(request)).StatusCode;
        }

        var pending = await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-31", sources = new[] { Source } });
        var editId = pending["editIds"]![0]!.GetValue<long>();

        (await WithApiKey(HttpMethod.Post, $"/api/media-deck/{DeckId}/metadata-edits", Users.Author,
                          new { releaseDate = "2007-04-01", sources = new[] { Source } })).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Author)).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Post, $"/api/media-deck/{DeckId}/character-names/check", Users.Author, new { names = new[] { "前島 鹿之助" } }))
            .Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Put, $"/api/metadata-edits/{editId}/check", Users.Checker, new { verdict = 1 })).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Post, $"/api/metadata-edits/{editId}/approve", Users.Editor, new { })).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Get, "/api/metadata-edits/pending", Users.Checker)).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.Checker)).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Get, "/api/metadata-edits/recent", Users.Checker)).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Get, $"/api/metadata-edits/by-user/{Users.Author}", Users.Checker)).Should().Be(HttpStatusCode.Forbidden);
        (await WithApiKey(HttpMethod.Get, $"/api/metadata-edits/contributions/{Users.Author}", Users.Checker)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Editable_returns_current_values_and_viewer_flags()
    {
        var editable = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Author));
        editable["originalTitle"]!.GetValue<string>().Should().Be("きらきら");
        editable["releaseDate"]!.GetValue<string>().Should().Be("2007-03-30");
        Ints(editable["genres"]).Should().Equal((int)Genre.Action);
        editable["tags"]!.AsArray().Select(t => t!["tagId"]!.GetValue<int>()).Should().Equal(TagBeta, TagAlpha);
        editable["links"]![0]!["url"]!.GetValue<string>().Should().Be("https://vndb.org/v1474");

        (await ViewerAsync(Users.NoName)).HasDisplayName.Should().BeFalse();
        (await ViewerAsync(Users.Unconfirmed)).EmailConfirmed.Should().BeFalse();

        (await ViewerAsync(Users.Author)).Role.Should().Be("user");
        (await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.PlusFan)))["viewer"]!["showsAutoApplyLine"]
            .Should().BeNull("contributors are never told whether their edits skip review");

        (await ViewerAsync(Users.Reviewer)).Role.Should().Be("reviewer");

        var editor = await ViewerAsync(Users.Editor);
        editor.Role.Should().Be("editor");
        (await ViewerAsync(Users.Admin)).Role.Should().Be("editor");
    }

    [Fact]
    public async Task Editable_for_a_subdeck_points_to_the_parent_and_an_unknown_deck_is_404()
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/api/media-deck/{SubdeckId}/editable", Users.Author);
        status.Should().Be(HttpStatusCode.BadRequest);
        body!["parentDeckId"]!.GetValue<int>().Should().Be(DeckId);

        await ErrorAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{MissingDeckId}/editable", Users.Author), HttpStatusCode.NotFound);
        (await SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable")).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submitting_needs_a_display_name_a_confirmed_email_and_a_top_level_deck()
    {
        var edit = new { releaseDate = "2007-03-31", sources = new[] { Source } };

        (await ErrorAsync(SubmitAsync(Users.NoName, edit), HttpStatusCode.BadRequest)).Should().Be("Set a display name before suggesting edits.");
        (await ErrorAsync(SubmitAsync(Users.Unconfirmed, edit), HttpStatusCode.BadRequest)).Should().Be("Confirm your email address before suggesting edits.");

        var (status, body) = await SubmitAsync(Users.Author, edit, SubdeckId);
        status.Should().Be(HttpStatusCode.BadRequest);
        body!["parentDeckId"]!.GetValue<int>().Should().Be(DeckId);

        await ErrorAsync(SubmitAsync(Users.Author, edit, MissingDeckId), HttpStatusCode.NotFound);
        (await DbAsync(db => db.MetadataEdits.CountAsync())).Should().Be(0);
    }

    public static TheoryData<string, string> InvalidSubmissions => new()
    {
        { """{"releaseDate":"2007-03-31"}""", "Add at least one source so someone can check this." },
        { """{"releaseDate":"2007-03-31","sources":["javascript:alert(1)"]}""", "Sources need to be links starting with http:// or https://." },
        { """{"releaseDate":"2007-03-31","sources":["https://a.example.com/1","https://a.example.com/2","https://a.example.com/3","https://a.example.com/4","https://a.example.com/5","https://a.example.com/6"]}""", "Add at most 5 sources." },
        { """{"releaseDate":"2007-03-30","sources":["https://vndb.org/v1474"]}""", "Nothing changed." },
        { """{"releaseDate":"not a date","sources":["https://vndb.org/v1474"]}""", "Use a valid release date." },
        { """{"releaseDate":"2099-01-01","sources":["https://vndb.org/v1474"]}""", "That release date doesn't look right." },
        { """{"description":"A better description."}""", "Add a note explaining the change to description." },
        { """{"originalTitle":"   "}""", "The original title can't be empty." },
        { """{"aliases":["a01","a02","a03","a04","a05","a06","a07","a08","a09","a10","a11","a12","a13","a14","a15","a16"],"sources":["https://vndb.org/v1474"]}""", "A deck can have at most 15 aliases. Remove one before adding another." },
        { """{"aliases":["see https://spam.example.com"],"sources":["https://vndb.org/v1474"]}""", "Aliases can't contain links." },
        { """{"genres":[999],"sources":["https://vndb.org/v1474"]}""", "Pick genres from the list." },
        { """{"tags":[{"tagId":12345,"percentage":50}],"sources":["https://vndb.org/v1474"]}""", "Pick tags from the list." },
        { """{"tags":[{"tagId":11,"percentage":150}],"sources":["https://vndb.org/v1474"]}""", "Tag percentages go from 0 to 100." },
        { """{"links":[{"linkType":2,"url":"https://vndb.org/v1474"},{"linkType":4,"url":"https://evil.example.com/x"}],"sources":["https://vndb.org/v1474"]}""", "AniList links must point to anilist.co." },
        { """{"links":[{"linkType":2,"url":"https://vndb.org/v1"},{"linkType":2,"url":"https://vndb.org/v2"}],"sources":["https://vndb.org/v1474"]}""", "Add only one VNDB link." },
        { """{"relationships":[{"sourceDeckId":2,"targetDeckId":3,"relationshipType":1}],"sources":["https://vndb.org/v1474"]}""", "Every relationship must include this deck." },
        { """{"relationships":[{"sourceDeckId":1,"targetDeckId":999,"relationshipType":1}],"sources":["https://vndb.org/v1474"]}""", "One of the related decks doesn't exist." },
        { """{"removeCharacterNames":["誰か"],"sources":["https://vndb.org/v1474"]}""", "Nothing changed." },
        { """{"addCharacterNames":["Rika"],"sources":["https://vndb.org/v1474"]}""", "Rika: Names can only use kanji, kana, ー, 々, ・ and a space between the family and given name." }
    };

    [Theory]
    [MemberData(nameof(InvalidSubmissions))]
    public async Task Invalid_submissions_are_rejected_with_a_user_facing_message(string json, string message)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(json);

        (await ErrorAsync(SubmitAsync(Users.Author, body), HttpStatusCode.BadRequest)).Should().Be(message);
        (await DbAsync(db => db.MetadataEdits.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_new_contributors_edit_waits_for_review_and_leaves_the_deck_alone()
    {
        var result = await SubmitOkAsync(Users.Author, new
        {
            releaseDate = "2007-03-29", aliases = new[] { "きらきら☆" }, sources = new[] { "https://vndb.org/r6036" }, note = "Retail date"
        });

        Ints(result["appliedFields"]).Should().BeEmpty();
        Ints(result["pendingFields"]).Should().BeEquivalentTo([(int)MetadataField.Aliases, (int)MetadataField.ReleaseDate]);

        var deck = await DeckAsync();
        deck.ReleaseDate.Should().Be(new DateOnly(2007, 3, 30));
        deck.Titles.Should().BeEmpty();
        deck.MetadataLocks.Should().Be(MetadataField.None);

        var row = await DbAsync(db => db.MetadataEdits.SingleAsync());
        row.Id.Should().Be(EditId(result));
        row.Status.Should().Be(MetadataEditStatus.Pending);
        row.UserId.Should().Be(Users.Author);
        row.Fields.Should().Be(MetadataField.Aliases | MetadataField.ReleaseDate);
        row.EditNote.Should().Be("Retail date");
        row.SourcesJson.Should().Contain("r6036");
        row.SubmissionId.ToString().Should().Be(result["submissionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_trusted_contributors_mixed_edit_splits_into_a_live_row_and_a_pending_row()
    {
        var result = await SubmitOkAsync(Users.PlusFan, new
        {
            englishTitle = "Kira☆Kira", genres = new[] { (int)Genre.Action, (int)Genre.Drama }, sources = new[] { Source }
        });

        Ints(result["appliedFields"]).Should().Equal((int)MetadataField.EnglishTitle);
        Ints(result["pendingFields"]).Should().Equal((int)MetadataField.Genres);
        result["editIds"]!.AsArray().Should().HaveCount(2);

        var deck = await DeckAsync();
        deck.EnglishTitle.Should().Be("Kira☆Kira");
        deck.MetadataLocks.Should().HaveFlag(MetadataField.EnglishTitle);
        deck.DeckGenres.Select(g => g.Genre).Should().Equal(Genre.Action);

        var rows = await DbAsync(db => db.MetadataEdits.OrderBy(e => e.Id).ToListAsync());
        rows.Select(r => r.SubmissionId).Distinct().Should().ContainSingle();
        rows.Single(r => r.Fields == MetadataField.EnglishTitle).Status.Should().Be(MetadataEditStatus.Applied);
        rows.Single(r => r.Fields == MetadataField.Genres).Status.Should().Be(MetadataEditStatus.Pending);
    }

    [Fact]
    public async Task An_admin_trusted_contributor_auto_applies_low_risk_fields()
    {
        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Vandal}", Users.Admin, new { adminTrusted = true }));

        var result = await SubmitOkAsync(Users.Vandal, new { romajiTitle = "Kirakira", sources = new[] { Source } });

        Ints(result["appliedFields"]).Should().Equal((int)MetadataField.RomajiTitle);
        (await DeckAsync()).RomajiTitle.Should().Be("Kirakira");
    }

    [Fact]
    public async Task High_risk_fields_stay_pending_for_trusted_contributors_and_need_no_source()
    {
        var result = await SubmitOkAsync(Users.PlusFan, new { description = "A school story.", note = "The old one was a different game." });

        Ints(result["appliedFields"]).Should().BeEmpty();
        Ints(result["pendingFields"]).Should().Equal((int)MetadataField.Description);
    }

    [Fact]
    public async Task A_new_free_form_web_link_is_reviewed_like_a_description()
    {
        var result = await SubmitOkAsync(Users.PlusFan, new
        {
            links = new object[] { new { linkType = (int)LinkType.Vndb, url = "https://vndb.org/v1474" }, new { linkType = (int)LinkType.Web, url = "https://kirakira.example.com" } },
            sources = new[] { Source }
        });

        Ints(result["pendingFields"]).Should().Equal((int)MetadataField.Links);
    }

    [Fact]
    public async Task An_editors_edit_applies_directly_as_approved_including_high_risk_fields()
    {
        var result = await SubmitOkAsync(Users.Editor, new
        {
            description = "A school story.", mediaType = (int)MediaType.Novel, note = "Checked against the box.", sources = new[] { Source }
        });

        Ints(result["appliedFields"]).Should().BeEquivalentTo([(int)MetadataField.Description, (int)MetadataField.MediaType]);
        Ints(result["pendingFields"]).Should().BeEmpty();

        var deck = await DeckAsync();
        deck.Description.Should().Be("A school story.");
        deck.MediaType.Should().Be(MediaType.Novel);
        deck.MetadataLocks.Should().Be(MetadataField.Description | MetadataField.MediaType);

        var row = await DbAsync(db => db.MetadataEdits.SingleAsync());
        row.Status.Should().Be(MetadataEditStatus.Approved);
        row.ReviewedByUserId.Should().Be(Users.Editor);
        row.AppliedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_eleventh_submission_in_an_hour_from_a_new_contributor_is_rate_limited()
    {
        for (var i = 0; i < MetadataEditService.NewHourlyLimit; i++)
            await SubmitOkAsync(Users.Author, new { aliases = new[] { $"rate test {i}" }, sources = new[] { Source } }, OtherDeckId);

        var message = await ErrorAsync(SubmitAsync(Users.Author, new { aliases = new[] { "rate test 10" }, sources = new[] { Source } }, OtherDeckId),
                                       HttpStatusCode.TooManyRequests);
        message.Should().StartWith("You've submitted a lot of edits recently. Try again in ");

        await SubmitOkAsync(Users.Editor, new { aliases = new[] { "editors are not limited" }, sources = new[] { Source } }, OtherDeckId);
    }

    [Fact]
    public async Task Character_name_check_reports_errors_and_surfaces_per_name()
    {
        var results = (await OkAsync(SendAsync(HttpMethod.Post, $"/api/media-deck/{DeckId}/character-names/check", Users.Author,
                                               new { names = new[] { "前島 鹿之助", "Rika", "園崎 魅音", "椎野 きらり" } }))).AsArray();

        results.Should().HaveCount(4);
        results[0]!["error"].Should().BeNull();
        results[0]!["surfaces"]!.AsArray().Select(s => (s!["surface"]!.GetValue<string>(), s["status"]!.GetValue<string>(), s["found"]!.GetValue<bool>()))
            .Should().BeEquivalentTo([("前島鹿之助", "ok", true), ("前島", "ok", true), ("鹿之助", "ok", true)]);
        results[0]!["surfaces"]!.AsArray().Should().OnlyContain(s => s!["occurrences"] == null, "contributors never see exact counts");
        results[1]!["error"]!.GetValue<string>().Should().Contain("kanji, kana");
        results[2]!["error"]!.GetValue<string>().Should().Be("This name doesn't appear anywhere in this deck's text. Check the spelling against the text.");
        results[3]!["error"].Should().BeNull();
        results[3]!["surfaces"]!.AsArray().Select(s => (s!["surface"]!.GetValue<string>(), s["status"]!.GetValue<string>()))
            .Should().BeEquivalentTo([("椎野きらり", "notInText"), ("椎野", "notInText"), ("きらり", "ok")]);
    }

    [Fact]
    public async Task Character_names_are_submitted_as_full_names_and_stored_as_derived_surfaces()
    {
        var result = await SubmitOkAsync(Users.Author, new { addCharacterNames = new[] { "前島 鹿之助", "椎野 きらり" }, sources = new[] { "https://vndb.org/v1474/chars" } });

        Ints(result["pendingFields"]).Should().Equal((int)MetadataField.CharacterNames);
        var change = MetadataEditService.ParseChanges((await DbAsync(db => db.MetadataEdits.SingleAsync())).ChangesJson).Single();
        change.After.Deserialize<List<string>>().Should().BeEquivalentTo(["前島", "前島鹿之助", "鹿之助", "きらり"]);
        (await DeckAsync()).DictionaryEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Human_removals_write_exclusions_that_re_adding_lifts_and_hand_set_tags_become_manual()
    {
        await SubmitOkAsync(Users.Editor, new
        {
            genres = Array.Empty<int>(), tags = new[] { new { tagId = TagBeta, percentage = 60 } }, aliases = new[] { "Kirakira" },
            releaseDate = "2007-03-29", sources = new[] { Source }
        });
        await SubmitOkAsync(Users.Editor, new { aliases = Array.Empty<string>(), sources = new[] { Source } });

        var exclusions = await DbAsync(db => db.DeckMetadataExclusions.Select(x => new { x.Kind, x.Value }).ToListAsync());
        exclusions.Should().BeEquivalentTo([
            new { Kind = DeckMetadataExclusionKind.Genre, Value = ((int)Genre.Action).ToString() },
            new { Kind = DeckMetadataExclusionKind.Tag, Value = TagAlpha.ToString() },
            new { Kind = DeckMetadataExclusionKind.Alias, Value = "Kirakira" }
        ]);
        (await DeckAsync()).MetadataLocks.Should().Be(MetadataField.ReleaseDate);

        await SubmitOkAsync(Users.Editor, new
        {
            genres = new[] { (int)Genre.Action }, tags = new[] { new { tagId = TagAlpha, percentage = 40 }, new { tagId = TagBeta, percentage = 70 } },
            aliases = new[] { "Kirakira" }, sources = new[] { Source }
        });

        var remaining = await DbAsync(db => db.DeckMetadataExclusions.CountAsync());
        remaining.Should().Be(0);
        var tags = (await DeckAsync()).DeckTags.ToDictionary(t => t.TagId);
        tags[TagAlpha].IsManual.Should().BeTrue();
        tags[TagBeta].Percentage.Should().Be(70);
        tags[TagBeta].IsManual.Should().BeTrue();
    }

    [Fact]
    public async Task Removing_a_character_name_writes_an_exclusion()
    {
        await DbAsync(async db =>
        {
            db.DeckDictionaryEntries.Add(new DeckDictionaryEntry { DeckId = DeckId, Surface = "先輩", EntryType = DeckDictionaryEntryType.Name });
            await db.SaveChangesAsync();
        });

        await SubmitOkAsync(Users.Editor, new { removeCharacterNames = new[] { "先輩" }, sources = new[] { Source } });

        (await DeckAsync()).DictionaryEntries.Should().BeEmpty();
        (await DbAsync(db => db.DeckMetadataExclusions.SingleAsync())).Should().BeEquivalentTo(
            new { Kind = DeckMetadataExclusionKind.CharacterName, Value = "先輩", CreatedByUserId = Users.Editor },
            o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task An_admin_dashboard_patch_is_logged_under_the_admins_name_and_locks_the_field()
    {
        await OkAsync(SendAsync(HttpMethod.Patch, $"/api/admin/deck/{DeckId}/metadata", Users.Admin, new { description = "Admin description." }));

        var row = await DbAsync(db => db.MetadataEdits.SingleAsync());
        row.Status.Should().Be(MetadataEditStatus.Approved);
        row.UserId.Should().Be(Users.Admin);
        row.ReviewedByUserId.Should().Be(Users.Admin);
        row.Fields.Should().Be(MetadataField.Description);
        (await DeckAsync()).MetadataLocks.Should().Be(MetadataField.Description);

        var history = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.NoName));
        var top = history["items"]![0]!;
        top["author"]!["displayName"]!.GetValue<string>().Should().Be("TestAdmin");
        top["changes"]![0]!["field"]!.GetValue<int>().Should().Be((int)MetadataField.Description);
    }

    [Fact]
    public async Task An_admin_patch_that_changes_nothing_is_not_logged()
    {
        await OkAsync(SendAsync(HttpMethod.Patch, $"/api/admin/deck/{DeckId}/metadata", Users.Admin, new { englishTitle = "Kira Kira" }));

        (await DbAsync(db => db.MetadataEdits.CountAsync())).Should().Be(0);
    }
}
