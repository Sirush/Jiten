using System.Net.Http.Json;
using FluentAssertions;
using Hangfire;
using Jiten.Api.Dtos;
using Jiten.Api.Jobs;
using Jiten.Core;
using Jiten.Core.Data;
using Jiten.Core.Difficulty;
using Jiten.Parser.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jiten.Parser.Tests.Integration;

public class AlgorithmAdjustmentJobTests(JitenWebApplicationFactory factory)
    : IClassFixture<JitenWebApplicationFactory>, IAsyncLifetime
{
    private const int SeriesId = 9200;
    private const int StandaloneId = 9300;

    private readonly HttpClient _client = factory.CreateClient();

    public Task InitializeAsync() => factory.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private DifficultyComputationJob Job() => new(
        factory.Services.GetRequiredService<IDbContextFactory<JitenDbContext>>(),
        factory.Services.GetRequiredService<IHttpClientFactory>(),
        factory.Services.GetRequiredService<IConfiguration>(),
        factory.Services.GetRequiredService<IBackgroundJobClient>(),
        NullLogger<DifficultyComputationJob>.Instance);

    private async Task Seed()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();

        // A short, kanji-light film: the adjustment lowers it by about a point.
        db.Decks.Add(new Deck
        {
            DeckId = StandaloneId, OriginalTitle = "Film", MediaType = MediaType.Movie,
            CharacterCount = 7610, UniqueKanjiCount = 191, Difficulty = 1.68f
        });
        db.DeckDifficulties.Add(new DeckDifficulty
        {
            DeckId = StandaloneId, Difficulty = 1.68m, Peak = 2.80m,
            Deciles = new Dictionary<string, decimal> { ["50"] = 1.68m, ["80"] = 2.80m },
            Progression = [new ProgressionSegment { Segment = 0, Difficulty = 1.50m, Peak = 2.60m }]
        });

        db.Decks.Add(new Deck
        {
            DeckId = SeriesId, OriginalTitle = "Series", MediaType = MediaType.Anime,
            CharacterCount = 59198, UniqueKanjiCount = 1128, Difficulty = 1.12f
        });
        db.DeckDifficulties.Add(new DeckDifficulty { DeckId = SeriesId, Difficulty = 1.12m, Peak = 2.10m });
        (int Characters, int UniqueKanji, decimal Difficulty)[] episodes = [(30000, 700, 1.00m), (29198, 720, 1.24m)];
        for (var i = 0; i < episodes.Length; i++)
        {
            var episodeId = SeriesId + 1 + i;
            db.Decks.Add(new Deck
            {
                DeckId = episodeId, ParentDeckId = SeriesId, DeckOrder = i + 1, OriginalTitle = $"Episode {i + 1}",
                MediaType = MediaType.Anime, CharacterCount = episodes[i].Characters,
                UniqueKanjiCount = episodes[i].UniqueKanji, Difficulty = (float)episodes[i].Difficulty
            });
            db.DeckDifficulties.Add(new DeckDifficulty
            {
                DeckId = episodeId, Difficulty = episodes[i].Difficulty, Peak = episodes[i].Difficulty + 1m
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<int, (float DeckDifficulty, decimal Raw, decimal Adjustment)>> Load()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<JitenDbContext>();
        var rows = await db.Decks.AsNoTracking()
                           .Where(d => d.DeckDifficulty != null)
                           .Select(d => new { d.DeckId, d.Difficulty, Raw = d.DeckDifficulty!.Difficulty, d.DeckDifficulty.AlgorithmAdjustment })
                           .ToListAsync();
        return rows.ToDictionary(r => r.DeckId, r => (r.Difficulty, r.Raw, r.AlgorithmAdjustment));
    }

    [Fact]
    public async Task Recompute_SetsRootAdjustment_AndLeavesTheRawScoreUntouched()
    {
        await Seed();

        await Job().RecomputeAlgorithmAdjustments();

        var film = (await Load())[StandaloneId];
        film.Raw.Should().Be(1.68m);
        film.Adjustment.Should().BeApproximately(-1.09m, 0.02m);
        film.DeckDifficulty.Should().BeApproximately((float)(1.68m + film.Adjustment), 0.001f);
    }

    [Fact]
    public async Task Recompute_ChildrenCarryTheirRootsAdjustment_NotOneFromTheirOwnCounts()
    {
        await Seed();

        await Job().RecomputeAlgorithmAdjustments();

        var decks = await Load();
        var series = decks[SeriesId];
        series.Adjustment.Should().BeApproximately(0.39m, 0.02m);

        var ownCounts = AlgorithmAdjustmentCalculator.Compute(MediaType.Anime, 1.00m, 700, 30000);
        ownCounts.Should().NotBe(series.Adjustment);

        foreach (var episodeId in new[] { SeriesId + 1, SeriesId + 2 })
        {
            decks[episodeId].Adjustment.Should().Be(series.Adjustment);
            decks[episodeId].DeckDifficulty.Should().BeApproximately((float)(decks[episodeId].Raw + series.Adjustment), 0.001f);
        }
    }

    [Fact]
    public async Task Reaggregate_AppliesTheAdjustmentToParentAndChildren()
    {
        await Seed();

        await Job().ReaggregateParentDifficulty(SeriesId);

        var decks = await Load();
        var series = decks[SeriesId];
        series.Adjustment.Should().NotBe(0m);
        series.DeckDifficulty.Should().BeApproximately((float)(series.Raw + series.Adjustment), 0.001f);
        decks[SeriesId + 1].Adjustment.Should().Be(series.Adjustment);
        decks[SeriesId + 2].Adjustment.Should().Be(series.Adjustment);
    }

    [Fact]
    public async Task DifficultyEndpoint_ReturnsEveryValueOnTheAdjustedScale()
    {
        await Seed();
        await Job().RecomputeAlgorithmAdjustments();
        var adjustment = (await Load())[StandaloneId].Adjustment;

        var dto = await _client.GetFromJsonAsync<DeckDifficultyDto>($"/api/media-deck/{StandaloneId}/difficulty");

        dto!.Difficulty.Should().Be(1.68m + adjustment);
        dto.Peak.Should().Be(2.80m + adjustment);
        dto.Deciles["80"].Should().Be(2.80m + adjustment);
        dto.Progression.Single().Difficulty.Should().Be(1.50m + adjustment);
        dto.Progression.Single().Peak.Should().Be(2.60m + adjustment);
    }
}
