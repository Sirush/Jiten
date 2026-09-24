using System.Net;
using FluentAssertions;
using ImageMagick;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

/// <summary>Rows rewritten by the retired renormalize backfill still point at their original; replacing or deleting the media must take it too.</summary>
public class CardMediaRetainedOriginalTests(JitenWebApplicationFactory factory)
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

    private static byte[] Png(int w, int h)
    {
        using var image = new MagickImage(MagickColors.CornflowerBlue, (uint)w, (uint)h);
        image.AddNoise(NoiseType.Random);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private async Task<UserCardMedia> SeedRewrittenAsync(int wordId)
    {
        var cdn = factory.Services.GetRequiredService<StubCdnService>();
        var original = Png(640, 360);
        var current = Png(64, 36);

        var originalPath = CardMediaStorage.PathFor(TestUsers.UserA, wordId, 0, CardMediaKind.Image, "png");
        var currentPath = CardMediaStorage.PathFor(TestUsers.UserA, wordId, 0, CardMediaKind.Image, "webp");
        await cdn.UploadFile(original, originalPath, secure: true);
        await cdn.UploadFile(current, currentPath, secure: true);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        var row = new UserCardMedia
                  {
                      UserId = TestUsers.UserA,
                      WordId = wordId,
                      ReadingIndex = 0,
                      Kind = CardMediaKind.Image,
                      StoragePath = currentPath,
                      ContentType = "image/webp",
                      FileSizeBytes = current.Length,
                      PreviousStoragePath = originalPath,
                      PreviousContentType = "image/png",
                      PreviousFileSizeBytes = original.Length
                  };
        userDb.UserCardMedia.Add(row);
        await userDb.SaveChangesAsync();
        cdn.Deletions.Clear();
        return row;
    }

    [Fact]
    public async Task ReplacingRewrittenMedia_DeletesTheRetainedOriginal()
    {
        var seeded = await SeedRewrittenAsync(409);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/srs/card-media/409/0")
        {
            Content = new MultipartFormDataContent { { new ByteArrayContent(Png(200, 200)), "file", "x.png" } }
        }.WithUser(TestUsers.UserA);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Services.GetRequiredService<StubCdnService>().Deletions
               .Should().Contain(seeded.PreviousStoragePath!).And.Contain(seeded.StoragePath);

        using var scope = factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<UserDbContext>().UserCardMedia.AsNoTracking().FirstAsync(m => m.Id == seeded.Id);
        row.PreviousStoragePath.Should().BeNull();
        row.PreviousContentType.Should().BeNull();
        row.PreviousFileSizeBytes.Should().BeNull();
    }

    [Fact]
    public async Task DeletingRewrittenMedia_DeletesTheRetainedOriginal()
    {
        var seeded = await SeedRewrittenAsync(410);

        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/srs/card-media/410/0/image").WithUser(TestUsers.UserA);
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.OK);

        factory.Services.GetRequiredService<StubCdnService>().Deletions
               .Should().Contain(seeded.PreviousStoragePath!).And.Contain(seeded.StoragePath);
    }
}
