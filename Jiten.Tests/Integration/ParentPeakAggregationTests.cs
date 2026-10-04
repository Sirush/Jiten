using FluentAssertions;
using Hangfire;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Parser.Tests.Integration;

public class ParentPeakAggregationTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int ParentId = 9100;

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private DifficultyComputationJob Job() => new(
        factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>(),
        factory.Services.GetRequiredService<IHttpClientFactory>(),
        factory.Services.GetRequiredService<IConfiguration>(),
        factory.Services.GetRequiredService<IBackgroundJobClient>(),
        NullLogger<DifficultyComputationJob>.Instance);

    private async Task SeedParent(params (int Characters, decimal Difficulty, decimal Peak)[] children)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        db.Decks.Add(new Deck { DeckId = ParentId, OriginalTitle = "Series", MediaType = MediaType.Anime });
        for (var i = 0; i < children.Length; i++)
        {
            var childId = ParentId + 1 + i;
            db.Decks.Add(new Deck
            {
                DeckId = childId, ParentDeckId = ParentId, DeckOrder = i + 1, OriginalTitle = $"Episode {i + 1}",
                MediaType = MediaType.Anime, CharacterCount = children[i].Characters
            });
            db.DeckDifficulties.Add(new DeckDifficulty
            {
                DeckId = childId, Difficulty = children[i].Difficulty, Peak = children[i].Peak
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<DeckDifficulty> ParentDifficulty()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        return await db.DeckDifficulties.AsNoTracking().SingleAsync(d => d.DeckId == ParentId);
    }

    [Fact]
    public async Task ParentPeak_IsCharacterWeightedAverageOfChildPeaks()
    {
        await SeedParent((1000, 2.0m, 3.0m), (3000, 2.0m, 4.0m));

        await Job().ReaggregateParentDifficulty(ParentId);

        (await ParentDifficulty()).Peak.Should().Be(3.75m);
    }

    [Fact]
    public async Task ParentPeak_IsNotPulledUpByOneOutlierChild()
    {
        var children = Enumerable.Repeat((Characters: 1000, Difficulty: 2.0m, Peak: 3.0m), 23)
                                 .Append((Characters: 1000, Difficulty: 2.0m, Peak: 4.9m))
                                 .ToArray();
        await SeedParent(children);

        await Job().ReaggregateParentDifficulty(ParentId);

        (await ParentDifficulty()).Peak.Should().Be(3.08m);
    }

    [Fact]
    public async Task ProgressionSegmentPeak_AveragesTheChildrenItCovers()
    {
        // 50 children fold into 25 segments of two children each.
        var children = Enumerable.Range(0, 50)
                                 .Select(i => (Characters: 1000, Difficulty: 2.0m, Peak: i % 2 == 0 ? 3.0m : 4.0m))
                                 .ToArray();
        await SeedParent(children);

        await Job().ReaggregateParentDifficulty(ParentId);

        var progression = (await ParentDifficulty()).Progression;
        progression.Should().HaveCount(25);
        progression.Should().OnlyContain(s => s.Peak == 3.5m);
    }
}
