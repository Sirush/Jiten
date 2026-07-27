using System.Net;
using FluentAssertions;
using Jiten.Core;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class CustomSentenceDedupeTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public async Task InitializeAsync()
    {
        await factory.ResetDatabaseAsync();
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        await userDb.UserExampleSentences.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> Favourite(string text, string? source = "Some deck")
        => _client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/user/example-sentences/1/0/favourite")
                             .WithUser(TestUsers.UserA)
                             .WithJsonContent(new { text, source }));

    private async Task<List<string>> SavedTexts()
    {
        using var scope = factory.Services.CreateScope();
        var userDb = scope.ServiceProvider.GetRequiredService<UserDbContext>();
        return await userDb.UserExampleSentences
                           .Where(e => e.UserId == TestUsers.UserA)
                           .Select(e => e.Text)
                           .ToListAsync();
    }

    [Fact]
    public async Task Favouriting_TheSameSentenceTwice_StoresItOnce()
    {
        (await Favourite("これは**猫**です")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Favourite("これは**猫**です")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await SavedTexts()).Should().ContainSingle();
    }

    [Fact]
    public async Task Favouriting_DifferentSentences_StoresBoth()
    {
        await Favourite("これは**猫**です");
        await Favourite("あれは**猫**でした");

        (await SavedTexts()).Should().HaveCount(2);
    }
}
