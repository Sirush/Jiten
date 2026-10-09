using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Services;
using Jiten.Api.Services.Notices;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class OneTimeNoticeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const string StudyOrderKey = "study-order-follows-rank-source";
    private const int MediaDeckId = 7402;

    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();

        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.Decks.AnyAsync(d => d.DeckId == MediaDeckId))
        {
            jitenDb.Decks.Add(new Deck
            {
                DeckId = MediaDeckId, OriginalTitle = "Media", MediaType = MediaType.Anime, CreationDate = DateTime.UtcNow,
                CharacterCount = 1, WordCount = 1, UniqueWordCount = 1
            });
            await jitenDb.SaveChangesAsync();
        }

        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserStudyDecks.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();
        await userDb.UserSettings.ExecuteDeleteAsync();

        var cache = factory.Services.GetRequiredService<IMemoryCache>();
        foreach (var userId in new[] { TestUsers.UserA, TestUsers.UserB })
            FrequencySourceResolver.Invalidate(cache, userId);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SetDefault(string userId, int mediaType)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Put, "/api/srs/study-settings")
                                          .WithUser(userId)
                                          .WithJsonContent(new { defaultFrequencyMediaType = mediaType, defaultFrequencyListId = 0L }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task AddMediaDeck(string userId, int order)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/study-decks")
                                          .WithUser(userId)
                                          .WithJsonContent(new { deckType = 0, deckId = MediaDeckId, downloadType = 1, order }));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task Grant(string userId, string key = StudyOrderKey)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var settings = await userDb.UserSettings.FirstOrDefaultAsync(us => us.UserId == userId);
        if (settings == null)
        {
            settings = new UserSettings { UserId = userId };
            userDb.UserSettings.Add(settings);
        }

        settings.GrantedNoticesJson = JsonSerializer.Serialize(new[] { key });
        await userDb.SaveChangesAsync();
    }

    private async Task<List<JsonElement>> DecksNotices(string userId)
    {
        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/user/notices?surface=decks").WithUser(userId));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().ToList();
    }

    private async Task<HttpResponseMessage> Dismiss(string userId, string key) =>
        await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/api/user/notices/{key}/dismiss").WithUser(userId));

    [Fact]
    public async Task AffectedUser_SeesTheStudyOrderNotice_WithTheirSource()
    {
        await Grant(TestUsers.UserA);
        await SetDefault(TestUsers.UserA, (int)MediaType.Novel);
        await AddMediaDeck(TestUsers.UserA, order: 2);

        var notices = await DecksNotices(TestUsers.UserA);

        notices.Should().ContainSingle();
        notices[0].GetProperty("key").GetString().Should().Be(StudyOrderKey);
        notices[0].GetProperty("values").GetProperty("mediaType").GetString().Should().Be(((int)MediaType.Novel).ToString());
    }

    [Fact]
    public async Task UserWhoQualifiesAfterDeploy_SeesNothing()
    {
        await SetDefault(TestUsers.UserA, (int)MediaType.Novel);
        await AddMediaDeck(TestUsers.UserA, order: 2);

        (await DecksNotices(TestUsers.UserA)).Should().BeEmpty();
    }

    [Fact]
    public async Task GlobalDefault_SeesNothing()
    {
        await Grant(TestUsers.UserA);
        await AddMediaDeck(TestUsers.UserA, order: 2);

        (await DecksNotices(TestUsers.UserA)).Should().BeEmpty();
    }

    [Fact]
    public async Task NoDeckInFrequencyOrder_SeesNothing()
    {
        await Grant(TestUsers.UserA);
        await SetDefault(TestUsers.UserA, (int)MediaType.Novel);
        await AddMediaDeck(TestUsers.UserA, order: 1);

        (await DecksNotices(TestUsers.UserA)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnonymousCaller_IsRefused()
    {
        var res = await _client.GetAsync("/api/user/notices?surface=decks");

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dismissing_HidesItForGood_AndIsIdempotent()
    {
        await Grant(TestUsers.UserA);
        await SetDefault(TestUsers.UserA, (int)MediaType.Novel);
        await AddMediaDeck(TestUsers.UserA, order: 2);

        (await Dismiss(TestUsers.UserA, StudyOrderKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Dismiss(TestUsers.UserA, StudyOrderKey)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await DecksNotices(TestUsers.UserA)).Should().BeEmpty();
    }

    [Fact]
    public async Task DismissingForOneUser_LeavesOthersAlone()
    {
        foreach (var user in new[] { TestUsers.UserA, TestUsers.UserB })
        {
            await Grant(user);
            await SetDefault(user, (int)MediaType.Novel);
            await AddMediaDeck(user, order: 2);
        }

        await Dismiss(TestUsers.UserA, StudyOrderKey);

        (await DecksNotices(TestUsers.UserB)).Should().ContainSingle();
    }

    [Fact]
    public async Task UnknownKey_Returns404()
    {
        (await Dismiss(TestUsers.UserA, "no-such-notice")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ExpiredNotice_IsNotReturned()
    {
        await Grant(TestUsers.UserA);
        await SetDefault(TestUsers.UserA, (int)MediaType.Novel);
        await AddMediaDeck(TestUsers.UserA, order: 2);

        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IOneTimeNoticeService>();
        var expiry = OneTimeNoticeRegistry.Default.Notices.Single(n => n.Key == StudyOrderKey).ExpiresAtUtc;

        (await service.GetActive(TestUsers.UserA, "decks", expiry.AddSeconds(-1))).Should().ContainSingle();
        (await service.GetActive(TestUsers.UserA, "decks", expiry)).Should().BeEmpty();
    }

    [Fact]
    public async Task ANewRegistryRow_IsServedAndDismissableWithNoOtherChange()
    {
        var registry = new OneTimeNoticeRegistry(
        [
            new OneTimeNotice("test-notice", "study", DateTime.UtcNow.AddDays(1),
                              _ => Task.FromResult<IReadOnlyDictionary<string, string>?>(new Dictionary<string, string>())),
        ]);

        using var scope = factory.Services.CreateScope();
        var service = new OneTimeNoticeService(scope.ServiceProvider.GetRequiredService<UserDbContext>(),
                                               scope.ServiceProvider.GetRequiredService<IFrequencySourceResolver>(), registry);

        (await service.GetActive(TestUsers.UserA, "study", DateTime.UtcNow)).Select(n => n.Key).Should().Equal("test-notice");
        (await service.GetActive(TestUsers.UserA, "reader", DateTime.UtcNow)).Should().BeEmpty();

        (await service.Dismiss(TestUsers.UserA, "test-notice")).Should().BeTrue();
        (await service.GetActive(TestUsers.UserA, "study", DateTime.UtcNow)).Should().BeEmpty();
    }
}
