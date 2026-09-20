using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data.FSRS;
using Jiten.Core.Data.JMDict;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ImportStateAndMassActionDateTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.FsrsReviewLogs.ExecuteDeleteAsync();
        await userDb.FsrsCards.ExecuteDeleteAsync();
        await userDb.FsrsCardArchives.ExecuteDeleteAsync();
        await userDb.UserFsrsSettings.ExecuteDeleteAsync();

        var jitenDb = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        if (!await jitenDb.WordForms.AnyAsync(wf => wf.WordId == 910))
        {
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 910, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 910, ReadingIndex = 0, Text = "猫", RubyText = "猫[ねこ]", FormType = JmDictFormType.KanjiForm });
            jitenDb.JMDictWords.Add(new JmDictWord { WordId = 911, PartsOfSpeech = ["noun"] });
            jitenDb.WordForms.Add(new JmDictWordForm { WordId = 911, ReadingIndex = 0, Text = "鳥", RubyText = "鳥[とり]", FormType = JmDictFormType.KanjiForm });
            await jitenDb.SaveChangesAsync();
        }
        scope.ServiceProvider.GetRequiredService<IWordFormSiblingCache>().Reload();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<FsrsCard> Card(int wordId)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.FsrsCards.AsNoTracking().FirstAsync(c => c.UserId == TestUsers.UserA && c.WordId == wordId);
    }

    [Fact]
    public async Task ImportFromIds_SuspendedWords_HaveNoLastReview_SoTheyAreNotKnown()
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/vocabulary/import-from-ids")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { wordIds = new[] { 910 }, suspendedWordIds = new[] { 911 } }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var mastered = await Card(910);
        mastered.State.Should().Be(FsrsState.Mastered);
        mastered.LastReview.Should().NotBeNull();

        var suspended = await Card(911);
        suspended.State.Should().Be(FsrsState.Suspended);
        suspended.LastReview.Should().BeNull("a suspended card with a LastReview derives as Young and inflates coverage");
    }

    private async Task SeedCreatedAt(int wordId, DateTime createdAtUtc)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.FsrsCards.Add(new FsrsCard(TestUsers.UserA, wordId, 0, state: FsrsState.Review, stability: 10, difficulty: 5,
                                          due: createdAtUtc.AddDays(10), lastReview: createdAtUtc) { CreatedAt = createdAtUtc });
        await userDb.SaveChangesAsync();
    }

    private async Task SetTimezone(string timezone)
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserFsrsSettings.Add(new UserFsrsSettings
                                    {
                                        UserId = TestUsers.UserA,
                                        SettingsJson = JsonSerializer.Serialize(new StudySettingsDto { Timezone = timezone })
                                    });
        await userDb.SaveChangesAsync();
    }

    private async Task<int> PreviewCount(string dateFrom, string dateTo)
    {
        var response = await _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/srs/mass-action/preview")
                                               .WithUser(TestUsers.UserA)
                                               .WithJsonContent(new { action = "delete-cards", dateType = "created", dateFrom, dateTo, limit = 50 }));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("totalItems").GetInt32();
    }

    // Tokyo is UTC+9: a card made at 20:00 UTC on the 9th belongs to the 10th there, and one at 14:00 UTC on the 10th to the 10th too.
    [Fact]
    public async Task MassActionDateFilter_UsesTheStudyTimezoneCalendarDay()
    {
        await SetTimezone("Asia/Tokyo");
        await SeedCreatedAt(1, new DateTime(2025, 3, 9, 20, 0, 0, DateTimeKind.Utc));  // 10 Mar 05:00 JST
        await SeedCreatedAt(2, new DateTime(2025, 3, 10, 14, 0, 0, DateTimeKind.Utc)); // 10 Mar 23:00 JST
        await SeedCreatedAt(3, new DateTime(2025, 3, 10, 16, 0, 0, DateTimeKind.Utc)); // 11 Mar 01:00 JST

        (await PreviewCount("2025-03-10", "2025-03-10")).Should().Be(2);
        (await PreviewCount("2025-03-11", "2025-03-11")).Should().Be(1);
        (await PreviewCount("2025-03-09", "2025-03-09")).Should().Be(0);
    }

    [Fact]
    public async Task MassActionDateFilter_WithoutATimezone_UsesUtcDays()
    {
        await SeedCreatedAt(1, new DateTime(2025, 3, 9, 20, 0, 0, DateTimeKind.Utc));
        await SeedCreatedAt(2, new DateTime(2025, 3, 10, 14, 0, 0, DateTimeKind.Utc));

        (await PreviewCount("2025-03-10", "2025-03-10")).Should().Be(1);
        (await PreviewCount("2025-03-09", "2025-03-10")).Should().Be(2);
    }
}
