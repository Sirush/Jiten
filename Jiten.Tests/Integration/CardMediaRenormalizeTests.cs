using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ImageMagick;
using Jiten.Api.Jobs;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>
/// The renormalize backfill rewrites files users own, so these cover the properties that make that safe:
/// the original is never deleted, a row that changed underneath is left alone, and a rewrite is reversible.
/// </summary>
public class CardMediaRenormalizeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        userDb.UserCardMedia.RemoveRange(userDb.UserCardMedia);
        foreach (var user in await userDb.Users.ToListAsync())
            user.AdminPremiumOverride = user.Id == TestUsers.UserA;
        await userDb.SaveChangesAsync();

        factory.Services.GetRequiredService<StubCdnService>().Uploads.Clear();
        factory.Services.GetRequiredService<StubCdnService>().Deletions.Clear();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// A full-frame PNG like the ones that fell through: noise, because a flat colour compresses better as
    /// PNG than as lossy WebP and the job is right to leave those alone.
    /// </summary>
    private static byte[] UnprocessedPng(int w = 1280, int h = 720)
    {
        using var image = new MagickImage(MagickColors.CornflowerBlue, (uint)w, (uint)h);
        image.AddNoise(NoiseType.Random);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private Task<UserCardMedia> SeedUnprocessedAsync(int wordId, byte[]? bytes = null) =>
        SeedAsync(wordId, bytes ?? UnprocessedPng(), CardMediaKind.Image, "image/png", "png");

    /// <summary>Places a file on the stub CDN and the row that points at it, bypassing the normalizing endpoint.</summary>
    private async Task<UserCardMedia> SeedAsync(
        int wordId, byte[] bytes, CardMediaKind kind, string contentType, string extension)
    {
        var cdn = factory.Services.GetRequiredService<StubCdnService>();

        var path = CardMediaStorage.PathFor(TestUsers.UserA, wordId, 0, kind, extension);
        await cdn.UploadFile(bytes, path, secure: true);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var row = new UserCardMedia
                  {
                      UserId = TestUsers.UserA,
                      WordId = wordId,
                      ReadingIndex = 0,
                      Kind = kind,
                      StoragePath = path,
                      ContentType = contentType,
                      FileSizeBytes = bytes.Length
                  };
        userDb.UserCardMedia.Add(row);
        await userDb.SaveChangesAsync();
        return row;
    }

    private async Task RunJobAsync(bool dryRun)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CardMediaRenormalizeJob>().RenormalizeAll(dryRun);
    }

    private static async Task<UserCardMedia> ReloadAsync(JitenWebApplicationFactory factory, long id)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserCardMedia.AsNoTracking().FirstAsync(m => m.Id == id);
    }

    [Fact]
    public async Task Renormalize_RewritesToWebp_AndKeepsTheOriginal()
    {
        var seeded = await SeedUnprocessedAsync(400);
        var cdn = factory.Services.GetRequiredService<StubCdnService>();

        await RunJobAsync(dryRun: false);

        var row = await ReloadAsync(factory, seeded.Id);
        row.ContentType.Should().Be("image/webp");
        row.StoragePath.Should().NotBe(seeded.StoragePath).And.EndWith(".webp");
        row.FileSizeBytes.Should().BeLessThan(seeded.FileSizeBytes);

        // The whole safety story: the file the row used to point at is still on the CDN, and the row still
        // knows where it is.
        cdn.Deletions.Should().NotContain(seeded.StoragePath);
        (await cdn.DownloadFile(seeded.StoragePath, secure: true)).Should().NotBeNull();
        row.PreviousStoragePath.Should().Be(seeded.StoragePath);
        row.PreviousContentType.Should().Be("image/png");
        row.PreviousFileSizeBytes.Should().Be(seeded.FileSizeBytes);
    }

    /// <summary>
    /// A second pass must not overwrite a retained original with the file the first pass produced, which
    /// would leave the row unable to roll back to anything but its own output.
    /// </summary>
    [Fact]
    public async Task Renormalize_RunTwice_KeepsTheFirstOriginal()
    {
        var seeded = await SeedUnprocessedAsync(408);

        await RunJobAsync(dryRun: false);
        var afterFirst = await ReloadAsync(factory, seeded.Id);
        await RunJobAsync(dryRun: false);
        var afterSecond = await ReloadAsync(factory, seeded.Id);

        afterSecond.PreviousStoragePath.Should().Be(seeded.StoragePath);
        afterSecond.StoragePath.Should().Be(afterFirst.StoragePath);
    }

    /// <summary>Replacing the media orphans the retained original, so the upload has to take it too.</summary>
    [Fact]
    public async Task ReplacingRewrittenMedia_DeletesTheRetainedOriginal()
    {
        var seeded = await SeedUnprocessedAsync(409);
        await RunJobAsync(dryRun: false);
        var rewritten = await ReloadAsync(factory, seeded.Id);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-media/409/0")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(UnprocessedPng(200, 200)), "file", "x.png" } }
        }.WithUser(TestUsers.UserA);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        cdn.Deletions.Should().Contain(seeded.StoragePath).And.Contain(rewritten.StoragePath);

        var row = await ReloadAsync(factory, seeded.Id);
        row.PreviousStoragePath.Should().BeNull();
        row.PreviousContentType.Should().BeNull();
        row.PreviousFileSizeBytes.Should().BeNull();
    }

    /// <summary>Deleting the media has the same obligation as replacing it.</summary>
    [Fact]
    public async Task DeletingRewrittenMedia_DeletesTheRetainedOriginal()
    {
        var seeded = await SeedUnprocessedAsync(410);
        await RunJobAsync(dryRun: false);
        var rewritten = await ReloadAsync(factory, seeded.Id);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media/410/0/image").WithUser(TestUsers.UserA);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        cdn.Deletions.Should().Contain(seeded.StoragePath).And.Contain(rewritten.StoragePath);
    }

    [Fact]
    public async Task Renormalize_DryRun_WritesNothing()
    {
        var seeded = await SeedUnprocessedAsync(401);
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var uploadsBefore = cdn.Uploads.Count;

        await RunJobAsync(dryRun: true);

        var row = await ReloadAsync(factory, seeded.Id);
        row.StoragePath.Should().Be(seeded.StoragePath);
        row.ContentType.Should().Be("image/png");
        row.FileSizeBytes.Should().Be(seeded.FileSizeBytes);
        cdn.Uploads.Count.Should().Be(uploadsBefore);
        row.PreviousStoragePath.Should().BeNull();
    }

    [Fact]
    public async Task Renormalize_RowChangedSinceRead_IsLeftAlone()
    {
        var seeded = await SeedUnprocessedAsync(402);

        // Stands in for a user replacing the file mid-run: the row no longer describes the stored bytes.
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            await userDb.UserCardMedia.Where(m => m.Id == seeded.Id)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.FileSizeBytes, seeded.FileSizeBytes + 1));
        }

        await RunJobAsync(dryRun: false);

        var row = await ReloadAsync(factory, seeded.Id);
        row.StoragePath.Should().Be(seeded.StoragePath);
        row.ContentType.Should().Be("image/png");
    }

    [Fact]
    public async Task Renormalize_SkipsFilesAlreadyWebp()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-media/403/0")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(UnprocessedPng(200, 200)), "file", "x.png" } }
        }.WithUser(TestUsers.UserA);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var uploadsBefore = cdn.Uploads.Count;

        await RunJobAsync(dryRun: false);

        cdn.Uploads.Count.Should().Be(uploadsBefore);
    }

    [Fact]
    public async Task Renormalize_NeverTouchesAudio()
    {
        var mp3 = new byte[] { 0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var seeded = await SeedAsync(406, mp3, CardMediaKind.Audio, "audio/mpeg", "mp3");
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var uploadsBefore = cdn.Uploads.Count;

        await RunJobAsync(dryRun: false);

        var row = await ReloadAsync(factory, seeded.Id);
        row.StoragePath.Should().Be(seeded.StoragePath);
        row.ContentType.Should().Be("audio/mpeg");
        row.FileSizeBytes.Should().Be(seeded.FileSizeBytes);
        cdn.Uploads.Count.Should().Be(uploadsBefore);
        cdn.Deletions.Should().BeEmpty();
    }

    /// <summary>
    /// The kind filter is a column, not the file: a row that claims Image over audio bytes must still be
    /// rejected, which is what the second guard on the sniffed kind is for.
    /// </summary>
    [Fact]
    public async Task Renormalize_AudioBytesMislabelledAsImage_AreLeftAlone()
    {
        var mp3 = new byte[] { 0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var seeded = await SeedAsync(407, mp3, CardMediaKind.Image, "image/png", "png");
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var uploadsBefore = cdn.Uploads.Count;

        await RunJobAsync(dryRun: false);

        var row = await ReloadAsync(factory, seeded.Id);
        row.StoragePath.Should().Be(seeded.StoragePath);
        row.ContentType.Should().Be("image/png");
        cdn.Uploads.Count.Should().Be(uploadsBefore);
    }

    [Fact]
    public async Task Rollback_RestoresTheOriginalAndRemovesTheRewrite()
    {
        var seeded = await SeedUnprocessedAsync(404);
        await RunJobAsync(dryRun: false);
        var rewritten = await ReloadAsync(factory, seeded.Id);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CardMediaRenormalizeJob>().RollbackAll();

        var row = await ReloadAsync(factory, seeded.Id);
        row.StoragePath.Should().Be(seeded.StoragePath);
        row.ContentType.Should().Be("image/png");
        row.FileSizeBytes.Should().Be(seeded.FileSizeBytes);
        row.PreviousStoragePath.Should().BeNull();

        // The re-encoded file is unreferenced now, so leaving it would orphan it.
        factory.Services.GetRequiredService<StubCdnService>().Deletions.Should().Contain(rewritten.StoragePath);
    }

    [Fact]
    public async Task DiscardOriginals_DeletesTheOriginalAndClearsTheColumns()
    {
        var seeded = await SeedUnprocessedAsync(411);
        await RunJobAsync(dryRun: false);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<CardMediaRenormalizeJob>().DiscardOriginals();

        var row = await ReloadAsync(factory, seeded.Id);
        row.PreviousStoragePath.Should().BeNull();
        row.ContentType.Should().Be("image/webp");
        factory.Services.GetRequiredService<StubCdnService>().Deletions.Should().Contain(seeded.StoragePath);
    }

    [Fact]
    public async Task Preview_ReportsAffectedRowsWithoutMediaUrls()
    {
        await SeedUnprocessedAsync(405);

        var response = await _client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "/api/admin/card-media/renormalize/preview").WithAdmin());
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("totalCount").GetInt32().Should().Be(1);
        body.GetProperty("totalBytes").GetInt64().Should().BeGreaterThan(0);

        var item = body.GetProperty("items").EnumerateArray().Single();
        item.GetProperty("wordId").GetInt32().Should().Be(405);
        item.GetProperty("contentType").GetString().Should().Be("image/png");
        // Admins get identifiers, never a link to another user's private media.
        item.TryGetProperty("url", out _).Should().BeFalse();
    }
}
