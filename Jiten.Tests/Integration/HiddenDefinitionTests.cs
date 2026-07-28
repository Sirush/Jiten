using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class HiddenDefinitionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        userDb.UserHiddenDefinitions.RemoveRange(userDb.UserHiddenDefinitions);
        await userDb.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpResponseMessage> Update(string userId, int wordId, params int[] hiddenIndices)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/user/hidden-definitions/{wordId}")
            .WithUser(userId)
            .WithJsonContent(new { hiddenIndices });
        return await _client.SendAsync(request);
    }

    private async Task<UserHiddenDefinitionsDto> Get(string userId, int wordId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/user/hidden-definitions/{wordId}").WithUser(userId);
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<UserHiddenDefinitionsDto>())!;
    }

    [Fact]
    public async Task Get_WhenNoneHidden_ReturnsEmpty()
    {
        var dto = await Get(TestUsers.UserA, 100);
        dto.WordId.Should().Be(100);
        dto.HiddenIndices.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_StoresAndReadsBackSorted()
    {
        (await Update(TestUsers.UserA, 100, 3, 1)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Get(TestUsers.UserA, 100)).HiddenIndices.Should().Equal(1, 3);
    }

    [Fact]
    public async Task Update_ReplacesPreviousSelection()
    {
        await Update(TestUsers.UserA, 100, 1, 2);
        await Update(TestUsers.UserA, 100, 5);

        (await Get(TestUsers.UserA, 100)).HiddenIndices.Should().Equal(5);

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserHiddenDefinitions.CountAsync(e => e.WordId == 100)).Should().Be(1);
    }

    [Fact]
    public async Task Update_WithEmptySelection_RemovesRow()
    {
        await Update(TestUsers.UserA, 100, 1);
        await Update(TestUsers.UserA, 100);

        (await Get(TestUsers.UserA, 100)).HiddenIndices.Should().BeEmpty();

        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        (await userDb.UserHiddenDefinitions.AnyAsync(e => e.WordId == 100)).Should().BeFalse();
    }

    [Fact]
    public async Task Update_IgnoresIndicesOutsideMaskRange()
    {
        await Update(TestUsers.UserA, 100, 0, 2, 64, -1);

        (await Get(TestUsers.UserA, 100)).HiddenIndices.Should().Equal(2);
    }

    [Fact]
    public async Task HiddenDefinitions_AreIsolatedPerUser()
    {
        await Update(TestUsers.UserA, 100, 1);

        (await Get(TestUsers.UserB, 100)).HiddenIndices.Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthenticated_IsRejected()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/user/hidden-definitions/100");
        (await _client.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Batch_ReturnsIndicesByWordId()
    {
        await Update(TestUsers.UserA, 100, 1, 2);
        await Update(TestUsers.UserA, 200, 4);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/user/hidden-definitions/batch")
            .WithUser(TestUsers.UserA)
            .WithJsonContent(new[] { 100, 200, 300 });
        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var map = await response.Content.ReadFromJsonAsync<Dictionary<int, List<int>>>();
        map!.Should().HaveCount(2);
        map[100].Should().Equal(1, 2);
        map[200].Should().Equal(4);
    }
}
