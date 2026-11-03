using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditWorkflowTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    private Task<(HttpStatusCode Status, JsonNode? Body)> ApproveAsync(long editId, string userId, object? body = null) =>
        SendAsync(HttpMethod.Post, $"/api/metadata-edits/{editId}/approve", userId, body ?? new { });

    private Task<MetadataEdit> EditAsync(long id) => DbAsync(db => db.MetadataEdits.AsNoTracking().SingleAsync(e => e.Id == id));

    private Task<JsonNode> HistoryAsync(string viewer) => OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", viewer));

    [Fact]
    public async Task A_list_edit_from_a_stale_form_keeps_items_added_since()
    {
        await SubmitOkAsync(Users.Editor, new { aliases = new[] { "added meanwhile" }, sources = new[] { Source } });

        var result = await SubmitOkAsync(Users.PlusFan, new
        {
            aliases = new[] { "from the stale form" },
            @base = new { aliases = Array.Empty<string>() },
            sources = new[] { Source }
        });

        Ints(result["appliedFields"]).Should().Equal((int)MetadataField.Aliases);
        (await DeckAsync()).Titles.Where(t => t.TitleType == DeckTitleType.Alias).Select(t => t.Title)
            .Should().BeEquivalentTo("added meanwhile", "from the stale form");
        (await DbAsync(db => db.DeckMetadataExclusions.CountAsync())).Should().Be(0, "nothing the contributor saw was removed");
    }

    [Fact]
    public async Task A_scalar_changed_after_the_form_loaded_comes_back_as_a_conflict_with_the_new_value()
    {
        await SubmitOkAsync(Users.Editor, new { romajiTitle = "Kirakira", sources = new[] { Source } });

        var stale = await SendAsync(HttpMethod.Post, $"/api/media-deck/{DeckId}/metadata-edits", Users.PlusFan,
                                    new { romajiTitle = "Kira☆Kira", @base = new { romajiTitle = "Kira Kira" }, sources = new[] { Source } });

        stale.Status.Should().Be(HttpStatusCode.Conflict);
        stale.Body!["changedFields"]![0]!["field"]!.GetValue<int>().Should().Be((int)MetadataField.RomajiTitle);
        stale.Body["changedFields"]![0]!["current"]!.GetValue<string>().Should().Be("Kirakira");
        (await DeckAsync()).RomajiTitle.Should().Be("Kirakira");

        await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Kira☆Kira", @base = new { romajiTitle = "Kirakira" }, sources = new[] { Source } });
        (await DeckAsync()).RomajiTitle.Should().Be("Kira☆Kira");
    }

    [Fact]
    public async Task A_second_approval_of_the_same_edit_is_refused_and_notifies_once()
    {
        var pending = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));

        await OkAsync(ApproveAsync(pending, Users.Editor));
        (await ErrorAsync(ApproveAsync(pending, Users.Admin), HttpStatusCode.Conflict)).Should().Be("This edit was already handled.");
        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/reject", Users.Admin, new { note = "late" }), HttpStatusCode.Conflict))
            .Should().Be("This edit was already handled.");

        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditApproved)).Should().Be(1);
        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditRejected)).Should().Be(0);
    }

    [Fact]
    public async Task Approving_part_of_an_edit_splits_the_rest_into_a_rejected_row_of_the_same_submission()
    {
        var edit = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", genres = new[] { (int)Genre.Action, (int)Genre.Comedy },
                                                                   sources = new[] { Source } }));

        (await ErrorAsync(ApproveAsync(edit, Users.Editor, new { excludeFields = new[] { (int)MetadataField.Genres } }), HttpStatusCode.BadRequest))
            .Should().Be("Say why the changes you left out weren't approved.");
        (await ErrorAsync(ApproveAsync(edit, Users.Editor, new { excludeFields = new[] { (int)MetadataField.Genres, (int)MetadataField.ReleaseDate }, excludeNote = "x" }),
                          HttpStatusCode.BadRequest)).Should().Be("Leave at least one change in, or reject the edit.");

        await OkAsync(ApproveAsync(edit, Users.Editor, new { excludeFields = new[] { (int)MetadataField.Genres }, excludeNote = "Not a comedy." }));

        var deck = await DeckAsync();
        deck.ReleaseDate.Should().Be(new DateOnly(2007, 3, 29));
        deck.DeckGenres.Select(g => g.Genre).Should().Equal(Genre.Action);

        var approved = await EditAsync(edit);
        approved.Status.Should().Be(MetadataEditStatus.Approved);
        approved.Fields.Should().Be(MetadataField.ReleaseDate);
        var split = await DbAsync(db => db.MetadataEdits.AsNoTracking().SingleAsync(e => e.SubmissionId == approved.SubmissionId && e.Id != edit));
        split.Status.Should().Be(MetadataEditStatus.Rejected);
        split.Fields.Should().Be(MetadataField.Genres);
        split.ReviewNote.Should().Be("Not a comedy.");

        var message = await DbAsync(db => db.Notifications.Where(n => n.UserId == Users.Author).Select(n => n.Message).SingleAsync());
        message.Should().EndWith("except the genres: Not a comedy.");
    }

    [Fact]
    public async Task A_doubt_can_name_the_changes_that_look_wrong()
    {
        var edit = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", genres = new[] { (int)Genre.Action, (int)Genre.Comedy },
                                                                   sources = new[] { Source } }));

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/metadata-edits/{edit}/check", Users.Checker,
                                new { verdict = 2, reason = 2, note = "Not a comedy", fields = new[] { (int)MetadataField.Genres } }));

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        queue["items"]![0]!["checks"]![0]!["fields"]!.GetValue<int>().Should().Be((int)MetadataField.Genres);
        var own = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Checker));
        own["items"]![0]!["myCheck"]!["fields"]!.GetValue<int>().Should().Be((int)MetadataField.Genres);
    }

    [Fact]
    public async Task Authors_can_withdraw_their_own_pending_edit_only()
    {
        var pending = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));

        var editable = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Author));
        editable["pending"]![0]!["id"]!.GetValue<long>().Should().Be(pending);
        editable["pending"]![0]!["isYours"]!.GetValue<bool>().Should().BeTrue();
        editable["pending"]![0]!["fields"]!.GetValue<int>().Should().Be((int)MetadataField.ReleaseDate);
        (await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Checker)))["pending"]![0]!["isYours"]!
            .GetValue<bool>().Should().BeFalse();

        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/withdraw", Users.Checker)).Status.Should().Be(HttpStatusCode.Forbidden);
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/withdraw", Users.Author));
        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/withdraw", Users.Author)).Status.Should().Be(HttpStatusCode.Conflict);

        (await EditAsync(pending)).Status.Should().Be(MetadataEditStatus.Withdrawn);
        (await HistoryAsync(Users.Checker))["items"]![0]!["publicStatus"]!.GetValue<string>().Should().Be("withdrawn");
        (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor)))["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task A_rejection_can_be_undone_by_its_reviewer_and_takes_the_notification_back()
    {
        var pending = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/reject", Users.Editor, new { note = "Wrong date." }));
        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditRejected)).Should().Be(1);

        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/reopen", Users.Admin)).Status.Should().Be(HttpStatusCode.Conflict);
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/reopen", Users.Editor));

        var edit = await EditAsync(pending);
        edit.Status.Should().Be(MetadataEditStatus.Pending);
        edit.ReviewNote.Should().BeNull();
        (await NotificationCountAsync(Users.Author, NotificationType.MetadataEditRejected)).Should().Be(0);

        await DbAsync(db => db.MetadataEdits.Where(e => e.Id == pending)
                              .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, MetadataEditStatus.Rejected)
                                                        .SetProperty(e => e.ReviewedByUserId, Users.Editor)
                                                        .SetProperty(e => e.ReviewedAt, DateTime.UtcNow.AddMinutes(-5))));
        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/reopen", Users.Editor), HttpStatusCode.Conflict))
            .Should().Be("This rejection can't be undone anymore.");
    }

    [Fact]
    public async Task Reverting_an_added_genre_leaves_providers_free_to_import_it_later()
    {
        var edit = EditId(await SubmitOkAsync(Users.Editor, new { genres = new[] { (int)Genre.Action, (int)Genre.Comedy }, sources = new[] { Source } }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{edit}/revert", Users.Admin));

        (await DeckAsync()).DeckGenres.Select(g => g.Genre).Should().Equal(Genre.Action);
        (await DbAsync(db => db.DeckMetadataExclusions.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task A_deck_over_the_alias_cap_can_still_lose_aliases_but_not_gain_them()
    {
        var aliases = Enumerable.Range(1, 17).Select(i => $"provider alias {i:D2}").ToList();
        await DbAsync(async db =>
        {
            db.DeckTitles.AddRange(aliases.Select(a => new DeckTitle { DeckId = DeckId, Title = a, TitleType = DeckTitleType.Alias }));
            await db.SaveChangesAsync();
        });

        await SubmitOkAsync(Users.PlusFan, new { aliases = aliases.Skip(1), sources = new[] { Source } });
        (await DeckAsync()).Titles.Count(t => t.TitleType == DeckTitleType.Alias).Should().Be(16);

        (await ErrorAsync(SubmitAsync(Users.PlusFan, new { aliases = aliases.Skip(1).Append("one more"), sources = new[] { Source } }), HttpStatusCode.BadRequest))
            .Should().Be("A deck can have at most 15 aliases. Remove one before adding another.");
    }

    [Fact]
    public async Task Approving_an_alias_edit_that_would_now_pass_the_cap_is_refused()
    {
        var aliases = Enumerable.Range(1, 14).Select(i => $"alias {i:D2}").ToList();
        await DbAsync(async db =>
        {
            db.DeckTitles.AddRange(aliases.Select(a => new DeckTitle { DeckId = DeckId, Title = a, TitleType = DeckTitleType.Alias }));
            await db.SaveChangesAsync();
        });

        var pending = EditId(await SubmitOkAsync(Users.Author, new { aliases = aliases.Append("from the author"), sources = new[] { Source } }));
        await SubmitOkAsync(Users.Editor, new { aliases = aliases.Append("from the editor"), @base = new { aliases }, sources = new[] { Source } });

        (await ErrorAsync(ApproveAsync(pending, Users.Admin), HttpStatusCode.BadRequest))
            .Should().Be("This deck now has 15 aliases, so this edit would go over the limit of 15. Leave the aliases out or reject it.");
        (await EditAsync(pending)).Status.Should().Be(MetadataEditStatus.Pending);
    }

    [Fact]
    public async Task Links_and_sources_lose_tracking_parameters()
    {
        var result = await SubmitOkAsync(Users.Editor, new
        {
            links = new object[]
            {
                new { linkType = (int)LinkType.Vndb, url = "https://vndb.org/v1474?utm_source=x&fbclid=abc" },
                new { linkType = (int)LinkType.Amazon, url = "https://www.amazon.co.jp/%E3%82%AD%E3%83%A9/dp/B000ABCDEF/ref=sr_1_1?tag=aff-22&keywords=x" },
                new { linkType = (int)LinkType.Web, url = "https://www.google.com/url?q=https://example.com/page%3Fid%3D3%26utm_medium%3Dsocial&sa=D" }
            },
            sources = new[] { "vndb.org/v1474?gclid=1" }
        });

        var urls = (await DbAsync(db => db.Set<Link>().Where(l => l.DeckId == DeckId).Select(l => l.Url).ToListAsync())).Order().ToList();
        urls.Should().Equal("https://example.com/page?id=3", "https://vndb.org/v1474", "https://www.amazon.co.jp/dp/B000ABCDEF");
        (await EditAsync(EditId(result))).SourcesJson.Should().Be("""["https://vndb.org/v1474"]""");
    }

    [Fact]
    public async Task Google_books_links_must_point_to_a_book_page()
    {
        (await ErrorAsync(SubmitAsync(Users.Editor, new { links = new[] { new { linkType = (int)LinkType.GoogleBooks, url = "https://www.google.com/search?q=x" } },
                                                         sources = new[] { Source } }), HttpStatusCode.BadRequest))
            .Should().Be("Google Books links must point to a book page.");
    }

    [Fact]
    public async Task A_reverted_mixed_submission_counts_once_toward_the_auto_block()
    {
        await OkAsync(SendAsync(HttpMethod.Put, "/api/admin/metadata-edits/settings", Users.Admin, new
        {
            establishedMinAccountDays = 30, establishedCountedEdits = 15, establishedNoRevertDays = 90, trustedCountedEdits = 50,
            trustedNoRevertDays = 180, establishedAutoApplyMaxTier = -1, trustedAutoApplyMaxTier = 0
        }));
        var result = await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Mixed", genres = new[] { (int)Genre.Action, (int)Genre.Drama }, sources = new[] { Source } });
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(result)}/revert", Users.Editor));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{EditId(result, 1)}/reject", Users.Editor, new { note = "No." }));
        await DbAsync(db => db.MetadataEdits.Where(e => e.Id == EditId(result, 1))
                              .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, MetadataEditStatus.Reverted)));

        var contributors = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors", Users.Admin));
        var plusFan = contributors["items"]!.AsArray().Single(c => c!["userId"]!.GetValue<string>() == Users.PlusFan)!;
        plusFan["reverted"]!.GetValue<int>().Should().Be(1);
        plusFan["levelReason"]!.GetValue<string>().Should().Be("Jiten+ Full");
    }

    [Fact]
    public async Task Reverted_edits_hide_their_values_from_everyone_but_the_author_and_reviewers()
    {
        var spam = EditId(await SubmitOkAsync(Users.PlusFan, new { aliases = new[] { "buy cheap" }, sources = new[] { "https://spam.example.com" } }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{spam}/revert", Users.Editor));

        var publicItem = (await HistoryAsync(Users.Checker))["items"]![0]!;
        publicItem["contentHidden"]!.GetValue<bool>().Should().BeTrue();
        publicItem["changes"]!.AsArray().Should().BeEmpty();
        publicItem["sources"]!.AsArray().Should().BeEmpty();

        (await HistoryAsync(Users.PlusFan))["items"]![0]!["changes"]!.AsArray().Should().NotBeEmpty();
        (await HistoryAsync(Users.Editor))["items"]![0]!["changes"]!.AsArray().Should().NotBeEmpty();

        var recent = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent", Users.Checker));
        recent["items"]![0]!["changes"]!.AsArray().Should().BeEmpty();
        recent["items"]![0]!["contentHidden"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Recent_changes_carry_the_values_that_changed()
    {
        await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } });

        var item = (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent", Users.Checker)))["items"]![0]!;
        item["changes"]![0]!["before"]!.GetValue<string>().Should().Be("2007-03-30");
        item["changes"]![0]!["after"]!.GetValue<string>().Should().Be("2007-03-29");
        item["isYours"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_name_check_needs_the_same_account_as_submitting()
    {
        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/media-deck/{DeckId}/character-names/check", Users.Unconfirmed, new { names = new[] { "前島 鹿之助" } }),
                          HttpStatusCode.BadRequest)).Should().Be("Confirm your email address before suggesting edits.");
        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/media-deck/{DeckId}/character-names/check", Users.NoName, new { names = new[] { "前島 鹿之助" } }),
                          HttpStatusCode.BadRequest)).Should().Be("Set a display name before suggesting edits.");
        (await OkAsync(SendAsync(HttpMethod.Post, $"/api/media-deck/{DeckId}/character-names/check", Users.Author, new { }))).AsArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Queue_name_checks_show_excerpts_to_everyone_and_counts_to_reviewers_only()
    {
        await SubmitOkAsync(Users.Author, new { addCharacterNames = new[] { "前島 鹿之助" }, sources = new[] { Source } });

        var contributorView = (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Checker)))["items"]![0]!["characterNameChecks"]!.AsArray();
        var full = contributorView.Single(s => s!["surface"]!.GetValue<string>() == "前島鹿之助")!;
        full["occurrences"].Should().BeNull();
        full["found"]!.GetValue<bool>().Should().BeTrue();
        full["examples"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain("前島鹿之助は部室に入った。");

        var editorView = (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor)))["items"]![0]!["characterNameChecks"]!.AsArray();
        editorView.Single(s => s!["surface"]!.GetValue<string>() == "前島鹿之助")!["occurrences"]!.GetValue<int>().Should().Be(3);
    }

    [Fact]
    public async Task Metrics_are_admin_only_and_count_the_queue()
    {
        await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } });
        var approved = EditId(await SubmitOkAsync(Users.Checker, new { romajiTitle = "Kira", sources = new[] { Source } }));
        await OkAsync(ApproveAsync(approved, Users.Editor));

        (await SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/metrics", Users.Editor)).Status.Should().Be(HttpStatusCode.Forbidden);
        var metrics = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/metrics", Users.Admin));
        metrics["pending"]!.GetValue<int>().Should().Be(1);
        metrics["approved30d"]!.GetValue<int>().Should().Be(1);
        metrics["submissions30d"]!.GetValue<int>().Should().Be(2);
        metrics["weeks"]!.AsArray().Should().HaveCount(4);
    }
}
