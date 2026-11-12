using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditReviewTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    private Task<(HttpStatusCode Status, JsonNode? Body)> CheckAsync(long editId, string userId, object body) =>
        SendAsync(HttpMethod.Put, $"/api/metadata-edits/{editId}/check", userId, body);

    private Task<(HttpStatusCode Status, JsonNode? Body)> ApproveAsync(long editId, string userId, object? body = null) =>
        SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/approve", userId, body ?? new { });

    private Task<(HttpStatusCode Status, JsonNode? Body)> RevertAsync(long editId, string userId) =>
        SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/revert", userId);

    private Task<MetadataEdit> EditAsync(long id) => DbAsync(db => db.MetadataEdits.AsNoTracking().SingleAsync(e => e.Id == id));

    private async Task<long> PendingDateEditAsync(string author = Users.Author) =>
        EditId(await SubmitOkAsync(author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));

    private async Task<long> PendingRelationshipEditAsync() =>
        EditId(await SubmitOkAsync(Users.Author, new
        {
            relationships = new[] { new { sourceDeckId = DeckId, targetDeckId = OtherDeckId, relationshipType = (int)DeckRelationshipType.Sequel } },
            sources = new[] { Source }
        }));

    private async Task<JsonNode> QueueItemAsync(string viewerId, long editId, string query = "")
    {
        var queue = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/pending{query}", viewerId));
        return queue["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == editId)!;
    }

    [Fact]
    public async Task Checks_need_an_established_account_that_is_not_the_author_and_a_reason_to_doubt()
    {
        var editId = await PendingDateEditAsync();

        var confirmed = await OkAsync(CheckAsync(editId, Users.Checker, new { verdict = 1, sourceUrl = "https://vndb.org/r6037" }));
        confirmed["confirmCount"]!.GetValue<int>().Should().Be(1);
        (await EditAsync(editId)).SourcesJson.Should().Contain("r6037");

        (await ErrorAsync(CheckAsync(editId, Users.YoungChecker, new { verdict = 1 }), HttpStatusCode.BadRequest))
            .Should().Be("New accounts can check edits after their first week.");
        (await ErrorAsync(CheckAsync(editId, Users.Author, new { verdict = 1 }), HttpStatusCode.BadRequest))
            .Should().Be("You can't check your own edit.");
        (await ErrorAsync(CheckAsync(editId, Users.NoName, new { verdict = 1 }), HttpStatusCode.BadRequest))
            .Should().Be("Set a display name before checking edits.");
        (await ErrorAsync(CheckAsync(editId, Users.PlusFan, new { verdict = 2, note = "  " }), HttpStatusCode.BadRequest))
            .Should().Be("Say what looks wrong so editors can check it.");

        (await OkAsync(CheckAsync(editId, Users.PlusFan, new { verdict = 2, note = "Wrong edition" })))["confirmCount"]!
            .GetValue<int>().Should().Be(1);
        (await OkAsync(CheckAsync(editId, Users.PlusFan, new { verdict = 1 })))["confirmCount"]!.GetValue<int>().Should().Be(2);

        (await SendAsync(HttpMethod.Delete, $"/api/metadata-edits/{editId}/check", Users.PlusFan)).Status.Should().Be(HttpStatusCode.NoContent);
        (await DbAsync(db => db.MetadataEditChecks.CountAsync())).Should().Be(1);
        (await SendAsync(HttpMethod.Delete, $"/api/metadata-edits/{editId}/check", Users.PlusFan)).Status.Should().Be(HttpStatusCode.NotFound);

        await OkAsync(ApproveAsync(editId, Users.Reviewer));
        (await ErrorAsync(CheckAsync(editId, Users.PlusFan, new { verdict = 1 }), HttpStatusCode.BadRequest)).Should().Be("This edit was already handled.");
        (await SendAsync(HttpMethod.Delete, $"/api/metadata-edits/{editId}/check", Users.Checker)).Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task The_queue_hides_reviewer_data_from_plain_users()
    {
        var editId = await PendingRelationshipEditAsync();
        await OkAsync(CheckAsync(editId, Users.Checker, new { verdict = 2, note = "Different series" }));

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.PlusFan));
        queue["viewerRole"]!.GetValue<string>().Should().Be("user");
        var item = queue["items"]![0]!;
        item["id"]!.GetValue<long>().Should().Be(editId);
        item["author"]!["displayName"]!.GetValue<string>().Should().Be("tsukimi");
        item["author"]!["isYou"]!.GetValue<bool>().Should().BeFalse();
        item["canCheck"]!.GetValue<bool>().Should().BeTrue();
        item["canApprove"]!.GetValue<bool>().Should().BeFalse();
        foreach (var redacted in new[] { "tier", "authorRecord", "checks", "doubtCount", "conflicts" })
            item[redacted].Should().BeNull(redacted);

        var own = await QueueItemAsync(Users.Author, editId);
        own["author"]!["isYou"]!.GetValue<bool>().Should().BeTrue();
        own["canCheck"]!.GetValue<bool>().Should().BeFalse();
        own["checkBlockedReason"]!.GetValue<string>().Should().Be("You can't check your own edit.");

        var asEditor = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        asEditor["viewerRole"]!.GetValue<string>().Should().Be("editor");
        var full = asEditor["items"]![0]!;
        full["tier"]!.GetValue<int>().Should().Be((int)MetadataRiskTier.Medium);
        full["authorRecord"]!["level"]!.GetValue<string>().Should().Be("new");
        full["doubtCount"]!.GetValue<int>().Should().Be(1);
        full["checks"]![0]!["displayName"]!.GetValue<string>().Should().Be("nekomata");
        full["checks"]![0]!["note"]!.GetValue<string>().Should().Be("Different series");
        full["conflicts"]!.AsArray().Should().BeEmpty();
        full["canApprove"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task The_queue_explains_franchise_merges_and_shows_name_checks()
    {
        var relationshipEdit = await PendingRelationshipEditAsync();
        var namesEdit = EditId(await SubmitOkAsync(Users.Author, new { addCharacterNames = new[] { "前島 鹿之助" }, sources = new[] { Source } }));

        var relationshipItem = await QueueItemAsync(Users.Checker, relationshipEdit);
        var effect = relationshipItem["franchiseEffect"]!;
        effect["into"].Should().BeNull();
        effect["joining"]![0]!["franchiseId"]!.GetValue<int>().Should().Be(OtherFranchiseId);
        effect["joining"]![0]!["originalTitle"]!.GetValue<string>().Should().Be("Other Franchise");
        effect["joining"]![0]!["deckCount"]!.GetValue<int>().Should().Be(1);

        var added = relationshipItem["changes"]![0]!["after"]![0]!;
        added["otherDeckId"]!.GetValue<int>().Should().Be(OtherDeckId);
        added["otherDeckMediaType"]!.GetValue<int>().Should().Be((int)MediaType.Novel);
        added["otherDeckReleaseYear"].Should().BeNull();

        var namesItem = await QueueItemAsync(Users.Checker, namesEdit);
        namesItem["characterNameChecks"]!.AsArray().Select(s => (s!["surface"]!.GetValue<string>(), s["status"]!.GetValue<string>()))
            .Should().BeEquivalentTo([("前島", "ok"), ("前島鹿之助", "ok"), ("鹿之助", "ok")]);
    }

    [Fact]
    public async Task Queue_sort_and_tier_filters()
    {
        var dateEdit = await PendingDateEditAsync();
        var relationshipEdit = await PendingRelationshipEditAsync();
        await OkAsync(CheckAsync(dateEdit, Users.Checker, new { verdict = 1 }));

        var newest = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Checker));
        newest["total"]!.GetValue<int>().Should().Be(2);
        newest["items"]![0]!["id"]!.GetValue<long>().Should().Be(relationshipEdit);

        var confirmed = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?sort=confirmed", Users.Checker));
        confirmed["items"]![0]!["id"]!.GetValue<long>().Should().Be(dateEdit);

        var lowForReviewer = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?tier=0", Users.Reviewer));
        lowForReviewer["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(dateEdit);

        var tierIgnoredForUser = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?tier=0", Users.Checker));
        tierIgnoredForUser["total"]!.GetValue<int>().Should().Be(2);

        var byDeck = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/pending?deckId={OtherDeckId}", Users.Checker));
        byDeck["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Unread_approvals_fold_into_one_notification()
    {
        var first = await PendingDateEditAsync();
        var second = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }, OtherDeckId));
        var third = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }, ThirdDeckId));

        await OkAsync(ApproveAsync(first, Users.Editor));
        var single = await DbAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.UserId == Users.Author));
        single.Title.Should().Be("Your edit was approved");
        single.LinkUrl.Should().Be($"/decks/media/{DeckId}/history");

        await OkAsync(ApproveAsync(second, Users.Editor));
        var folded = await DbAsync(db => db.Notifications.AsNoTracking().SingleAsync(n => n.UserId == Users.Author));
        folded.Id.Should().Be(single.Id);
        folded.GroupCount.Should().Be(2);
        folded.Message.Should().Be("2 of your edits are now live.");
        folded.LinkUrl.Should().Be("/requests?tab=contributions");

        await DbAsync(db => db.Notifications.Where(n => n.UserId == Users.Author).ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true)));
        await OkAsync(ApproveAsync(third, Users.Editor));
        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditApproved)).Should().Be(2);
    }

    [Fact]
    public async Task Authors_whose_edits_go_live_without_review_get_no_approval_notification()
    {
        var edit = EditId(await SubmitOkAsync(Users.PlusFan, new { genres = new[] { (int)Genre.Action, (int)Genre.Drama }, sources = new[] { Source } }));
        (await EditAsync(edit)).Status.Should().Be(MetadataEditStatus.Pending);

        await OkAsync(ApproveAsync(edit, Users.Editor));

        (await NotificationCountAsync(Users.PlusFan, NotificationType.MetadataEditApproved)).Should().Be(0);
    }

    [Fact]
    public async Task Muted_edit_notifications_are_not_sent()
    {
        await OkAsync(SendAsync(HttpMethod.Put, $"/api/user/settings/notifications/{(int)NotificationGroup.MetadataEdits}", Users.Author, new { enabled = false }));
        var edit = await PendingDateEditAsync();

        await OkAsync(ApproveAsync(edit, Users.Editor));

        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditApproved)).Should().Be(0);
    }

    [Fact]
    public async Task Reviewers_approve_low_risk_edits_only_and_editors_approve_everything()
    {
        var dateEdit = await PendingDateEditAsync();
        var genresEdit = EditId(await SubmitOkAsync(Users.Author, new { genres = new[] { (int)Genre.Action, (int)Genre.Drama }, sources = new[] { Source } }));

        (await ApproveAsync(genresEdit, Users.Reviewer)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await ApproveAsync(dateEdit, Users.Checker)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await QueueItemAsync(Users.Reviewer, dateEdit))["canApprove"]!.GetValue<bool>().Should().BeTrue();
        (await QueueItemAsync(Users.Reviewer, genresEdit))["canApprove"]!.GetValue<bool>().Should().BeFalse();

        (await OkAsync(ApproveAsync(dateEdit, Users.Reviewer)))["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Approved);
        var deck = await DeckAsync();
        deck.ReleaseDate.Should().Be(new DateOnly(2007, 3, 29));
        deck.MetadataLocks.Should().HaveFlag(MetadataField.ReleaseDate);
        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditApproved)).Should().Be(1);

        var approved = await EditAsync(dateEdit);
        approved.ReviewedByUserId.Should().Be(Users.Reviewer);
        approved.AppliedAt.Should().NotBeNull();

        await OkAsync(ApproveAsync(genresEdit, Users.Editor));
        (await DeckAsync()).DeckGenres.Select(g => g.Genre).Should().BeEquivalentTo([Genre.Action, Genre.Drama]);

        (await ErrorAsync(ApproveAsync(genresEdit, Users.Editor), HttpStatusCode.Conflict)).Should().Be("This edit was already handled.");
        (await ApproveAsync(123456, Users.Editor)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approving_counts_toward_the_authors_record()
    {
        var dateEdit = await PendingDateEditAsync();
        await OkAsync(ApproveAsync(dateEdit, Users.Reviewer));

        var queueEdit = EditId(await SubmitOkAsync(Users.Author, new { englishTitle = "Kira Kira!", sources = new[] { Source } }));
        var item = await QueueItemAsync(Users.Editor, queueEdit);
        item["authorRecord"]!["accepted"]!.GetValue<int>().Should().Be(1);
        item["authorRecord"]!["reverted"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Rejecting_needs_a_reason_and_notifies_the_author()
    {
        var editId = await PendingRelationshipEditAsync();

        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/reject", Users.Editor, new { note = " " }), HttpStatusCode.BadRequest))
            .Should().Be("Say why the edit was rejected.");
        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/reject", Users.Reviewer, new { note = "No" })).Status
            .Should().Be(HttpStatusCode.Forbidden);

        var result = await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/reject", Users.Editor,
                                             new { note = "Same publisher, not the same story." }));
        result["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Rejected);

        var edit = await EditAsync(editId);
        edit.ReviewNote.Should().Be("Same publisher, not the same story.");
        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditRejected)).Should().Be(1);
        (await DbAsync(db => db.DeckRelationships.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Approving_names_without_an_excluded_surface_adds_only_the_rest()
    {
        var editId = EditId(await SubmitOkAsync(Users.Author, new { addCharacterNames = new[] { "前島 鹿之助" }, sources = new[] { Source } }));

        await OkAsync(ApproveAsync(editId, Users.Editor, new
        {
            excludeItems = new[] { new { field = (int)MetadataField.CharacterNames, keys = new[] { "鹿之助" } } },
            excludeNote = "Too common a word."
        }));

        (await DeckAsync()).DictionaryEntries.Select(e => e.Surface).Should().BeEquivalentTo(["前島鹿之助", "前島"]);
        var approved = await EditAsync(editId);
        var stored = MetadataEditService.ParseChanges(approved.ChangesJson).Single();
        stored.After.Deserialize<List<string>>().Should().NotContain("鹿之助");
        var rejected = await DbAsync(db => db.MetadataEdits.AsNoTracking().SingleAsync(e => e.SubmissionId == approved.SubmissionId && e.Id != editId));
        rejected.Status.Should().Be(MetadataEditStatus.Rejected);
        MetadataEditService.ParseChanges(rejected.ChangesJson).Single().After.Deserialize<List<string>>().Should().Equal("鹿之助");
    }

    [Fact]
    public async Task The_name_preview_is_for_reviewers_and_only_for_name_edits()
    {
        var namesEdit = EditId(await SubmitOkAsync(Users.Author, new { addCharacterNames = new[] { "前島 鹿之助" }, sources = new[] { Source } }));
        var dateEdit = await PendingDateEditAsync();

        (await SendAsync(HttpMethod.Get, $"/api/metadata-edits/{namesEdit}/name-preview", Users.Checker)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, $"/api/metadata-edits/{dateEdit}/name-preview", Users.Editor)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact(Skip = "The preview runs the real parser (Sudachi, full JMDict, Redis), which the SQLite test host doesn't have; covered by the live E2E script.")]
    public Task The_name_preview_counts_lines_whose_segmentation_changes() => Task.CompletedTask;

    [Fact]
    public async Task A_conflicting_edit_needs_a_resolution_and_keeping_current_supersedes_it()
    {
        var authorEdit = EditId(await SubmitOkAsync(Users.Author, new { romajiTitle = "Kira Kira (tsukimi)", sources = new[] { Source } }));
        var plusResult = await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Kirakira", sources = new[] { Source } });
        Ints(plusResult["appliedFields"]).Should().Equal((int)MetadataField.RomajiTitle);

        var (status, body) = await ApproveAsync(authorEdit, Users.Editor);
        status.Should().Be(HttpStatusCode.Conflict);
        var conflict = body!["conflicts"]![0]!;
        conflict["field"]!.GetValue<int>().Should().Be((int)MetadataField.RomajiTitle);
        conflict["editBefore"]!.GetValue<string>().Should().Be("Kira Kira");
        conflict["current"]!.GetValue<string>().Should().Be("Kirakira");
        conflict["editAfter"]!.GetValue<string>().Should().Be("Kira Kira (tsukimi)");

        var queued = await QueueItemAsync(Users.Editor, authorEdit);
        queued["conflicts"]!.AsArray().Should().ContainSingle();

        var kept = await OkAsync(ApproveAsync(authorEdit, Users.Editor, new { resolutions = new[] { new { field = (int)MetadataField.RomajiTitle, keep = "current" } } }));
        kept["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Superseded);
        (await DeckAsync()).RomajiTitle.Should().Be("Kirakira");
    }

    [Fact]
    public async Task Resolving_a_conflict_with_the_edits_value_applies_it()
    {
        var authorEdit = EditId(await SubmitOkAsync(Users.Author, new { romajiTitle = "Kira Kira (tsukimi)", sources = new[] { Source } }));
        await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Kirakira", sources = new[] { Source } });

        var applied = await OkAsync(ApproveAsync(authorEdit, Users.Editor, new { resolutions = new[] { new { field = (int)MetadataField.RomajiTitle, keep = "edit" } } }));

        applied["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Approved);
        (await DeckAsync()).RomajiTitle.Should().Be("Kira Kira (tsukimi)");
    }

    [Fact]
    public async Task A_pending_edit_is_superseded_once_its_values_are_live()
    {
        var authorEdit = EditId(await SubmitOkAsync(Users.Author, new { englishTitle = "KiraKira", sources = new[] { Source } }));

        await SubmitOkAsync(Users.Editor, new { englishTitle = "KiraKira", sources = new[] { Source } });

        (await EditAsync(authorEdit)).Status.Should().Be(MetadataEditStatus.Superseded);
    }

    [Fact]
    public async Task Reverting_restores_the_value_notifies_the_author_and_blocks_re_editing_the_field()
    {
        var liveEdit = EditId(await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Kirakira", sources = new[] { Source } }));

        (await RevertAsync(liveEdit, Users.Checker)).Status.Should().Be(HttpStatusCode.Forbidden);

        var result = await OkAsync(RevertAsync(liveEdit, Users.Reviewer));
        Ints(result["revertedFields"]).Should().Equal((int)MetadataField.RomajiTitle);
        Ints(result["skippedFields"]).Should().BeEmpty();

        (await DeckAsync()).RomajiTitle.Should().Be("Kira Kira");
        var reverted = await EditAsync(liveEdit);
        reverted.Status.Should().Be(MetadataEditStatus.Reverted);
        reverted.ReviewedByUserId.Should().Be(Users.Reviewer);
        (await NotificationCountAsync(Users.PlusFan, NotificationType.MetadataEditReverted)).Should().Be(1);

        var revertRow = await DbAsync(db => db.MetadataEdits.SingleAsync(e => e.RevertsEditId == liveEdit));
        revertRow.Status.Should().Be(MetadataEditStatus.Approved);
        revertRow.UserId.Should().Be(Users.Reviewer);

        (await ErrorAsync(SubmitAsync(Users.PlusFan, new { romajiTitle = "Kirakira again", sources = new[] { Source } }), HttpStatusCode.BadRequest))
            .Should().Be("Your recent change to romaji title on this deck was reverted, so you can't edit it again for a few days.");
        await SubmitOkAsync(Users.PlusFan, new { englishTitle = "Other field is fine", sources = new[] { Source } });
        await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Other deck is fine", sources = new[] { Source } }, OtherDeckId);

        (await ErrorAsync(RevertAsync(liveEdit, Users.Reviewer), HttpStatusCode.Conflict)).Should().Be("Only live edits can be reverted.");
    }

    [Fact]
    public async Task A_partial_revert_skips_fields_changed_since_and_says_so()
    {
        var reviewerEdit = await SubmitOkAsync(Users.Reviewer, new { englishTitle = "KiraKira", romajiTitle = "Kira Kira R", sources = new[] { Source } });
        Ints(reviewerEdit["appliedFields"]).Should().BeEquivalentTo([(int)MetadataField.RomajiTitle, (int)MetadataField.EnglishTitle]);
        var twoFieldEdit = EditId(reviewerEdit);

        var editorEdit = await SubmitOkAsync(Users.Editor, new { englishTitle = "Kira Kira (EN)", sources = new[] { Source } });
        Ints(editorEdit["appliedFields"]).Should().Equal((int)MetadataField.EnglishTitle);

        var result = await OkAsync(RevertAsync(twoFieldEdit, Users.Editor));
        Ints(result["revertedFields"]).Should().Equal((int)MetadataField.RomajiTitle);
        Ints(result["skippedFields"]).Should().Equal((int)MetadataField.EnglishTitle);

        var deck = await DeckAsync();
        deck.RomajiTitle.Should().Be("Kira Kira");
        deck.EnglishTitle.Should().Be("Kira Kira (EN)");
        (await EditAsync(twoFieldEdit)).ReviewNote.Should()
            .Be("Romaji title restored. English title left alone because it had been changed again since.");

        var history = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.NoName));
        var item = history["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == twoFieldEdit)!;
        item["publicStatus"]!.GetValue<string>().Should().Be("reverted");
        Ints(item["revertedFields"]).Should().Equal((int)MetadataField.RomajiTitle);
        Ints(item["skippedFields"]).Should().Equal((int)MetadataField.EnglishTitle);
        item["reviewNote"].Should().BeNull();
        item["contentHidden"]!.GetValue<bool>().Should().BeTrue();

        var own = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.Reviewer));
        own["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == twoFieldEdit)!["reviewNote"]!.GetValue<string>()
            .Should().StartWith("Romaji title restored.");
    }

    [Fact]
    public async Task A_partial_revert_only_blocks_re_editing_the_fields_it_restored()
    {
        var twoFieldEdit = EditId(await SubmitOkAsync(Users.Reviewer, new { englishTitle = "KiraKira", romajiTitle = "Kira Kira R", sources = new[] { Source } }));
        await SubmitOkAsync(Users.Editor, new { englishTitle = "Kira Kira (EN)", sources = new[] { Source } });
        await OkAsync(RevertAsync(twoFieldEdit, Users.Editor));

        (await ErrorAsync(SubmitAsync(Users.Reviewer, new { romajiTitle = "Kira Kira R2", sources = new[] { Source } }), HttpStatusCode.BadRequest))
            .Should().Contain("romaji title");
        await SubmitOkAsync(Users.Reviewer, new { englishTitle = "Kira Kira (EN2)", sources = new[] { Source } });
    }

    [Fact]
    public async Task Reverting_an_edit_whose_every_field_changed_again_is_refused()
    {
        var plusEdit = EditId(await SubmitOkAsync(Users.PlusFan, new { englishTitle = "KiraKira", sources = new[] { Source } }));
        await SubmitOkAsync(Users.Editor, new { englishTitle = "Kira Kira (EN)", sources = new[] { Source } });

        (await ErrorAsync(RevertAsync(plusEdit, Users.Editor), HttpStatusCode.Conflict))
            .Should().Be("Every field in this edit was changed again since, so there's nothing to revert.");
    }

    [Fact]
    public async Task Confirming_an_applied_edit_marks_it_approved()
    {
        var plusEdit = EditId(await SubmitOkAsync(Users.PlusFan, new { englishTitle = "KiraKira", sources = new[] { Source } }));
        var reviewerEdit = EditId(await SubmitOkAsync(Users.Reviewer, new { romajiTitle = "Kira Kira R", sources = new[] { Source } }));

        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{plusEdit}/confirm-applied", Users.Checker)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{reviewerEdit}/confirm-applied", Users.Reviewer)).Status.Should().Be(HttpStatusCode.Forbidden);

        var result = await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{plusEdit}/confirm-applied", Users.Reviewer));
        result["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Approved);
        (await EditAsync(plusEdit)).ReviewedByUserId.Should().Be(Users.Reviewer);

        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{plusEdit}/confirm-applied", Users.Reviewer)).Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Revert_all_cleans_up_a_vandal_who_is_auto_blocked_at_five_reverts_until_an_admin_clears_it()
    {
        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Vandal}", Users.Admin, new { adminTrusted = true }));

        async Task<bool> AutoAppliesAsync()
        {
            var contributors = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors", Users.Admin));
            return contributors["items"]!.AsArray().Single(c => c!["userId"]!.GetValue<string>() == Users.Vandal)!["level"]!.GetValue<string>() == "trusted";
        }
        Task<JsonNode> RevertAllAsync() =>
            OkAsync(SendAsync(HttpMethod.Post, $"/api/admin/metadata-edits/contributors/{Users.Vandal}/revert-all", Users.Admin));

        var i = 0;
        foreach (var deckId in new[] { DeckId, OtherDeckId, ThirdDeckId })
        {
            var result = await SubmitOkAsync(Users.Vandal, new { aliases = new[] { $"spam alias {i++}" }, sources = new[] { "https://spam.example.com" } }, deckId);
            Ints(result["appliedFields"]).Should().Equal((int)MetadataField.Aliases);
        }

        var pendingSpam = EditId(await SubmitOkAsync(Users.Vandal, new { description = "Spam.", note = "Spam" }));

        (await SendAsync(HttpMethod.Post, $"/api/admin/metadata-edits/contributors/{Users.Vandal}/revert-all", Users.Editor)).Status
            .Should().Be(HttpStatusCode.Forbidden);
        var cleanup = await RevertAllAsync();
        cleanup["reverted"]!.GetValue<int>().Should().Be(3);
        cleanup["partial"]!.GetValue<int>().Should().Be(0);

        (await DbAsync(db => db.DeckTitles.CountAsync(t => t.Title.StartsWith("spam alias")))).Should().Be(0);
        var pending = await EditAsync(pendingSpam);
        pending.Status.Should().Be(MetadataEditStatus.Rejected);
        pending.ReviewNote.Should().Be("Removed during a cleanup.");
        (await NotificationCountAsync(Users.Vandal, NotificationType.MetadataEditReverted)).Should().Be(0);
        (await AutoAppliesAsync()).Should().BeTrue("three reverts stay under the auto-block");

        foreach (var deckId in new[] { DeckId, OtherDeckId })
        {
            var result = await SubmitOkAsync(Users.Vandal, new { englishTitle = $"spam title {deckId}", sources = new[] { "https://spam.example.com" } }, deckId);
            Ints(result["appliedFields"]).Should().Equal((int)MetadataField.EnglishTitle);
        }

        (await RevertAllAsync())["reverted"]!.GetValue<int>().Should().Be(2);
        (await DeckAsync()).EnglishTitle.Should().Be("Kira Kira");

        (await AutoAppliesAsync()).Should().BeFalse();
        var blocked = await SubmitOkAsync(Users.Vandal, new { englishTitle = "spam", sources = new[] { "https://spam.example.com" } }, ThirdDeckId);
        Ints(blocked["pendingFields"]).Should().Equal((int)MetadataField.EnglishTitle);

        var contributors = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors", Users.Admin));
        var vandal = contributors["items"]!.AsArray().Single(c => c!["userId"]!.GetValue<string>() == Users.Vandal)!;
        vandal["blocked"]!.GetValue<bool>().Should().BeTrue();
        vandal["reverted"]!.GetValue<int>().Should().Be(MetadataTrustService.AutoBlockReverts);

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Vandal}", Users.Admin, new { clearBlock = true }));
        (await AutoAppliesAsync()).Should().BeTrue();
    }

    private sealed record UnreviewedSeed(long PlusLive, long ReviewerLive, long Pending);

    private async Task<UnreviewedSeed> SeedUnreviewedAsync()
    {
        var plusLive = EditId(await SubmitOkAsync(Users.PlusFan, new { englishTitle = "Kira☆Kira", sources = new[] { Source } }));
        var reviewerLive = EditId(await SubmitOkAsync(Users.Reviewer, new { romajiTitle = "Kira Kira R", sources = new[] { Source } }));
        var pending = await PendingDateEditAsync();
        await SubmitOkAsync(Users.Editor, new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });

        var reverted = EditId(await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Kirakira", sources = new[] { Source } }, OtherDeckId));
        await OkAsync(RevertAsync(reverted, Users.Editor));

        await DbAsync(async db =>
        {
            db.MetadataEdits.Add(new MetadataEdit
            {
                DeckId = DeckId, Actor = MetadataEditActor.Provider, Provider = LinkType.Vndb, SubmissionId = Guid.NewGuid(),
                Status = MetadataEditStatus.Applied, Fields = MetadataField.Aliases
            });
            await db.SaveChangesAsync();
        });

        return new UnreviewedSeed(plusLive, reviewerLive, pending);
    }

    [Fact]
    public async Task The_unreviewed_queue_lists_live_unreviewed_user_edits_for_editors()
    {
        var seed = await SeedUnreviewedAsync();

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?state=unreviewed", Users.Editor));

        queue["total"]!.GetValue<int>().Should().Be(2);
        var items = queue["items"]!.AsArray();
        items.Select(i => i!["id"]!.GetValue<long>()).Should().BeEquivalentTo([seed.PlusLive, seed.ReviewerLive]);
        foreach (var item in items)
        {
            item!["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Applied);
            item["canRevert"]!.GetValue<bool>().Should().BeTrue();
            item["canMarkReviewed"]!.GetValue<bool>().Should().BeTrue();
            item["canApprove"]!.GetValue<bool>().Should().BeFalse();
            item["canCheck"]!.GetValue<bool>().Should().BeFalse();
            item["conflicts"].Should().BeNull();
        }

        var pendingQueue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        pendingQueue["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(seed.Pending);
        pendingQueue["items"]![0]!["canMarkReviewed"]!.GetValue<bool>().Should().BeFalse();
        pendingQueue["items"]![0]!["canRevert"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_reviewer_cannot_mark_their_own_live_edit_reviewed()
    {
        var seed = await SeedUnreviewedAsync();

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?state=unreviewed", Users.Reviewer));

        var items = queue["items"]!.AsArray().ToDictionary(i => i!["id"]!.GetValue<long>());
        items[seed.ReviewerLive]!["canMarkReviewed"]!.GetValue<bool>().Should().BeFalse();
        items[seed.ReviewerLive]!["author"]!["isYou"]!.GetValue<bool>().Should().BeTrue();
        items[seed.PlusLive]!["canMarkReviewed"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Plain_users_asking_for_unreviewed_get_the_pending_queue()
    {
        var seed = await SeedUnreviewedAsync();

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?state=unreviewed", Users.Checker));

        queue["viewerRole"]!.GetValue<string>().Should().Be("user");
        queue["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(seed.Pending);
        var item = queue["items"]![0]!;
        item["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Pending);
        item["canMarkReviewed"]!.GetValue<bool>().Should().BeFalse();
        item["canRevert"]!.GetValue<bool>().Should().BeFalse();
        item["canCheck"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Marking_an_edit_reviewed_or_reverting_it_removes_it_from_the_unreviewed_queue()
    {
        var seed = await SeedUnreviewedAsync();

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{seed.PlusLive}/confirm-applied", Users.Editor));
        await OkAsync(RevertAsync(seed.ReviewerLive, Users.Editor));

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending?state=unreviewed", Users.Editor));
        queue["total"]!.GetValue<int>().Should().Be(0);
    }
}
