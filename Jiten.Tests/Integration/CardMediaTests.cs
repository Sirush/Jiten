using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ImageMagick;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.Billing;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CardMediaTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    // Minimal valid magic-byte headers padded past the 12-byte sniff floor. Not decodable images — used only
    // where the file content is irrelevant (audio, oversize gate). Real images are generated with Magick.
    private static readonly byte[] Mp3 = [0x49, 0x44, 0x33, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static byte[] RealPng(int w = 64, int h = 64) => RealImage(MagickFormat.Png, w, h);
    private static byte[] RealJpeg(int w = 64, int h = 64) => RealImage(MagickFormat.Jpeg, w, h);

    private static byte[] RealImage(MagickFormat format, int w, int h)
    {
        using var image = new MagickImage(MagickColors.Red, (uint)w, (uint)h);
        image.Format = format;
        return image.ToByteArray();
    }

    private static byte[] RealApng(int w = 50, int h = 50)
    {
        using var frames = new MagickImageCollection();
        frames.Add(new MagickImage(MagickColors.Red, (uint)w, (uint)h) { AnimationDelay = 10 });
        frames.Add(new MagickImage(MagickColors.Blue, (uint)w, (uint)h) { AnimationDelay = 10 });
        return frames.ToByteArray(MagickFormat.APng);
    }

    private static byte[] RealGif(int w = 50, int h = 50)
    {
        using var image = new MagickImage(MagickColors.Green, (uint)w, (uint)h);
        image.Format = MagickFormat.Gif;
        return image.ToByteArray();
    }

    /// <summary>Matches an uploaded path, whose kind segment carries a per-upload version suffix.</summary>
    private static bool IsUploadOf(string storagePath, CardMediaKind kind, string extension) =>
        storagePath.Contains($"_{kind.ToString().ToLowerInvariant()}_") && storagePath.EndsWith($".{extension}");

    private static bool IsWebp(byte[] bytes) =>
        bytes.Length >= 12 && Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF"
                           && Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP";

    private static byte[] JpegWithExif(int w, int h)
    {
        using var image = new MagickImage(MagickColors.Blue, (uint)w, (uint)h);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Copyright, "hidden-gps-secret");
        image.SetProfile(exif);
        image.Format = MagickFormat.Jpeg;
        return image.ToByteArray();
    }

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        userDb.UserCardMedia.RemoveRange(userDb.UserCardMedia);
        foreach (var user in await userDb.Users.ToListAsync())
        {
            user.StripeSubscriptionActive = false;
            user.IsLifetime = false;
            // UserA is the Full-tier actor for these tests; UserB stays free (tier none).
            user.AdminPremiumOverride = user.Id == TestUsers.UserA;
        }
        await userDb.SaveChangesAsync();

        var jiten = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        jiten.WordForms.RemoveRange(jiten.WordForms);
        jiten.JMDictWords.RemoveRange(jiten.JMDictWords.Where(w => w.WordId == 200 || w.WordId == 300));
        await jiten.SaveChangesAsync();
        // Parent word rows so the WordForms FK is satisfied.
        jiten.JMDictWords.AddRange(new JmDictWord { WordId = 200 }, new JmDictWord { WordId = 300 });
        // word 200: two kana readings -> audio must not inherit across forms.
        jiten.WordForms.AddRange(
            Form(200, 0, "会明", JmDictFormType.KanjiForm),
            Form(200, 1, "あした", JmDictFormType.KanaForm),
            Form(200, 2, "あす", JmDictFormType.KanaForm),
            // word 300: one kana reading -> audio may inherit.
            Form(300, 0, "会う", JmDictFormType.KanjiForm),
            Form(300, 1, "あう", JmDictFormType.KanaForm));
        await jiten.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        foreach (var id in new[] { TestUsers.UserA, TestUsers.UserB, TestUsers.Admin })
            jitenPlus.InvalidateTier(id);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static JmDictWordForm Form(int wordId, short ri, string text, JmDictFormType type) =>
        new() { WordId = wordId, ReadingIndex = ri, Text = text, RubyText = text, FormType = type };

    private static MultipartFormDataContent FileBody(byte[] bytes, string filename = "upload.bin")
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(bytes), "file", filename);
        return content;
    }

    private async Task<HttpResponseMessage> Upload(string userId, int wordId, int readingIndex, byte[] bytes, string filename = "upload.bin")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/card-media/{wordId}/{readingIndex}")
        {
            Content = FileBody(bytes, filename)
        }.WithUser(userId);
        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> Batch(string userId, params (int wordId, int readingIndex)[] items)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-media/batch")
            .WithUser(userId)
            .WithJsonContent(new { items = items.Select(i => new { wordId = i.wordId, readingIndex = i.readingIndex }) });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Upload_ImageAndAudio_HappyPath()
    {
        var imgResp = await Upload(TestUsers.UserA, 100, 0, RealPng());
        imgResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var imgBody = await imgResp.Content.ReadFromJsonAsync<JsonElement>();
        imgBody.GetProperty("media").GetProperty("kind").GetString().Should().Be("image");
        // PNG is normalized to WebP on the way in.
        imgBody.GetProperty("media").GetProperty("contentType").GetString().Should().Be("image/webp");
        imgBody.GetProperty("media").GetProperty("inherited").GetBoolean().Should().BeFalse();
        var imageProcessedBytes = imgBody.GetProperty("quota").GetProperty("usedBytes").GetInt64();
        imageProcessedBytes.Should().BeGreaterThan(0);

        var audResp = await Upload(TestUsers.UserA, 100, 0, Mp3);
        audResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var audBody = await audResp.Content.ReadFromJsonAsync<JsonElement>();
        audBody.GetProperty("media").GetProperty("kind").GetString().Should().Be("audio");
        audBody.GetProperty("media").GetProperty("contentType").GetString().Should().Be("audio/mpeg");
        // image (processed) + audio (untouched) counted together
        audBody.GetProperty("quota").GetProperty("usedBytes").GetInt64().Should().Be(imageProcessedBytes + Mp3.Length);
    }

    [Fact]
    public async Task Upload_LargeImage_NormalizedToWebp_Downscaled()
    {
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Uploads.Clear();

        var resp = await Upload(TestUsers.UserA, 101, 0, RealPng(2400, 1000), "big.png");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("media").GetProperty("contentType").GetString().Should().Be("image/webp");

        // Re-read the exact bytes recorded by the stub CDN: WebP, long edge downscaled to <= 1600.
        var stored = cdn.Uploads.Last(u => IsUploadOf(u.FileName, CardMediaKind.Image, "webp"));
        using var storedImage = new MagickImage(stored.File);
        storedImage.Format.Should().Be(MagickFormat.WebP);
        Math.Max(storedImage.Width, storedImage.Height).Should().BeLessThanOrEqualTo(1600);
        // aspect preserved: 2400x1000 -> 1600x666(ish)
        storedImage.Width.Should().Be(1600u);

        // FileSizeBytes reflects the processed file.
        body.GetProperty("media").GetProperty("fileSizeBytes").GetInt64().Should().Be(stored.File.Length);
        body.GetProperty("quota").GetProperty("usedBytes").GetInt64().Should().Be(stored.File.Length);
    }

    [Fact]
    public async Task Upload_SmallImage_BecomesWebp_NoUpscale_ExifStripped()
    {
        var input = JpegWithExif(100, 80);
        // sanity: the input actually carries EXIF we expect to be stripped.
        using (var inImage = new MagickImage(input))
            inImage.GetExifProfile().Should().NotBeNull();

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Uploads.Clear();

        var resp = await Upload(TestUsers.UserA, 108, 0, input, "small.jpg");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("media").GetProperty("contentType").GetString().Should().Be("image/webp");

        var stored = cdn.Uploads.Last(u => IsUploadOf(u.FileName, CardMediaKind.Image, "webp"));
        using var storedImage = new MagickImage(stored.File);
        storedImage.Format.Should().Be(MagickFormat.WebP);
        // never upscaled: dimensions unchanged
        storedImage.Width.Should().Be(100u);
        storedImage.Height.Should().Be(80u);
        // EXIF stripped for privacy
        storedImage.GetExifProfile().Should().BeNull();
    }

    [Fact]
    public async Task Upload_Gif_NormalizedToWebp()
    {
        var gif = RealGif();
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Uploads.Clear();

        var resp = await Upload(TestUsers.UserA, 109, 0, gif, "anim.gif");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("media").GetProperty("contentType").GetString().Should().Be("image/webp");

        var stored = cdn.Uploads.Last(u => IsUploadOf(u.FileName, CardMediaKind.Image, "webp"));
        stored.File.Should().NotEqual(gif); // re-encoded to WebP, not passed through
        IsWebp(stored.File).Should().BeTrue();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var row = await userDb.UserCardMedia.FirstAsync(m => m.UserId == TestUsers.UserA && m.WordId == 109);
        IsUploadOf(row.StoragePath, CardMediaKind.Image, "webp").Should().BeTrue();
        row.ContentType.Should().Be("image/webp");
    }

    /// <summary>
    /// An animated upload must never come back as a single frame. Whether it can be re-encoded at all is
    /// platform-dependent - ImageMagick's APNG coder is an ffmpeg delegate on Linux - so the invariant is
    /// "converted with its frames, or kept exactly as uploaded", never "silently flattened".
    /// </summary>
    [Fact]
    public async Task Upload_AnimatedPng_IsNeverFlattened()
    {
        var apng = RealApng();
        if (apng.Length == 0)
            return; // no APNG encoder here, so there is nothing to upload

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Uploads.Clear();

        var resp = await Upload(TestUsers.UserA, 111, 0, apng, "anim.png");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var contentType = (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("media").GetProperty("contentType").GetString();

        var stored = cdn.Uploads.Last(u => u.FileName.Contains("_image_")).File;

        if (contentType == "image/webp")
        {
            using var storedFrames = new MagickImageCollection(stored);
            storedFrames.Count.Should().BeGreaterThan(1);
        }
        else
        {
            contentType.Should().Be("image/png");
            stored.Should().Equal(apng);
        }
    }

    [Fact]
    public async Task Upload_Replace_ExtensionChange_DeletesOldCdnFile()
    {
        // An undecodable-but-valid PNG falls back to its original extension (stored as .png); replacing it with a
        // decodable image normalizes to .webp. The old CDN file must be deleted; one row remains.
        byte[] fallbackPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0];
        await Upload(TestUsers.UserA, 110, 0, fallbackPng, "broken.png");
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var resp = await Upload(TestUsers.UserA, 110, 0, RealPng());
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        cdn.Deletions.Should().Contain(p => IsUploadOf(p, CardMediaKind.Image, "png"));

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var rows = await userDb.UserCardMedia.Where(m => m.UserId == TestUsers.UserA && m.WordId == 110).ToListAsync();
        rows.Should().HaveCount(1);
        IsUploadOf(rows[0].StoragePath, CardMediaKind.Image, "webp").Should().BeTrue();
    }

    [Fact]
    public async Task Upload_Replace_SameExtension_TakesNewPathAndDeletesOldFile()
    {
        // Both images normalize to WebP, so only the version suffix distinguishes them. The replacement must
        // still land on a fresh path (nothing cached can be served for it) and orphan exactly one old file.
        await Upload(TestUsers.UserA, 111, 0, RealPng());
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        string oldPath;
        using (var before = factory.Services.CreateScope())
        {
            var db = before.ServiceProvider.GetRequiredService<UserDbContext>();
            oldPath = (await db.UserCardMedia.FirstAsync(m => m.UserId == TestUsers.UserA && m.WordId == 111)).StoragePath;
        }

        var resp = await Upload(TestUsers.UserA, 111, 0, RealJpeg());
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        cdn.Deletions.Should().ContainSingle().Which.Should().Be(oldPath);
        cdn.Purges.Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var rows = await userDb.UserCardMedia.Where(m => m.UserId == TestUsers.UserA && m.WordId == 111).ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].StoragePath.Should().NotBe(oldPath);
        IsUploadOf(rows[0].StoragePath, CardMediaKind.Image, "webp").Should().BeTrue();
    }

    [Fact]
    public async Task Upload_ProcessingFailure_FallsBackToOriginal()
    {
        // Valid PNG magic bytes but undecodable content: Magick throws, so the original is stored unmodified.
        byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0];
        var resp = await Upload(TestUsers.UserA, 112, 0, fakePng, "broken.png");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("media").GetProperty("contentType").GetString().Should().Be("image/png");
        body.GetProperty("media").GetProperty("fileSizeBytes").GetInt64().Should().Be(fakePng.Length);
    }

    [Fact]
    public async Task Upload_MagicByteMismatch_Rejected()
    {
        var textBytes = Encoding.ASCII.GetBytes("this is definitely not a real image file at all");
        var resp = await Upload(TestUsers.UserA, 102, 0, textBytes, "fake.png");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_OverFiveMB_Rejected()
    {
        // Size is gated before sniff/processing; a valid PNG signature keeps it a plausible upload.
        var big = new byte[5 * 1024 * 1024 + 50];
        byte[] pngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Array.Copy(pngSig, big, pngSig.Length);
        var resp = await Upload(TestUsers.UserA, 103, 0, big, "big.png");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_FreeUser_Returns403WithJitenPlusPayload()
    {
        var resp = await Upload(TestUsers.UserB, 104, 0, RealPng());
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("jitenPlus").GetBoolean().Should().BeTrue();
        body.GetProperty("requiredTier").GetString().Should().Be("trial");
    }

    [Fact]
    public async Task Delete_FreeUser_CanDeleteOwnMedia()
    {
        // Deletion is not tier-gated: a lapsed/free user must always be able to remove their own media.
        // Upload is tier-gated, so seed UserB's row directly.
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.UserCardMedia.Add(new UserCardMedia
            {
                UserId = TestUsers.UserB,
                WordId = 105,
                ReadingIndex = 0,
                Kind = CardMediaKind.Image,
                StoragePath = $"card-media/{TestUsers.UserB}/105_0_image.png",
                ContentType = "image/png",
                FileSizeBytes = 16,
                CreatedAt = DateTime.UtcNow
            });
            await userDb.SaveChangesAsync();
        }

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media/105/0/image").WithUser(TestUsers.UserB);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        cdn.Deletions.Should().Contain(p => p.EndsWith("105_0_image.png"));

        using var verifyScope = factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await db.UserCardMedia.AnyAsync(m => m.UserId == TestUsers.UserB && m.WordId == 105)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_RemovesRowAndCdnFile_ReturnsQuota()
    {
        await Upload(TestUsers.UserA, 106, 0, RealPng());
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media/106/0/image").WithUser(TestUsers.UserA);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("quota").GetProperty("usedBytes").GetInt64().Should().Be(0);

        cdn.Deletions.Should().Contain(p => IsUploadOf(p, CardMediaKind.Image, "webp"));

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserCardMedia.AnyAsync(m => m.WordId == 106)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_Missing_Returns404()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media/999/0/image").WithUser(TestUsers.UserA);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_InvalidKind_Returns400()
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media/100/0/video").WithUser(TestUsers.UserA);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Batch_ReturnsOnlyOwnMedia()
    {
        await Upload(TestUsers.UserA, 100, 0, RealPng());

        var mine = await Batch(TestUsers.UserA, (100, 0));
        mine.GetProperty("items")[0].GetProperty("image").ValueKind.Should().NotBe(JsonValueKind.Null);

        var others = await Batch(TestUsers.UserB, (100, 0));
        others.GetProperty("items")[0].GetProperty("image").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Batch_ImageInheritsAcrossSiblingForms()
    {
        await Upload(TestUsers.UserA, 100, 0, RealPng());

        var body = await Batch(TestUsers.UserA, (100, 1));
        var image = body.GetProperty("items")[0].GetProperty("image");
        image.ValueKind.Should().NotBe(JsonValueKind.Null);
        image.GetProperty("inherited").GetBoolean().Should().BeTrue();
        image.GetProperty("sourceReadingIndex").GetInt32().Should().Be(0);
        image.GetProperty("url").GetString().Should().StartWith("https://stub-cdn/");
    }

    [Fact]
    public async Task Batch_AudioDoesNotInherit_WhenMultipleKanaReadings()
    {
        // word 200 has two kana readings, so pronunciation is ambiguous.
        await Upload(TestUsers.UserA, 200, 0, Mp3);

        var body = await Batch(TestUsers.UserA, (200, 1));
        body.GetProperty("items")[0].GetProperty("audio").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Batch_AudioInherits_WhenSingleKanaReading()
    {
        // word 300 has exactly one kana reading, so all forms share pronunciation.
        await Upload(TestUsers.UserA, 300, 0, Mp3);

        var body = await Batch(TestUsers.UserA, (300, 1));
        var audio = body.GetProperty("items")[0].GetProperty("audio");
        audio.ValueKind.Should().NotBe(JsonValueKind.Null);
        audio.GetProperty("inherited").GetBoolean().Should().BeTrue();
        audio.GetProperty("sourceReadingIndex").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Media_SurvivesSrsCardDeletion()
    {
        await Upload(TestUsers.UserA, 100, 0, RealPng());

        // Seed an SRS card for the same (word, form) so the clear-all endpoint has something to delete.
        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, 100, 0));
            await userDb.SaveChangesAsync();
        }

        // Delete all of the user's SRS cards via the real endpoint.
        var clear = new HttpRequestMessage(HttpMethod.Delete, "/api/user/vocabulary/known-ids/clear").WithUser(TestUsers.UserA);
        (await _client.SendAsync(clear)).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = factory.Services.CreateScope())
        {
            var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
            (await userDb.FsrsCards.AnyAsync(c => c.UserId == TestUsers.UserA)).Should().BeFalse();
        }

        // Media is keyed by (user, word, form) with no FK to FsrsCard, so it survives.
        var body = await Batch(TestUsers.UserA, (100, 0));
        body.GetProperty("items")[0].GetProperty("image").ValueKind.Should().NotBe(JsonValueKind.Null);
    }

    // ---- Manage / delete-all ------------------------------------------------

    private async Task SeedMedia(string userId, int wordId, byte ri, CardMediaKind kind, long bytes, DateTime? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        db.UserCardMedia.Add(new UserCardMedia
        {
            UserId = userId,
            WordId = wordId,
            ReadingIndex = ri,
            Kind = kind,
            StoragePath = $"card-media/{userId}/{wordId}_{ri}_{kind.ToString().ToLowerInvariant()}.webp",
            ContentType = kind == CardMediaKind.Image ? "image/webp" : "audio/mpeg",
            FileSizeBytes = bytes,
            CreatedAt = createdAt ?? DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonElement> Manage(string userId, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/srs/card-media/manage{query}").WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Manage_GroupsRows_WithTotalBytesAndSummary()
    {
        // word 300 form 0: image only (200 bytes). word 100 form 0: image (100) + audio (50).
        await SeedMedia(TestUsers.UserA, 300, 0, CardMediaKind.Image, 200);
        await SeedMedia(TestUsers.UserA, 100, 0, CardMediaKind.Image, 100);
        await SeedMedia(TestUsers.UserA, 100, 0, CardMediaKind.Audio, 50);

        var body = await Manage(TestUsers.UserA);

        var summary = body.GetProperty("summary");
        summary.GetProperty("totalForms").GetInt32().Should().Be(2);
        summary.GetProperty("imageCount").GetInt32().Should().Be(2);
        summary.GetProperty("imageBytes").GetInt64().Should().Be(300);
        summary.GetProperty("audioCount").GetInt32().Should().Be(1);
        summary.GetProperty("audioBytes").GetInt64().Should().Be(50);
        summary.GetProperty("usedBytes").GetInt64().Should().Be(350);

        var items = body.GetProperty("items");
        items.GetArrayLength().Should().Be(2);

        // Default sort is size desc, so word 300 (200 total) leads word 100 (150 total).
        var first = items[0];
        first.GetProperty("wordId").GetInt32().Should().Be(300);
        first.GetProperty("totalBytes").GetInt64().Should().Be(200);
        first.GetProperty("image").ValueKind.Should().NotBe(JsonValueKind.Null);
        first.GetProperty("audio").ValueKind.Should().Be(JsonValueKind.Null);
        // WordForms seeds a form for word 300 reading 0, so its text resolves.
        first.GetProperty("wordText").GetString().Should().Be("会う");

        var second = items[1];
        second.GetProperty("wordId").GetInt32().Should().Be(100);
        second.GetProperty("totalBytes").GetInt64().Should().Be(150);
        second.GetProperty("image").GetProperty("fileSizeBytes").GetInt64().Should().Be(100);
        second.GetProperty("audio").GetProperty("fileSizeBytes").GetInt64().Should().Be(50);
        // No WordForms row seeded for word 100 -> graceful empty text, never a throw.
        second.GetProperty("wordText").GetString().Should().Be("");
        second.GetProperty("image").GetProperty("url").GetString().Should().StartWith("https://stub-cdn/");
    }

    [Fact]
    public async Task Manage_Pagination_SecondPage()
    {
        for (var i = 0; i < 60; i++)
            await SeedMedia(TestUsers.UserA, 1000 + i, 0, CardMediaKind.Image, 100 + i);

        var page1 = await Manage(TestUsers.UserA, "?page=1");
        page1.GetProperty("items").GetArrayLength().Should().Be(50);
        page1.GetProperty("totalForms").GetInt32().Should().Be(60);
        page1.GetProperty("pageSize").GetInt32().Should().Be(50);

        var page2 = await Manage(TestUsers.UserA, "?page=2");
        page2.GetProperty("items").GetArrayLength().Should().Be(10);
        page2.GetProperty("page").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Manage_KindFilter_ImageOnly()
    {
        await SeedMedia(TestUsers.UserA, 410, 0, CardMediaKind.Image, 100);
        await SeedMedia(TestUsers.UserA, 411, 0, CardMediaKind.Audio, 50);
        await SeedMedia(TestUsers.UserA, 412, 0, CardMediaKind.Image, 200);
        await SeedMedia(TestUsers.UserA, 412, 0, CardMediaKind.Audio, 30);

        var body = await Manage(TestUsers.UserA, "?kind=image");
        var items = body.GetProperty("items");
        // Only forms with an image show, and the audio is excluded from those rows.
        items.GetArrayLength().Should().Be(2);
        body.GetProperty("totalForms").GetInt32().Should().Be(2);
        foreach (var item in items.EnumerateArray())
        {
            item.GetProperty("image").ValueKind.Should().NotBe(JsonValueKind.Null);
            item.GetProperty("audio").ValueKind.Should().Be(JsonValueKind.Null);
        }

        // Summary still reports totals across ALL kinds regardless of the filter.
        body.GetProperty("summary").GetProperty("audioCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Manage_SortByDate()
    {
        var now = DateTime.UtcNow;
        await SeedMedia(TestUsers.UserA, 420, 0, CardMediaKind.Image, 10, now.AddDays(-2));
        await SeedMedia(TestUsers.UserA, 421, 0, CardMediaKind.Image, 10, now.AddDays(-1));
        await SeedMedia(TestUsers.UserA, 422, 0, CardMediaKind.Image, 10, now);

        var newest = await Manage(TestUsers.UserA, "?sort=date_desc");
        newest.GetProperty("items")[0].GetProperty("wordId").GetInt32().Should().Be(422);

        var oldest = await Manage(TestUsers.UserA, "?sort=date_asc");
        oldest.GetProperty("items")[0].GetProperty("wordId").GetInt32().Should().Be(420);
    }

    [Fact]
    public async Task Manage_SortBySizeDesc_IsDefault()
    {
        await SeedMedia(TestUsers.UserA, 401, 0, CardMediaKind.Image, 10);
        await SeedMedia(TestUsers.UserA, 402, 0, CardMediaKind.Image, 900);
        await SeedMedia(TestUsers.UserA, 403, 0, CardMediaKind.Image, 300);

        var body = await Manage(TestUsers.UserA);
        var items = body.GetProperty("items");
        items[0].GetProperty("wordId").GetInt32().Should().Be(402);
        items[1].GetProperty("wordId").GetInt32().Should().Be(403);
        items[2].GetProperty("wordId").GetInt32().Should().Be(401);
    }

    [Fact]
    public async Task Manage_OwnershipIsolation()
    {
        await SeedMedia(TestUsers.UserA, 500, 0, CardMediaKind.Image, 100);

        var body = await Manage(TestUsers.UserB);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("totalForms").GetInt32().Should().Be(0);
        body.GetProperty("summary").GetProperty("usedBytes").GetInt64().Should().Be(0);
    }

    [Fact]
    public async Task Manage_ZeroMedia_EmptyAndZeroSummary()
    {
        var body = await Manage(TestUsers.UserA);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
        body.GetProperty("totalForms").GetInt32().Should().Be(0);
        var summary = body.GetProperty("summary");
        summary.GetProperty("totalForms").GetInt32().Should().Be(0);
        summary.GetProperty("imageCount").GetInt32().Should().Be(0);
        summary.GetProperty("audioCount").GetInt32().Should().Be(0);
        summary.GetProperty("usedBytes").GetInt64().Should().Be(0);
        summary.GetProperty("maxBytes").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Manage_FreeUser_NotTierGated()
    {
        // UserB is free (tier none); the manage read must still succeed.
        await SeedMedia(TestUsers.UserB, 600, 0, CardMediaKind.Image, 42);

        var body = await Manage(TestUsers.UserB);
        body.GetProperty("items").GetArrayLength().Should().Be(1);
        body.GetProperty("summary").GetProperty("usedBytes").GetInt64().Should().Be(42);
    }

    [Fact]
    public async Task DeleteAll_RemovesRowsAndCdnFiles_ReturnsZeroQuota()
    {
        await SeedMedia(TestUsers.UserA, 700, 0, CardMediaKind.Image, 100);
        await SeedMedia(TestUsers.UserA, 700, 0, CardMediaKind.Audio, 50);
        await SeedMedia(TestUsers.UserA, 701, 0, CardMediaKind.Image, 200);
        await SeedMedia(TestUsers.UserB, 800, 0, CardMediaKind.Image, 999);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media").WithUser(TestUsers.UserA);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("quota").GetProperty("usedBytes").GetInt64().Should().Be(0);

        // One CDN deletion per owned file (3), and UserB's file left alone.
        cdn.Deletions.Count(p => p.Contains($"/{TestUsers.UserA}/")).Should().Be(3);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await db.UserCardMedia.AnyAsync(m => m.UserId == TestUsers.UserA)).Should().BeFalse();
        (await db.UserCardMedia.CountAsync(m => m.UserId == TestUsers.UserB)).Should().Be(1);
    }

    [Fact]
    public async Task DeleteAll_FreeUser_CanClearOwnMedia()
    {
        // Deletion is not tier-gated: UserB (free) must be able to clear their own storage.
        await SeedMedia(TestUsers.UserB, 900, 0, CardMediaKind.Image, 100);

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media").WithUser(TestUsers.UserB);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await db.UserCardMedia.AnyAsync(m => m.UserId == TestUsers.UserB)).Should().BeFalse();
    }

    [Fact]
    public async Task Summary_ReturnsPerKindTotals()
    {
        await SeedMedia(TestUsers.UserA, 1100, 0, CardMediaKind.Image, 100);
        await SeedMedia(TestUsers.UserA, 1100, 0, CardMediaKind.Audio, 40);
        await SeedMedia(TestUsers.UserA, 1101, 0, CardMediaKind.Image, 200);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/srs/card-media/summary").WithUser(TestUsers.UserA);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("totalForms").GetInt32().Should().Be(2);
        body.GetProperty("imageCount").GetInt32().Should().Be(2);
        body.GetProperty("imageBytes").GetInt64().Should().Be(300);
        body.GetProperty("audioCount").GetInt32().Should().Be(1);
        body.GetProperty("audioBytes").GetInt64().Should().Be(40);
        body.GetProperty("usedBytes").GetInt64().Should().Be(340);
        body.GetProperty("maxBytes").GetInt64().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Summary_FreeUser_NotTierGated()
    {
        await SeedMedia(TestUsers.UserB, 1200, 0, CardMediaKind.Image, 42);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/srs/card-media/summary").WithUser(TestUsers.UserB);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("usedBytes").GetInt64().Should().Be(42);
    }

    private async Task<HttpResponseMessage> DeleteBatch(string userId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-media/delete-batch")
            .WithUser(userId)
            .WithJsonContent(body);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task DeleteBatch_DeletesOnlyListedItems()
    {
        await SeedMedia(TestUsers.UserA, 1300, 0, CardMediaKind.Image, 100);
        await SeedMedia(TestUsers.UserA, 1300, 0, CardMediaKind.Audio, 50);
        await SeedMedia(TestUsers.UserA, 1301, 0, CardMediaKind.Image, 200);

        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        cdn.Deletions.Clear();

        // Delete only word 1300's image and word 1301's image; word 1300's audio stays.
        var resp = await DeleteBatch(TestUsers.UserA, new
        {
            items = new[]
            {
                new { wordId = 1300, readingIndex = 0, kind = "image" },
                new { wordId = 1301, readingIndex = 0, kind = "image" }
            }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("deleted").GetInt32().Should().Be(2);
        body.GetProperty("quota").GetProperty("usedBytes").GetInt64().Should().Be(50);
        cdn.Deletions.Count(p => p.EndsWith("_image.webp")).Should().Be(2);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var remaining = await db.UserCardMedia.Where(m => m.UserId == TestUsers.UserA).ToListAsync();
        remaining.Should().ContainSingle();
        remaining[0].WordId.Should().Be(1300);
        remaining[0].Kind.Should().Be(CardMediaKind.Audio);
    }

    [Fact]
    public async Task DeleteBatch_IgnoresOtherUsersItems()
    {
        await SeedMedia(TestUsers.UserA, 1400, 0, CardMediaKind.Image, 100);
        await SeedMedia(TestUsers.UserB, 1400, 0, CardMediaKind.Image, 100);

        // UserA lists a target that matches UserB's (word, form, kind); it must be ignored, not deleted.
        var resp = await DeleteBatch(TestUsers.UserA, new
        {
            items = new[] { new { wordId = 1400, readingIndex = 0, kind = "image" } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("deleted").GetInt32().Should().Be(1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await db.UserCardMedia.AnyAsync(m => m.UserId == TestUsers.UserA && m.WordId == 1400)).Should().BeFalse();
        (await db.UserCardMedia.AnyAsync(m => m.UserId == TestUsers.UserB && m.WordId == 1400)).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteBatch_OverCap_Rejected()
    {
        var items = Enumerable.Range(0, 201).Select(i => new { wordId = 2000 + i, readingIndex = 0, kind = "image" }).ToArray();
        var resp = await DeleteBatch(TestUsers.UserA, new { items });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteBatch_InvalidKind_Rejected()
    {
        var resp = await DeleteBatch(TestUsers.UserA, new
        {
            items = new[] { new { wordId = 1500, readingIndex = 0, kind = "video" } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteBatch_FreeUser_CanDeleteOwn()
    {
        await SeedMedia(TestUsers.UserB, 1600, 0, CardMediaKind.Image, 100);

        var resp = await DeleteBatch(TestUsers.UserB, new
        {
            items = new[] { new { wordId = 1600, readingIndex = 0, kind = "image" } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await db.UserCardMedia.AnyAsync(m => m.UserId == TestUsers.UserB)).Should().BeFalse();
    }
}

/// <summary>
/// Quota rejection uses a factory with lowered <c>JitenPlus:CardMediaStorage</c> allowances so a small file
/// trips the limit without uploading gigabytes, and Trial sits below Full so the two are distinguishable.
/// The config is host-scoped (not a global env var) to avoid leaking into other test classes running in parallel.
/// </summary>
public class LowQuotaWebApplicationFactory : JitenWebApplicationFactory
{
    public const long FullBytes = 8;
    public const long TrialBytes = 4;

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["JitenPlus:CardMediaStorage:FullBytes"] = FullBytes.ToString(),
                ["JitenPlus:CardMediaStorage:TrialBytes"] = TrialBytes.ToString()
            }));
        base.ConfigureWebHost(builder);
    }
}

public class CardMediaQuotaTests(LowQuotaWebApplicationFactory factory)
    : IClassFixture<LowQuotaWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserCardMedia.RemoveRange(userDb.UserCardMedia);
        userDb.UserPromoCredits.RemoveRange(userDb.UserPromoCredits);
        var userA = await userDb.Users.FirstAsync(u => u.Id == TestUsers.UserA);
        userA.AdminPremiumOverride = true;
        await userDb.SaveChangesAsync();

        var jitenPlus = scope.ServiceProvider.GetRequiredService<IJitenPlusService>();
        jitenPlus.InvalidateTier(TestUsers.UserA);
        jitenPlus.InvalidateTier(TestUsers.UserB);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Trial comes only from an unconsumed non-Full promo credit.</summary>
    private async Task MakeTrial(string userId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();

        var code = new PromoCode { Code = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), DurationDays = 7 };
        userDb.PromoCodes.Add(code);
        await userDb.SaveChangesAsync();

        userDb.UserPromoCredits.Add(new UserPromoCredit
        {
            UserId = userId,
            PromoCodeId = code.CodeId,
            GrantsFullTier = false,
            RemainingDays = 7,
            GrantedAt = DateTime.UtcNow
        });
        await userDb.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IJitenPlusService>().InvalidateTier(userId);
    }

    private Task<HttpResponseMessage> Upload(string userId, int wordId)
    {
        using var image = new MagickImage(MagickColors.Red, 32, 32) { Format = MagickFormat.Png };
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(image.ToByteArray()), "file", "x.png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/srs/card-media/{wordId}/0") { Content = content }
            .WithUser(userId);
        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Upload_OverQuota_Rejected()
    {
        // Even a tiny real image exceeds the lowered allowance once processed.
        var resp = await Upload(TestUsers.UserA, 100);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("maxBytes").GetInt64().Should().Be(LowQuotaWebApplicationFactory.FullBytes);
    }

    [Fact]
    public async Task Upload_TrialUser_IsAllowed_AtTheTrialAllowance()
    {
        await MakeTrial(TestUsers.UserB);

        var resp = await Upload(TestUsers.UserB, 101);

        // Not a 403: Trial may upload. It fails on the lowered byte allowance, which is the Trial one.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("maxBytes").GetInt64().Should().Be(LowQuotaWebApplicationFactory.TrialBytes);
    }

    [Fact]
    public async Task Status_ReportsZeroAllowanceForLapsedUser()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/jiten-plus/status").WithUser(TestUsers.UserB);
        var resp = await _client.SendAsync(request);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var quota = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quota");
        quota.GetProperty("maxBytes").GetInt64().Should().Be(0);
        quota.GetProperty("allowances").GetProperty("trialBytes").GetInt64().Should().Be(LowQuotaWebApplicationFactory.TrialBytes);
        quota.GetProperty("allowances").GetProperty("fullBytes").GetInt64().Should().Be(LowQuotaWebApplicationFactory.FullBytes);
    }
}
