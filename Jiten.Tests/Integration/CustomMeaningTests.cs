using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Core.Data.User;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CustomMeaningTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserCustomMeanings.RemoveRange(userDb.UserCustomMeanings);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> Upsert(string userId, int wordId, string text)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/user/custom-meanings/{wordId}")
            .WithUser(userId)
            .WithJsonContent(new { text });
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Get(string userId, int wordId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/custom-meanings/{wordId}").WithUser(userId);
        return await _client.SendAsync(request);
    }

    [Fact]
    public async Task Get_WhenNone_ReturnsNull()
    {
        var response = await Get(TestUsers.UserA, 100);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_CreatesThenReadsBack()
    {
        (await Upsert(TestUsers.UserA, 100, "my note")).StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await (await Get(TestUsers.UserA, 100)).Content.ReadFromJsonAsync<UserCustomMeaningDto>();
        dto!.Text.Should().Be("my note");
        dto.WordId.Should().Be(100);
    }

    [Fact]
    public async Task Upsert_Twice_UpdatesInPlace()
    {
        await Upsert(TestUsers.UserA, 100, "first");
        await Upsert(TestUsers.UserA, 100, "second");

        var dto = await (await Get(TestUsers.UserA, 100)).Content.ReadFromJsonAsync<UserCustomMeaningDto>();
        dto!.Text.Should().Be("second");

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserCustomMeanings.CountAsync(m => m.WordId == 100)).Should().Be(1);
    }

    [Fact]
    public async Task Upsert_TrimsAndRejectsEmpty()
    {
        (await Upsert(TestUsers.UserA, 100, "   ")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upsert_RejectsTooLong()
    {
        var tooLong = new string('あ', 501);
        (await Upsert(TestUsers.UserA, 100, tooLong)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_RemovesMeaning()
    {
        await Upsert(TestUsers.UserA, 100, "note");

        var del = new HttpRequestMessage(HttpMethod.Delete, "/api/user/custom-meanings/100").WithUser(TestUsers.UserA);
        (await _client.SendAsync(del)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await (await Get(TestUsers.UserA, 100)).Content.ReadAsStringAsync()).Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Meanings_AreIsolatedPerUser()
    {
        await Upsert(TestUsers.UserA, 100, "A's note");

        (await (await Get(TestUsers.UserB, 100)).Content.ReadAsStringAsync()).Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/custom-meanings/100");
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Batch_ReturnsMeaningsByWordId()
    {
        await Upsert(TestUsers.UserA, 100, "note 100");
        await Upsert(TestUsers.UserA, 200, "note 200");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/custom-meanings/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new[] { 100, 200, 300 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var map = await response.Content.ReadFromJsonAsync<Dictionary<int, string>>();
        map!.Should().HaveCount(2);
        map[100].Should().Be("note 100");
        map[200].Should().Be("note 200");
    }
}
