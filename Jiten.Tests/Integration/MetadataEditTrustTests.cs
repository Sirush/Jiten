using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Parser.Tests.Integration.Infrastructure;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditTrustTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    private static object AliasEdit(string alias) => new { aliases = new[] { alias }, sources = new[] { Source } };

    /// <summary>Writes submissions straight to the table so limits can be reached without hundreds of requests.</summary>
    private Task SeedSubmissionsAsync(string userId, int count, TimeSpan age, int rowsInFirstSubmission = 1) =>
        DbAsync(async db =>
        {
            var createdAt = DateTime.UtcNow - age;
            for (var i = 0; i < count; i++)
            {
                var submissionId = Guid.NewGuid();
                for (var row = 0; row < (i == 0 ? rowsInFirstSubmission : 1); row++)
                    db.MetadataEdits.Add(new MetadataEdit
                    {
                        DeckId = OtherDeckId, UserId = userId, SubmissionId = submissionId, Status = MetadataEditStatus.Pending,
                        Fields = MetadataField.Aliases, CreatedAt = createdAt
                    });
            }

            await db.SaveChangesAsync();
        });

    private Task SeedReviewedAsync(string userId, MetadataEditStatus status, DateTime createdAt, DateTime? reviewedAt = null) =>
        DbAsync(async db =>
        {
            db.MetadataEdits.Add(new MetadataEdit
            {
                DeckId = DeckId, UserId = userId, SubmissionId = Guid.NewGuid(), Status = status, Fields = MetadataField.ReleaseDate,
                CreatedAt = createdAt, ReviewedAt = reviewedAt ?? createdAt, ReviewedByUserId = Users.Editor
            });
            await db.SaveChangesAsync();
        });

    private async Task<JsonNode> ContributorAsync(string userId)
    {
        var page = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors?limit=100", Users.Admin));
        return page["items"]!.AsArray().Single(c => c!["userId"]!.GetValue<string>() == userId)!;
    }

    private async Task<string> LevelAsync(string userId) => (await ContributorAsync(userId))["level"]!.GetValue<string>();

    private Task<JsonNode> ViewerAsync(string userId) =>
        OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", userId)).ContinueWith(t => t.Result["viewer"]!);

    [Fact]
    public async Task New_contributors_get_fifteen_submissions_an_hour_counted_per_submission()
    {
        await SeedSubmissionsAsync(Users.Author, MetadataEditService.NewHourlyLimit - 1, TimeSpan.FromMinutes(10), rowsInFirstSubmission: 2);

        await SubmitOkAsync(Users.Author, AliasEdit("fifteenth"));

        (await ErrorAsync(SubmitAsync(Users.Author, AliasEdit("sixteenth")), HttpStatusCode.TooManyRequests))
            .Should().MatchRegex(@"^You've submitted a lot of edits recently\. Try again in \d+ minutes\.$");
    }

    [Fact]
    public async Task New_contributors_get_thirty_submissions_a_day()
    {
        await SeedSubmissionsAsync(Users.Author, MetadataEditService.NewDailyLimit, TimeSpan.FromHours(3));

        (await ErrorAsync(SubmitAsync(Users.Author, AliasEdit("over the day")), HttpStatusCode.TooManyRequests))
            .Should().MatchRegex(@"^You've submitted a lot of edits recently\. Try again in \d+ hours\.$");
    }

    [Fact]
    public async Task Trusted_contributors_get_a_hundred_an_hour_and_no_daily_cap()
    {
        await SeedSubmissionsAsync(Users.PlusFan, MetadataEditService.NewDailyLimit, TimeSpan.FromHours(3));
        await SeedSubmissionsAsync(Users.PlusFan, MetadataEditService.EstablishedHourlyLimit - 1, TimeSpan.FromMinutes(10));

        await SubmitOkAsync(Users.PlusFan, AliasEdit("hundredth"));

        await ErrorAsync(SubmitAsync(Users.PlusFan, AliasEdit("over the hour")), HttpStatusCode.TooManyRequests);
    }

    [Theory]
    [InlineData(Users.Editor)]
    [InlineData(Users.Admin)]
    public async Task Editors_and_admins_get_five_hundred_an_hour(string userId)
    {
        await SeedSubmissionsAsync(userId, MetadataEditService.EditorHourlyLimit - 1, TimeSpan.FromMinutes(10));

        await SubmitOkAsync(userId, AliasEdit("five hundredth"));

        await ErrorAsync(SubmitAsync(userId, AliasEdit("over the hour")), HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Checks_have_no_daily_limit()
    {
        await SeedSubmissionsAsync(Users.Author, 60, TimeSpan.FromMinutes(5));
        var editIds = await DbAsync(db => Task.FromResult(db.MetadataEdits.Select(e => e.Id).ToList()));

        foreach (var id in editIds)
            await OkAsync(SendAsync(HttpMethod.Put, $"/api/metadata-edits/{id}/check", Users.Checker, new { verdict = 1 }));
    }

    [Fact]
    public async Task One_revert_is_forgiven_per_ten_counted_edits_inside_the_window()
    {
        var now = DateTime.UtcNow;
        for (var day = 1; day <= 15; day++)
            await SeedReviewedAsync(Users.Author, MetadataEditStatus.Approved, now.AddDays(-day));
        (await LevelAsync(Users.Author)).Should().Be("established");

        await SeedReviewedAsync(Users.Author, MetadataEditStatus.Reverted, now.AddDays(-20), now.AddDays(-5));
        (await LevelAsync(Users.Author)).Should().Be("established");

        await SeedReviewedAsync(Users.Author, MetadataEditStatus.Reverted, now.AddDays(-200), now.AddDays(-100));
        (await LevelAsync(Users.Author)).Should().Be("established", "a revert outside the 90-day window doesn't count");

        await SeedReviewedAsync(Users.Author, MetadataEditStatus.Reverted, now.AddDays(-30), now.AddDays(-10));
        (await LevelAsync(Users.Author)).Should().Be("new");
    }

    [Fact]
    public async Task A_banned_contributor_cannot_submit_check_or_auto_apply_until_unbanned()
    {
        var pending = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.PlusFan}", Users.Admin, new { banned = true }));

        var viewer = await ViewerAsync(Users.PlusFan);
        viewer["banned"]!.GetValue<bool>().Should().BeTrue();

        (await ErrorAsync(SubmitAsync(Users.PlusFan, AliasEdit("banned")), HttpStatusCode.Forbidden)).Should().Be(MetadataEditService.BannedMessage);
        (await ErrorAsync(SendAsync(HttpMethod.Post, $"/api/media-deck/{DeckId}/character-names/check", Users.PlusFan, new { names = new[] { "前島 鹿之助" } }),
                          HttpStatusCode.Forbidden)).Should().Be(MetadataEditService.BannedMessage);
        (await ErrorAsync(SendAsync(HttpMethod.Put, $"/api/metadata-edits/{pending}/check", Users.PlusFan, new { verdict = 1 }), HttpStatusCode.BadRequest))
            .Should().Be(MetadataEditService.BannedMessage);

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.PlusFan));
        var item = queue["items"]![0]!;
        item["canCheck"]!.GetValue<bool>().Should().BeFalse();
        item["checkBlockedReason"]!.GetValue<string>().Should().Be(MetadataEditService.BannedMessage);

        var record = await ContributorAsync(Users.PlusFan);
        record["banned"]!.GetValue<bool>().Should().BeTrue();
        record["level"]!.GetValue<string>().Should().Be("new");
        (await DbAsync(db => Task.FromResult(db.MetadataEdits.Count(e => e.UserId == Users.PlusFan)))).Should().Be(0);

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.PlusFan}", Users.Admin, new { banned = false }));

        var restored = await ViewerAsync(Users.PlusFan);
        restored["banned"]!.GetValue<bool>().Should().BeFalse();
        (await LevelAsync(Users.PlusFan)).Should().Be("trusted");
        Ints((await SubmitOkAsync(Users.PlusFan, AliasEdit("unbanned")))["appliedFields"]).Should().Equal((int)MetadataField.Aliases);
        await OkAsync(SendAsync(HttpMethod.Put, $"/api/metadata-edits/{pending}/check", Users.PlusFan, new { verdict = 1 }));
        (await ContributorAsync(Users.PlusFan))["banned"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task A_banned_reviewer_loses_review_rights_too()
    {
        var pending = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));
        var live = EditId(await SubmitOkAsync(Users.PlusFan, AliasEdit("live")));

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Reviewer}", Users.Admin, new { banned = true }));

        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{pending}/approve", Users.Reviewer, new { })).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{live}/revert", Users.Reviewer)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Post, $"/api/metadata-edits/{live}/confirm-applied", Users.Reviewer)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Check_accuracy_is_tracked_against_outcomes_with_reverts_counting_as_rejected()
    {
        async Task<long> PendingAsync(object body) => EditId(await SubmitOkAsync(Users.Author, body));
        var confirmedApproved = await PendingAsync(new { releaseDate = "2007-03-29", sources = new[] { Source } });
        var confirmedRejected = await PendingAsync(new { englishTitle = "Wrong", sources = new[] { Source } });
        var doubtedApproved = await PendingAsync(new { romajiTitle = "Kirakira", sources = new[] { Source } });
        var doubtedReverted = await PendingAsync(new { aliases = new[] { "Kirakira" }, sources = new[] { Source } });
        var stillPending = await PendingAsync(new { genres = new[] { (int)Genre.Drama }, sources = new[] { Source } });

        Task CheckAsync(long id, int verdict) =>
            OkAsync(SendAsync(HttpMethod.Put, $"/api/metadata-edits/{id}/check", Users.Checker,
                              verdict == 1 ? new { verdict, reason = (int?)null, note = (string?)null } : new { verdict, reason = (int?)2, note = (string?)"Not this" }));
        await CheckAsync(confirmedApproved, 1);
        await CheckAsync(confirmedRejected, 1);
        await CheckAsync(doubtedApproved, 2);
        await CheckAsync(doubtedReverted, 2);
        await CheckAsync(stillPending, 1);

        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{confirmedApproved}/approve", Users.Editor, new { }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{confirmedRejected}/reject", Users.Editor, new { note = "No." }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{doubtedApproved}/approve", Users.Editor, new { }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{doubtedReverted}/approve", Users.Editor, new { }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{doubtedReverted}/revert", Users.Editor));

        var record = await ContributorAsync(Users.Checker);
        record["checks"]!.GetValue<int>().Should().Be(5);
        record["checksConfirmedApproved"]!.GetValue<int>().Should().Be(1);
        record["checksConfirmedRejected"]!.GetValue<int>().Should().Be(1);
        record["checksDoubtedApproved"]!.GetValue<int>().Should().Be(1);
        record["checksDoubtedRejected"]!.GetValue<int>().Should().Be(1);

        var queue = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.Editor));
        var check = queue["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == stillPending)!["checks"]![0]!;
        check["displayName"]!.GetValue<string>().Should().Be("nekomata");
        check["matched"]!.GetValue<int>().Should().Be(2);
        check["missed"]!.GetValue<int>().Should().Be(2);

        var asUser = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/pending", Users.PlusFan));
        asUser["items"]![0]!["checks"].Should().BeNull();
    }
}
