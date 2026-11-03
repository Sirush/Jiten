using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Jiten.Api.Services.MetadataEdits;
using Jiten.Core.Data;
using Jiten.Core.Data.MetadataEdits;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Parser.Tests.Integration;

public class MetadataEditHistoryTests(JitenWebApplicationFactory factory)
    : MetadataEditTestBase(factory), IClassFixture<JitenWebApplicationFactory>
{
    private sealed record Seeded(long Pending, long Applied, long Approved, long Rejected, long Reverted, long Superseded, long ByEditor);

    private async Task<Seeded> SeedEveryStatusAsync()
    {
        var pending = EditId(await SubmitOkAsync(Users.Author, new { aliases = new[] { "きらきら☆" }, sources = new[] { Source } }));
        var applied = EditId(await SubmitOkAsync(Users.PlusFan, new { englishTitle = "Kira☆Kira", sources = new[] { Source } }));

        var approved = EditId(await SubmitOkAsync(Users.Author, new { releaseDate = "2007-03-29", sources = new[] { Source } }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{approved}/approve", Users.Reviewer, new { }));

        var rejected = EditId(await SubmitOkAsync(Users.Author, new { genres = new[] { (int)Genre.Drama }, sources = new[] { Source } }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{rejected}/reject", Users.Editor, new { note = "Not a drama." }));

        var reverted = EditId(await SubmitOkAsync(Users.PlusFan, new { romajiTitle = "Kirakira", sources = new[] { Source } }));
        await OkAsync(SendAsync(HttpMethod.Post, $"/api/metadata-edits/{reverted}/revert", Users.Reviewer));

        var superseded = EditId(await SubmitOkAsync(Users.Author, new { romajiTitle = "Kira Kira R", sources = new[] { Source } }));
        var byEditor = EditId(await SubmitOkAsync(Users.Editor, new { romajiTitle = "Kira Kira R", sources = new[] { Source } }));

        return new Seeded(pending, applied, approved, rejected, reverted, superseded, byEditor);
    }

    private static JsonNode Item(JsonNode page, long id) => page["items"]!.AsArray().Single(i => i!["id"]!.GetValue<long>() == id)!;

    [Fact]
    public async Task Public_history_maps_statuses_and_hides_reviewers()
    {
        var seeded = await SeedEveryStatusAsync();

        var history = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.NoName));

        history["viewerRole"]!.GetValue<string>().Should().Be("user");
        history["total"]!.GetValue<int>().Should().Be(7);
        history["items"]!.AsArray().Should().OnlyContain(i => i!["status"] == null && i["reviewer"] == null && !i["canRevert"]!.GetValue<bool>());

        Item(history, seeded.Pending)["publicStatus"]!.GetValue<string>().Should().Be("pending");
        Item(history, seeded.Applied)["publicStatus"]!.GetValue<string>().Should().Be("live");
        Item(history, seeded.Approved)["publicStatus"]!.GetValue<string>().Should().Be("live");
        Item(history, seeded.Reverted)["publicStatus"]!.GetValue<string>().Should().Be("reverted");
        Item(history, seeded.Superseded)["publicStatus"]!.GetValue<string>().Should().Be("superseded");

        var rejected = Item(history, seeded.Rejected);
        rejected["publicStatus"]!.GetValue<string>().Should().Be("rejected");
        rejected["reviewNote"].Should().BeNull("the public sees the outcome, not the reviewer's reason");

        var applied = Item(history, seeded.Applied);
        applied["author"]!["displayName"]!.GetValue<string>().Should().Be("plusfan");
        applied["actor"]!.GetValue<int>().Should().Be((int)MetadataEditActor.User);
        applied["changes"]![0]!["before"]!.GetValue<string>().Should().Be("Kira Kira");
        applied["changes"]![0]!["after"]!.GetValue<string>().Should().Be("Kira☆Kira");

        var origin = history["origin"]!;
        Ints(origin["providers"]).Should().Equal((int)LinkType.Vndb);

        var editable = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Author));
        editable["historyCount"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task Editors_see_raw_statuses_reviewers_and_revert_buttons()
    {
        var seeded = await SeedEveryStatusAsync();

        var history = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.Editor));

        history["viewerRole"]!.GetValue<string>().Should().Be("editor");
        var approved = Item(history, seeded.Approved);
        approved["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Approved);
        approved["reviewer"]!["displayName"]!.GetValue<string>().Should().Be("kaede");
        approved["canRevert"]!.GetValue<bool>().Should().BeTrue();
        Item(history, seeded.Applied)["status"]!.GetValue<int>().Should().Be((int)MetadataEditStatus.Applied);
        Item(history, seeded.Pending)["canRevert"]!.GetValue<bool>().Should().BeFalse();

        var asReviewer = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.Reviewer));
        asReviewer["viewerRole"]!.GetValue<string>().Should().Be("reviewer");
        Item(asReviewer, seeded.Approved)["reviewer"]!["displayName"]!.GetValue<string>().Should().Be("kaede");
    }

    [Fact]
    public async Task Provider_rows_show_the_provider_instead_of_an_author()
    {
        var before = await DbAsync(async db => DeckMetadataSnapshot.Capture(await db.Decks.Include(d => d.Titles).FirstAsync(d => d.DeckId == DeckId)));
        var after = new DeckMetadataSnapshot { OriginalTitle = before.OriginalTitle, RomajiTitle = before.RomajiTitle, EnglishTitle = before.EnglishTitle,
                                               ReleaseDate = before.ReleaseDate, MediaType = before.MediaType, Aliases = ["Kirakira"] };
        var row = MetadataEditLog.ProviderEdit(DeckId, LinkType.Vndb, before, after)!;
        await DbAsync(async db =>
        {
            db.MetadataEdits.Add(row);
            await db.SaveChangesAsync();
        });

        var history = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/metadata-edits", Users.NoName));
        var item = Item(history, row.Id);
        item["actor"]!.GetValue<int>().Should().Be((int)MetadataEditActor.Provider);
        item["provider"]!.GetValue<int>().Should().Be((int)LinkType.Vndb);
        item["author"].Should().BeNull();
        item["publicStatus"]!.GetValue<string>().Should().Be("live");

        var recent = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent", Users.NoName));
        recent["total"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Every_read_route_needs_a_signed_in_session()
    {
        foreach (var url in new[]
                 {
                     $"/api/media-deck/{DeckId}/metadata-edits", "/api/metadata-edits/recent", $"/api/metadata-edits/by-user/{Users.Author}",
                     $"/api/metadata-edits/contributions/{Users.Author}", "/api/metadata-edits/pending", $"/api/media-deck/{DeckId}/editable"
                 })
            (await SendAsync(HttpMethod.Get, url)).Status.Should().Be(HttpStatusCode.Unauthorized, url);
    }

    [Fact]
    public async Task History_of_an_unknown_deck_is_404()
    {
        (await SendAsync(HttpMethod.Get, $"/api/media-deck/{MissingDeckId}/metadata-edits", Users.NoName)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Recent_changes_filter_by_status_and_carry_deck_info()
    {
        var seeded = await SeedEveryStatusAsync();

        var all = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent", Users.NoName));
        all["total"]!.GetValue<int>().Should().Be(7);

        var live = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent?status=live", Users.NoName));
        live["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().BeEquivalentTo([seeded.Applied, seeded.Approved, seeded.ByEditor]);
        var deck = live["items"]![0]!["deck"]!;
        deck["deckId"]!.GetValue<int>().Should().Be(DeckId);
        deck["originalTitle"]!.GetValue<string>().Should().Be("きらきら");

        var pending = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent?status=pending", Users.NoName));
        pending["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(seeded.Pending);

        var reverted = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent?status=reverted", Users.NoName));
        reverted["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(seeded.Reverted);

        (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent?status=unreviewed", Users.NoName)))["total"]!.GetValue<int>().Should().Be(0);
        (await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent?status=unreviewed", Users.Author)))["total"]!.GetValue<int>().Should().Be(0);
        var unreviewed = await OkAsync(SendAsync(HttpMethod.Get, "/api/metadata-edits/recent?status=unreviewed", Users.Editor));
        unreviewed["items"]!.AsArray().Select(i => i!["id"]!.GetValue<long>()).Should().Equal(seeded.Applied);
    }

    [Fact]
    public async Task Contributions_count_accepted_submissions_and_show_pending_only_to_the_contributor()
    {
        var seeded = await SeedEveryStatusAsync();
        await OkAsync(SendAsync(HttpMethod.Put, $"/api/metadata-edits/{seeded.Pending}/check", Users.Checker, new { verdict = 1 }));
        await SubmitOkAsync(Users.PlusFan, new { aliases = new[] { "その他" }, sources = new[] { Source } }, OtherDeckId);

        var own = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/contributions/{Users.Author}", Users.Author));
        own["accepted"]!.GetValue<int>().Should().Be(1);
        own["decksImproved"]!.GetValue<int>().Should().Be(1);
        own["pending"]!.GetValue<int>().Should().Be(1);

        var publicView = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/contributions/{Users.Author}", Users.NoName));
        publicView["pending"].Should().BeNull();
        publicView["accepted"]!.GetValue<int>().Should().Be(1);

        var plus = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/contributions/{Users.PlusFan}", Users.Author));
        plus["accepted"]!.GetValue<int>().Should().Be(2);
        plus["decksImproved"]!.GetValue<int>().Should().Be(2);
        plus["pending"].Should().BeNull();

        var checker = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/contributions/{Users.Checker}", Users.NoName));
        checker["checked"]!.GetValue<int>().Should().Be(1);
        foreach (var hidden in new[] { "level", "counted", "progress" })
            checker[hidden].Should().BeNull(hidden);
    }

    [Fact]
    public async Task By_user_lists_a_contributors_edits_with_deck_info()
    {
        await SeedEveryStatusAsync();

        var page = await OkAsync(SendAsync(HttpMethod.Get, $"/api/metadata-edits/by-user/{Users.Author}", Users.NoName));

        page["total"]!.GetValue<int>().Should().Be(4);
        page["items"]!.AsArray().Should().OnlyContain(i => i!["deck"]!["deckId"]!.GetValue<int>() == DeckId && i["status"] == null);
    }

    [Fact]
    public async Task Admin_settings_are_admin_only_validated_and_drive_auto_apply()
    {
        var defaults = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/settings", Users.Admin));
        defaults["establishedCountedEdits"]!.GetValue<int>().Should().Be(15);
        defaults["trustedCountedEdits"]!.GetValue<int>().Should().Be(50);
        defaults["establishedAutoApplyMaxTier"]!.GetValue<int>().Should().Be(-1);
        defaults["trustedAutoApplyMaxTier"]!.GetValue<int>().Should().Be((int)MetadataRiskTier.Low);

        (await SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/settings", Users.Editor)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors", Users.Editor)).Status.Should().Be(HttpStatusCode.Forbidden);

        var settings = new
        {
            establishedMinAccountDays = 30, establishedCountedEdits = 15, establishedNoRevertDays = 90, trustedCountedEdits = 50,
            trustedNoRevertDays = 180, establishedAutoApplyMaxTier = -1, trustedAutoApplyMaxTier = -1
        };
        (await ErrorAsync(SendAsync(HttpMethod.Put, "/api/admin/metadata-edits/settings", Users.Admin, settings with { trustedAutoApplyMaxTier = 3 }),
                          HttpStatusCode.BadRequest)).Should().Be("Auto-apply tiers go from -1 (none) to 2 (high).");
        (await ErrorAsync(SendAsync(HttpMethod.Put, "/api/admin/metadata-edits/settings", Users.Admin, settings with { trustedCountedEdits = -1 }),
                          HttpStatusCode.BadRequest)).Should().Be("Thresholds must be between 0 and 10000.");

        await OkAsync(SendAsync(HttpMethod.Put, "/api/admin/metadata-edits/settings", Users.Admin, settings));
        (await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/settings", Users.Admin)))["trustedAutoApplyMaxTier"]!.GetValue<int>().Should().Be(-1);

        var queued = await SubmitOkAsync(Users.PlusFan, new { englishTitle = "Kira☆Kira", sources = new[] { Source } });
        Ints(queued["appliedFields"]).Should().BeEmpty();
    }

    [Fact]
    public async Task The_contributor_list_computes_levels_and_admins_can_grant_review_roles()
    {
        await SeedEveryStatusAsync();

        var page = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors", Users.Admin));
        var levels = page["items"]!.AsArray().ToDictionary(i => i!["displayName"]!.GetValue<string>(), i => i!["level"]!.GetValue<string>());
        levels.Should().Contain(new Dictionary<string, string>
        {
            ["tsukimi"] = "new", ["plusfan"] = "trusted", ["kaede"] = "trusted", ["henshuu"] = "editor"
        });
        var author = page["items"]!.AsArray().Single(i => i!["userId"]!.GetValue<string>() == Users.Author)!;
        author["accepted"]!.GetValue<int>().Should().Be(1);
        author["counted"]!.GetValue<int>().Should().Be(1);
        author["email"]!.GetValue<string>().Should().Be($"{Users.Author}@test.local");

        var search = await OkAsync(SendAsync(HttpMethod.Get, "/api/admin/metadata-edits/contributors?search=nekom", Users.Admin));
        search["items"]!.AsArray().Select(i => i!["userId"]!.GetValue<string>()).Should().Equal(Users.Checker);

        (await ErrorAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Checker}", Users.Admin, new { role = "owner" }),
                          HttpStatusCode.BadRequest)).Should().Be("Role must be none, reviewer or editor.");
        (await SendAsync(HttpMethod.Put, "/api/admin/metadata-edits/contributors/no-such-user", Users.Admin, new { role = "reviewer" })).Status
            .Should().Be(HttpStatusCode.NotFound);

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Checker}", Users.Admin, new { role = "reviewer" }));
        var asReviewer = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Checker));
        asReviewer["viewer"]!["role"]!.GetValue<string>().Should().Be("reviewer");

        await OkAsync(SendAsync(HttpMethod.Put, $"/api/admin/metadata-edits/contributors/{Users.Checker}", Users.Admin, new { role = "none" }));
        var asUser = await OkAsync(SendAsync(HttpMethod.Get, $"/api/media-deck/{DeckId}/editable", Users.Checker));
        asUser["viewer"]!["role"]!.GetValue<string>().Should().Be("user");
    }
}
