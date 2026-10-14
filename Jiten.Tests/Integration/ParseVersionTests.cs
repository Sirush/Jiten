using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Jiten.Api.Dtos;
using Jiten.Api.Services;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Services;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jiten.Parser.Tests.Integration;

public class ParseVersionTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private IDbContextFactory<JitenDbContext> ContextFactory =>
        factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>();

    private async Task<int> CurrentEpoch()
    {
        await using var db = await ContextFactory.CreateDbContextAsync();
        return await ParseDataEpochStore.ReadAsync(db);
    }

    [Fact]
    public async Task Version_IsAnonymousAndCacheable()
    {
        var response = await _client.GetAsync("/api/reader/version");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromSeconds(60));
        var body = await response.Content.ReadFromJsonAsync<ReaderVersionResponse>();
        body!.ParseVersion.Should().StartWith($"{ParseVersion.ParserRevision}.");
    }

    [Fact]
    public async Task Bump_IncrementsEpoch_ButRunningProcessKeepsItsVersion()
    {
        var running = factory.Services.GetRequiredService<IParseVersionProvider>();
        var before = await running.GetAsync();
        var epoch = await CurrentEpoch();

        await ParseDataEpochStore.BumpAsync(ContextFactory);

        (await CurrentEpoch()).Should().Be(epoch + 1);
        (await running.GetAsync()).Should().Be(before);
        (await new ParseVersionProvider(ContextFactory).GetAsync()).Should().Be(ParseVersion.Format(epoch + 1));
    }

    [Fact]
    public async Task Bump_CreatesRowWhenMissing()
    {
        await using (var db = await ContextFactory.CreateDbContextAsync())
            await db.ParseDataEpochs.ExecuteDeleteAsync();

        await ParseDataEpochStore.BumpAsync(ContextFactory);

        (await CurrentEpoch()).Should().Be(1);
    }
}
