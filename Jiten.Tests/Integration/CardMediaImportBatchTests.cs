using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ImageMagick;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CardMediaImportBatchTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] RealPng(int w = 64, int h = 64)
    {
        using var image = new MagickImage(MagickColors.Red, (uint)w, (uint)h);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    /// <summary>Every form the batch writes to has to be one the caller studies (the not_tracked fence).</summary>
    private static readonly (int WordId, byte ReadingIndex)[] TrackedForms = [(400, 0), (401, 0), (402, 0)];

    public async Task InitializeAsync()
    {
        factory.Services.GetRequiredService<StubCdnService>().FailNextUploads = 0;

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        userDb.UserCardMedia.RemoveRange(userDb.UserCardMedia);
        userDb.FsrsCards.RemoveRange(userDb.FsrsCards);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.IsLifetime = false;
            // UserA is the Full-tier actor; UserB stays free so the gate can be exercised.
            user.AdminPremiumOverride = user.Id == TestUsers.UserA;
        }

        foreach (var (wordId, readingIndex) in TrackedForms)
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, readingIndex));

        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Item(int Index, int WordId, int ReadingIndex, bool Overwrite, byte[] Bytes);

    private static Item File(int index, byte[] bytes, int wordId = 400, int readingIndex = 0, bool overwrite = false)
        => new(index, wordId, readingIndex, overwrite, bytes);

    private async Task<HttpResponseMessage> ImportRaw(string userId, string manifest, params Item[] items)
    {
        var content = new MultipartFormDataContent { { new StringContent(manifest), "manifest" } };
        foreach (var item in items)
            content.Add(new ByteArrayContent(item.Bytes), $"file{item.Index}", $"upload{item.Index}.bin");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-media/import-batch") { Content = content }
            .WithUser(userId);
        return await _client.SendAsync(request);
    }

    private Task<HttpResponseMessage> ImportRaw(string userId, params Item[] items)
    {
        var manifest = JsonSerializer.Serialize(items.Select(i => new
        {
            index = i.Index, wordId = i.WordId, readingIndex = i.ReadingIndex, overwrite = i.Overwrite
        }));
        return ImportRaw(userId, manifest, items);
    }

    private async Task<(JsonElement Body, Dictionary<int, string> Statuses)> Import(string userId, params Item[] items)
    {
        var response = await ImportRaw(userId, items);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var statuses = body.GetProperty("results")
                           .EnumerateArray()
                           .ToDictionary(r => r.GetProperty("index").GetInt32(), r => r.GetProperty("status").GetString()!);
        return (body, statuses);
    }

    private async Task<List<UserCardMedia>> Stored(string userId = TestUsers.UserA)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserCardMedia.Where(m => m.UserId == userId).ToListAsync();
    }

    [Fact]
    public async Task ImportBatch_TransientCdnFailure_RetriesAndStoresTheFile()
    {
        factory.Services.GetRequiredService<StubCdnService>().FailNextUploads = 1;

        var (_, statuses) = await Import(TestUsers.UserA, File(0, RealPng(), wordId: 400));

        statuses[0].Should().Be("ok");
        (await Stored()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_CdnOutage_FailsPerItemInsteadOf500ing()
    {
        var stub = factory.Services.GetRequiredService<StubCdnService>();
        stub.FailNextUploads = int.MaxValue;
        try
        {
            var (_, statuses) = await Import(TestUsers.UserA,
                                             File(0, RealPng(), wordId: 400),
                                             File(1, Mp3, wordId: 401),
                                             File(2, RealPng(), wordId: 402));

            // The first item exhausts its retries; the rest are failed without attempting.
            statuses.Values.Should().OnlyContain(s => s == "upload_failed");
            (await Stored()).Should().BeEmpty();
        }
        finally
        {
            stub.FailNextUploads = 0;
        }
    }

    [Fact]
    public async Task ImportBatch_WritesEveryFile()
    {
        var (body, statuses) = await Import(TestUsers.UserA,
                                            File(0, RealPng(), wordId: 400),
                                            File(1, Mp3, wordId: 401),
                                            File(2, RealPng(), wordId: 402));

        statuses.Values.Should().AllBe("ok");
        (await Stored()).Should().HaveCount(3);
        body.GetProperty("usedBytes").GetInt64().Should().BeGreaterThan(0);
        body.GetProperty("maxBytes").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ImportBatch_ReportsTheSniffedKind()
    {
        var (body, _) = await Import(TestUsers.UserA, File(0, RealPng()), File(1, Mp3, wordId: 401));

        var kinds = body.GetProperty("results").EnumerateArray()
                        .ToDictionary(r => r.GetProperty("index").GetInt32(), r => r.GetProperty("kind").GetString());
        kinds[0].Should().Be("image");
        kinds[1].Should().Be("audio");
    }

    /// <summary>The response must not be usable as an upload-and-share primitive.</summary>
    [Fact]
    public async Task ImportBatch_ReturnsNoUrls()
    {
        var response = await ImportRaw(TestUsers.UserA, File(0, RealPng()));
        var raw = await response.Content.ReadAsStringAsync();

        raw.Should().NotContain("http");
        raw.Should().NotContain("storagePath");
    }

    [Fact]
    public async Task ImportBatch_WithoutOverwrite_ReportsConflict()
    {
        await Import(TestUsers.UserA, File(0, RealPng()));

        var (_, statuses) = await Import(TestUsers.UserA, File(0, RealPng(32, 32)));

        statuses[0].Should().Be("conflict");
        (await Stored()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_WithOverwrite_ReplacesTheRowAndDeletesTheOldFile()
    {
        await Import(TestUsers.UserA, File(0, RealPng()));
        var original = (await Stored()).Single().StoragePath;

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var (_, statuses) = await Import(TestUsers.UserA, File(0, RealPng(32, 32), overwrite: true));

        statuses[0].Should().Be("ok");
        var stored = (await Stored()).Single();
        stored.StoragePath.Should().NotBe(original);
        cdn.Deletions.Should().Contain(original);
    }

    [Fact]
    public async Task ImportBatch_ForAFormTheUserDoesNotStudy_IsRejected()
    {
        var (_, statuses) = await Import(TestUsers.UserA, File(0, RealPng(), wordId: 999));

        statuses[0].Should().Be("not_tracked");
        (await Stored()).Should().BeEmpty();
    }

    [Fact]
    public async Task ImportBatch_WithUnsniffableBytes_ReportsInvalidWithoutFailingTheRequest()
    {
        var (_, statuses) = await Import(TestUsers.UserA,
                                         File(0, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }),
                                         File(1, RealPng(), wordId: 401));

        statuses[0].Should().Be("invalid");
        statuses[1].Should().Be("ok");
        (await Stored()).Should().ContainSingle();
    }

    [Fact]
    public async Task ImportBatch_OverTheQuota_ShortCircuitsRemainingItems()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            // Park the account just under the ceiling so the first real file cannot fit.
            var quota = scope.ServiceProvider.GetRequiredService<ICardMediaQuotaService>();
            var max = (await quota.GetQuotaAsync(TestUsers.UserA)).MaxBytes;
            userDb.UserCardMedia.Add(new UserCardMedia
            {
                UserId = TestUsers.UserA, WordId = 500, ReadingIndex = 0, Kind = CardMediaKind.Image,
                StoragePath = "card-media/filler.webp", ContentType = "image/webp", FileSizeBytes = max
            });
            await userDb.SaveChangesAsync();
        }

        var (_, statuses) = await Import(TestUsers.UserA,
                                         File(0, RealPng(), wordId: 400),
                                         File(1, RealPng(), wordId: 401),
                                         File(2, Mp3, wordId: 402));

        statuses[0].Should().Be("quota_exceeded");
        statuses[1].Should().Be("quota_exceeded");
        statuses[2].Should().Be("quota_exceeded");
    }

    /// <summary>Replacing a file frees the bytes it held, so a full account can still swap one out.</summary>
    [Fact]
    public async Task ImportBatch_ReplacingAtTheQuotaCeiling_Succeeds()
    {
        await Import(TestUsers.UserA, File(0, RealPng(64, 64)));
        var storedBytes = (await Stored()).Single().FileSizeBytes;

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            var quota = scope.ServiceProvider.GetRequiredService<ICardMediaQuotaService>();
            var max = (await quota.GetQuotaAsync(TestUsers.UserA)).MaxBytes;
            userDb.UserCardMedia.Add(new UserCardMedia
            {
                UserId = TestUsers.UserA, WordId = 500, ReadingIndex = 0, Kind = CardMediaKind.Image,
                StoragePath = "card-media/filler.webp", ContentType = "image/webp", FileSizeBytes = max - storedBytes
            });
            await userDb.SaveChangesAsync();
        }

        var (_, statuses) = await Import(TestUsers.UserA, File(0, RealPng(32, 32), overwrite: true));

        statuses[0].Should().Be("ok");
    }

    [Fact]
    public async Task ImportBatch_WithADuplicateManifestIndex_ReturnsBadRequest()
    {
        var manifest = JsonSerializer.Serialize(new[]
        {
            new { index = 0, wordId = 400, readingIndex = 0, overwrite = false },
            new { index = 0, wordId = 401, readingIndex = 0, overwrite = false }
        });

        var response = await ImportRaw(TestUsers.UserA, manifest, File(0, RealPng()), File(1, RealPng()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportBatch_WithMismatchedManifestAndFileCounts_ReturnsBadRequest()
    {
        var manifest = JsonSerializer.Serialize(new[]
        {
            new { index = 0, wordId = 400, readingIndex = 0, overwrite = false },
            new { index = 1, wordId = 401, readingIndex = 0, overwrite = false }
        });

        var response = await ImportRaw(TestUsers.UserA, manifest, File(0, RealPng()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportBatch_OverTheItemCap_ReturnsBadRequest()
    {
        var items = Enumerable.Range(0, 21).Select(i => File(i, RealPng())).ToArray();

        var response = await ImportRaw(TestUsers.UserA, items);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportBatch_WithMalformedManifest_ReturnsBadRequest()
    {
        var response = await ImportRaw(TestUsers.UserA, "not json", File(0, RealPng()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImportBatch_ForAFreeUser_IsGated()
    {
        var response = await ImportRaw(TestUsers.UserB, File(0, RealPng()));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("jitenPlus").GetBoolean().Should().BeTrue();
        body.GetProperty("requiredTier").GetString().Should().Be("trial");
    }
}
