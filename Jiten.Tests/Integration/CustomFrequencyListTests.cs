using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Controllers;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.JMDict;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// Custom frequency lists (Jiten+). Seeds six primary Novel decks (all genre Action; one tagged 100)
/// with five shared words, then exercises the tiered endpoints and the generation job.
/// </summary>
public class CustomFrequencyListTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();
    private List<int> _deckIds = new();

    private const int TagIsekai = 100;

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        await ResetBilling();
        await ClearLists();
        factory.Services.GetRequiredService<StubCdnService>().Uploads.Clear();
        factory.Services.GetRequiredService<StubCdnService>().Deletions.Clear();
        await SeedDecks();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- Seeding / helpers --------------------------------------------------

    private async Task ResetBilling()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        userDb.PromoCodes.RemoveRange(userDb.PromoCodes);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.SubscriptionPeriodEnd = null;
            user.SubscriptionPlan = null;
            user.IsLifetime = false;
            user.LifetimeSource = null;
            user.AdminPremiumOverride = false;
        }
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    private async Task ClearLists()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserFrequencyLists.RemoveRange(userDb.UserFrequencyLists);
        await userDb.SaveChangesAsync();
    }

    private async Task SeedDecks()
    {
        using var scope = factory.Services.CreateScope();
        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        await jitenDb.DeckWords.ExecuteDeleteAsync();
        await jitenDb.WordForms.ExecuteDeleteAsync();
        await jitenDb.JMDictWords.ExecuteDeleteAsync();

        for (var i = 1; i <= 5; i++)
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = i, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm
            {
                WordId = i, ReadingIndex = 0, Text = $"かな{i}", RubyText = $"かな{i}",
                FormType = JmDictFormType.KanaForm,
            });
        }

        if (!await jitenDb.Tags.AnyAsync(t => t.TagId == TagIsekai))
            jitenDb.Tags.Add(new Tag { TagId = TagIsekai, Name = "Isekai" });

        await jitenDb.SaveChangesAsync();

        _deckIds = new();
        for (var d = 0; d < 6; d++)
        {
            var deck = new Deck
            {
                OriginalTitle = $"Novel {d}",
                MediaType = MediaType.Novel,
                ReleaseDate = new DateOnly(2020, 1, 1),
                CharacterCount = 1000 + d,
                DeckGenres = new List<DeckGenre> { new() { Genre = Genre.Action } },
            };
            if (d < 1)
                deck.DeckTags = new List<DeckTag> { new() { TagId = TagIsekai, Percentage = 100 } };

            jitenDb.Decks.Add(deck);
            await jitenDb.SaveChangesAsync();
            _deckIds.Add(deck.DeckId);

            for (var w = 1; w <= 5; w++)
                jitenDb.DeckWords.Add(new DeckWord { Deck = deck, WordId = w, ReadingIndex = 0, Occurrences = w * (d + 1) });
            await jitenDb.SaveChangesAsync();
        }
    }

    private async Task MakeFull(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var user = await userDb.Users.FirstAsync(u => u.Id == userId);
        user.AdminPremiumOverride = true;
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private async Task MakeTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 5, GrantsFullTier = false };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();
        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId, PromoCodeId = code.CodeId, GrantsFullTier = false, RemainingDays = 5, GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private HttpRequestMessage Post(string url, object body, string userId) =>
        new HttpRequestMessage(HttpMethod.Post, url).WithUser(userId).WithJsonContent(body);

    private object FilterBody(string name, bool save, object? definition = null, bool autoUpdate = false) => new
    {
        name,
        mode = "filters",
        save,
        autoUpdate,
        definition = definition ?? new { mediaTypes = new[] { (int)MediaType.Novel } }
    };

    private async Task<(long Id, string Status)> CreateOk(object body, string userId)
    {
        var res = await _client.SendAsync(Post("/api/frequency-lists", body, userId));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (dto.GetProperty("id").GetInt64(), dto.GetProperty("status").GetString()!);
    }

    private async Task RunGeneration(long listId)
    {
        using var scope = factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<FrequencyListJob>();
        await job.Generate(listId);
    }

    // ---- Tests --------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var res = await _client.GetAsync("/api/frequency-lists");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NonPlusUser_CannotCreate()
    {
        // No tier: the [JitenPlus] gate rejects before validation.
        var res = await _client.SendAsync(Post("/api/frequency-lists", FilterBody("x", false), TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Preview_ReturnsMatchedCount()
    {
        await MakeTrial(TestUsers.UserA);
        var req = new HttpRequestMessage(HttpMethod.Get,
                $"/api/frequency-lists/preview?mode=filters&mediaTypes={(int)MediaType.Novel}")
            .WithUser(TestUsers.UserA);
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("deckCount").GetInt32().Should().Be(6);
        body.GetProperty("sampleTitles").GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Create_BelowMinDecks_ReturnsBadRequest()
    {
        await MakeTrial(TestUsers.UserA);
        // Only one deck carries the Isekai tag — below the two-deck minimum.
        var body = FilterBody("too small", false, new { tagsInclude = new[] { TagIsekai } });
        var res = await _client.SendAsync(Post("/api/frequency-lists", body, TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var err = await res.Content.ReadFromJsonAsync<JsonElement>();
        err.GetProperty("deckCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Create_AsTrial_WithSave_Returns403()
    {
        await MakeTrial(TestUsers.UserA);
        var res = await _client.SendAsync(Post("/api/frequency-lists", FilterBody("saved", save: true), TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("jitenPlus").GetBoolean().Should().BeTrue();
        body.GetProperty("requiredTier").GetString().Should().Be("full");
    }

    [Fact]
    public async Task Create_Transient_ThenGenerate_ProducesReadyListAndCdnFiles()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, status) = await CreateOk(FilterBody("my list", false), TestUsers.UserA);
        status.Should().Be("pending");

        await RunGeneration(id);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var list = await userDb.UserFrequencyLists.FirstAsync(f => f.Id == id);
        list.Status.Should().Be(FrequencyListStatus.Ready);
        list.DeckCount.Should().Be(6);
        list.WordCount.Should().Be(5);
        list.ZipUrl.Should().NotBeNullOrEmpty();
        list.CsvUrl.Should().NotBeNullOrEmpty();

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Uploads.Select(u => u.FileName).Should().Contain(FrequencyListJob.ZipStoragePath(TestUsers.UserA, id));
        cdn.Uploads.Select(u => u.FileName).Should().Contain(FrequencyListJob.CsvStoragePath(TestUsers.UserA, id));
    }

    [Fact]
    public async Task Create_HandPicked_Succeeds()
    {
        await MakeTrial(TestUsers.UserA);
        var body = new { name = "picked", mode = "handpicked", save = false, autoUpdate = false, definition = new { deckIds = _deckIds } };
        var (_, status) = await CreateOk(body, TestUsers.UserA);
        status.Should().Be("pending");
    }

    [Fact]
    public async Task Create_TransientCap_Enforced()
    {
        await MakeTrial(TestUsers.UserA);
        for (var i = 0; i < CustomFrequencyListController.MAX_TRANSIENT_LISTS; i++)
            await CreateOk(FilterBody($"list {i}", false), TestUsers.UserA);

        var res = await _client.SendAsync(Post("/api/frequency-lists", FilterBody("overflow", false), TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Save_AsFull_PromotesTransient_AndCapsAtMax()
    {
        await MakeFull(TestUsers.UserA);

        // Fill the saved cap directly at creation.
        for (var i = 0; i < CustomFrequencyListController.MAX_SAVED_LISTS; i++)
            await CreateOk(FilterBody($"saved {i}", save: true), TestUsers.UserA);

        // One more transient, then try to save it -> cap hit.
        var (transientId, _) = await CreateOk(FilterBody("transient", save: false), TestUsers.UserA);
        var res = await _client.SendAsync(Post($"/api/frequency-lists/{transientId}/save", new { }, TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Save_AsTrial_Returns403()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("t", save: false), TestUsers.UserA);
        var res = await _client.SendAsync(Post($"/api/frequency-lists/{id}/save", new { }, TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Ownership_OtherUsersList_Returns404()
    {
        await MakeTrial(TestUsers.UserA);
        await MakeTrial(TestUsers.UserB);
        var (id, _) = await CreateOk(FilterBody("mine", save: false), TestUsers.UserA);

        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/frequency-lists/{id}/download")
            .WithUser(TestUsers.UserB));
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Share_AsFull_ThenAnonymousDownloadServesNamedFile()
    {
        await MakeFull(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("shared", save: true), TestUsers.UserA);
        await RunGeneration(id);

        // Saved lists mint their slug at creation; share just retrieves it.
        var shareRes = await _client.SendAsync(Post($"/api/frequency-lists/{id}/share", new { }, TestUsers.UserA));
        shareRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var slug = (await shareRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("slug").GetString();
        slug.Should().NotBeNullOrEmpty();

        var anonymous = factory.CreateClient();
        var dl = await anonymous.GetAsync($"/api/frequency-lists/shared/{slug}");
        dl.StatusCode.Should().Be(HttpStatusCode.OK);
        dl.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        dl.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("shared.zip");
        (await dl.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task SavedList_HasSlugAtCreation_AndYomitanIndexIsUpdatable()
    {
        await MakeFull(TestUsers.UserA);
        var res = await _client.SendAsync(Post("/api/frequency-lists", FilterBody("updatable", save: true), TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = dto.GetProperty("id").GetInt64();
        var slug = dto.GetProperty("publicSlug").GetString();
        slug.Should().NotBeNullOrEmpty();

        await RunGeneration(id);

        // The anonymous index endpoint serves a fresh, updatable Yomitan index for the slug.
        var anonymous = factory.CreateClient();
        var indexRes = await anonymous.GetAsync($"/api/frequency-lists/shared/{slug}/index");
        indexRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var index = await indexRes.Content.ReadFromJsonAsync<JsonElement>();
        index.GetProperty("title").GetString().Should().Be("updatable");
        index.GetProperty("format").GetInt32().Should().Be(3);
        index.GetProperty("isUpdatable").GetBoolean().Should().BeTrue();
        index.GetProperty("indexUrl").GetString().Should().Contain($"/api/frequency-lists/shared/{slug}/index");
        index.GetProperty("downloadUrl").GetString().Should().Contain($"/api/frequency-lists/shared/{slug}?format=zip");
        index.GetProperty("revision").GetString().Should().MatchRegex(@"^\d{4}\.\d{2}\.\d{2}$");

        // The zip embeds the same updatable index, so an imported dictionary knows where to poll.
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var zip = cdn.Uploads.Last(u => u.FileName == FrequencyListJob.ZipStoragePath(TestUsers.UserA, id)).File;
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("index.json")!.Open());
        var embedded = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
        embedded.GetProperty("isUpdatable").GetBoolean().Should().BeTrue();
        embedded.GetProperty("indexUrl").GetString().Should().Contain(slug);
        embedded.GetProperty("revision").GetString().Should().Be(index.GetProperty("revision").GetString());
    }

    [Fact]
    public async Task TransientList_HasNoSlug_AndZipIsNotUpdatable()
    {
        await MakeTrial(TestUsers.UserA);
        var res = await _client.SendAsync(Post("/api/frequency-lists", FilterBody("throwaway", save: false), TestUsers.UserA));
        var dto = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = dto.GetProperty("id").GetInt64();
        dto.GetProperty("publicSlug").ValueKind.Should().Be(JsonValueKind.Null);

        await RunGeneration(id);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var zip = cdn.Uploads.Last(u => u.FileName == FrequencyListJob.ZipStoragePath(TestUsers.UserA, id)).File;
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("index.json")!.Open());
        var embedded = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
        embedded.TryGetProperty("isUpdatable", out _).Should().BeFalse();
        embedded.GetProperty("title").GetString().Should().Be("throwaway");
    }

    [Fact]
    public async Task SaveAfterGeneration_PatchesZipWithUpdateUrls()
    {
        await MakeFull(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("late save", save: false), TestUsers.UserA);
        await RunGeneration(id);

        // Saving mints the slug and synchronously patches the already-uploaded zip in place.
        var saveRes = await _client.SendAsync(Post($"/api/frequency-lists/{id}/save", new { }, TestUsers.UserA));
        saveRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var slug = (await saveRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("publicSlug").GetString();
        slug.Should().NotBeNullOrEmpty();

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var zip = cdn.Uploads.Last(u => u.FileName == FrequencyListJob.ZipStoragePath(TestUsers.UserA, id)).File;
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip));
        using var reader = new StreamReader(archive.GetEntry("index.json")!.Open());
        var embedded = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
        embedded.GetProperty("isUpdatable").GetBoolean().Should().BeTrue();
        embedded.GetProperty("indexUrl").GetString().Should().Contain(slug!);
        embedded.GetProperty("downloadUrl").GetString().Should().Contain(slug!);
        embedded.GetProperty("title").GetString().Should().Be("late save");
    }

    [Fact]
    public async Task OwnDownload_ServesFileNamedAfterList()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("My Anime Words", save: false), TestUsers.UserA);
        await RunGeneration(id);

        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/frequency-lists/{id}/download?format=csv")
            .WithUser(TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        res.Content.Headers.ContentDisposition!.FileName!.Trim('"').Should().Be("My Anime Words.csv");
        (await res.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesRowAndCdnFiles()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("gone", save: false), TestUsers.UserA);
        await RunGeneration(id);

        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/frequency-lists/{id}")
            .WithUser(TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserFrequencyLists.AnyAsync(f => f.Id == id)).Should().BeFalse();

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Should().Contain(FrequencyListJob.ZipStoragePath(TestUsers.UserA, id));
        cdn.Deletions.Should().Contain(FrequencyListJob.CsvStoragePath(TestUsers.UserA, id));
    }

    [Fact]
    public async Task Cleanup_ExpiresTransientFiles_ButKeepsRowAndDefinition()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("temp", save: false), TestUsers.UserA);
        await RunGeneration(id);

        // Backdate generation past the 48h window.
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var l = await userDb.UserFrequencyLists.FirstAsync(f => f.Id == id);
            l.GeneratedAt = DateTime.UtcNow.AddDays(-3);
            await userDb.SaveChangesAsync();

            var job = scope.ServiceProvider.GetRequiredService<FrequencyListJob>();
            await job.CleanupTransientLists();
        }

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<UserDbContext>();
        var list = await db.UserFrequencyLists.FirstOrDefaultAsync(f => f.Id == id);
        list.Should().NotBeNull();
        list!.Status.Should().Be(FrequencyListStatus.Expired);
        list.ZipUrl.Should().BeNull();
        list.CsvUrl.Should().BeNull();
        // Filter definition is retained for one-click regenerate.
        list.Definition.MediaTypes.Should().Contain((int)MediaType.Novel);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Should().Contain(FrequencyListJob.ZipStoragePath(TestUsers.UserA, id));
        cdn.Deletions.Should().Contain(FrequencyListJob.CsvStoragePath(TestUsers.UserA, id));
    }

    [Fact]
    public async Task Download_ExpiredList_Returns410()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("temp", save: false), TestUsers.UserA);
        await RunGeneration(id);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var l = await userDb.UserFrequencyLists.FirstAsync(f => f.Id == id);
            l.GeneratedAt = DateTime.UtcNow.AddDays(-3);
            await userDb.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<FrequencyListJob>().CleanupTransientLists();
        }

        var res = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/frequency-lists/{id}/download")
            .WithUser(TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Regenerate_RevivesExpiredList()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("revive", save: false), TestUsers.UserA);
        await RunGeneration(id);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var l = await userDb.UserFrequencyLists.FirstAsync(f => f.Id == id);
            l.GeneratedAt = DateTime.UtcNow.AddDays(-3);
            await userDb.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<FrequencyListJob>().CleanupTransientLists();
        }

        var res = await _client.SendAsync(Post($"/api/frequency-lists/{id}/regenerate", new { }, TestUsers.UserA));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        await RunGeneration(id);

        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<UserDbContext>();
        var list = await db.UserFrequencyLists.FirstAsync(f => f.Id == id);
        list.Status.Should().Be(FrequencyListStatus.Ready);
        list.ZipUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AutoUpdateToggle_RequiresFull()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("t", save: false), TestUsers.UserA);

        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/frequency-lists/{id}")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { autoUpdate = true });
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Rename_AsTrial_Succeeds()
    {
        await MakeTrial(TestUsers.UserA);
        var (id, _) = await CreateOk(FilterBody("old name", save: false), TestUsers.UserA);

        var req = new HttpRequestMessage(HttpMethod.Patch, $"/api/frequency-lists/{id}")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new { name = "new name" });
        var res = await _client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString().Should().Be("new name");
    }
}
