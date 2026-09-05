using FluentAssertions;
using Jiten.Core.Data;
using Jiten.Core.Services.Popularity;

namespace Jiten.Tests;

public class PopularityCalculatorTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddYears(-2);

    private static DeckNode Parent(int id, DateTime? createdAt = null, MediaType type = MediaType.Anime) => new(id, null, type, createdAt ?? Old);
    private static DeckNode Child(int id, int parent) => new(id, parent, MediaType.Anime, Old);

    private static Dictionary<int, double> Scores(Dictionary<int, PopularityResult> r) => r.ToDictionary(kv => kv.Key, kv => kv.Value.Score);

    [Fact]
    public void Intent_outranks_any_amount_of_views()
    {
        var decks = new[] { Parent(1), Parent(2), Parent(3) };
        var intents = new[] { new IntentEvent(1, PopularityWeights.StudyDeck, Now.AddDays(-1)) };
        var activity = Enumerable.Range(0, 3).Select(i => new ActivityDay(2, DateOnly.FromDateTime(Now.AddDays(-i)), 100_000, 0));

        var scores = Scores(PopularityCalculator.Compute(decks, intents, activity, Now));

        scores[1].Should().BeGreaterThan(scores[2]);
        scores[2].Should().BeGreaterThan(scores[3]);
        scores[3].Should().Be(0);
    }

    [Fact]
    public void Views_fade_within_weeks_while_intent_persists()
    {
        var decks = new[] { Parent(1), Parent(2), Parent(3) };
        var intents = new[] { new IntentEvent(1, PopularityWeights.Planning, Now.AddDays(-60)) };
        var spike = new[] { new ActivityDay(2, DateOnly.FromDateTime(Now.AddDays(-2)), 300, 0) };

        var fresh = Scores(PopularityCalculator.Compute(decks, intents, spike, Now));
        var later = Scores(PopularityCalculator.Compute(decks, intents, spike, Now.AddDays(40)));

        fresh[2].Should().BeGreaterThan(0);
        later[2].Should().BeLessThan(fresh[2]);
        later[1].Should().BeGreaterThan(later[2]);
    }

    [Fact]
    public void Child_signals_roll_up_into_the_parent()
    {
        var decks = new[] { Parent(1), Child(11, 1), Parent(2) };
        var intents = new[]
        {
            new IntentEvent(11, PopularityWeights.Completed, Now.AddDays(-5)),
            new IntentEvent(2, PopularityWeights.Planning, Now.AddDays(-5)),
        };

        var scores = Scores(PopularityCalculator.Compute(decks, intents, [], Now));

        scores.Should().NotContainKey(11);
        scores[1].Should().BeGreaterThan(scores[2]);
    }

    [Fact]
    public void All_time_share_keeps_an_old_favourite_above_nothing()
    {
        var decks = new[] { Parent(1), Parent(2) };
        var intents = new[] { new IntentEvent(1, PopularityWeights.Completed, Now.AddYears(-3)) };

        var scores = Scores(PopularityCalculator.Compute(decks, intents, [], Now));

        scores[1].Should().BeGreaterThan(0);
        scores[2].Should().Be(0);
    }

    [Fact]
    public void New_deck_boost_surfaces_a_fresh_parse_once()
    {
        var decks = new[] { Parent(1, Now.AddDays(-3)), Parent(2) };

        var scores = Scores(PopularityCalculator.Compute(decks, [], [], Now));
        var later = Scores(PopularityCalculator.Compute(decks, [], [], Now.AddDays(45)));

        scores[1].Should().BeGreaterThan(scores[2]);
        later[1].Should().Be(0);
    }

    [Fact]
    public void Ignored_can_only_pull_a_deck_down_to_zero()
    {
        var decks = new[] { Parent(1), Parent(2) };
        var intents = new[]
        {
            new IntentEvent(1, PopularityWeights.Ignored, Now),
            new IntentEvent(1, PopularityWeights.Ignored, Now),
        };

        var scores = Scores(PopularityCalculator.Compute(decks, intents, [], Now));

        scores[1].Should().Be(0);
        scores[2].Should().Be(0);
    }

    [Fact]
    public void Attention_is_capped_below_one_study_deck()
    {
        var decks = new[] { Parent(1), Parent(2), Parent(3) };
        var intents = new[]
        {
            new IntentEvent(1, PopularityWeights.StudyDeck, Now),
            new IntentEvent(1, PopularityWeights.Planning, Now),
        };
        var flood = new[] { new ActivityDay(2, DateOnly.FromDateTime(Now), 10_000_000, 10_000_000) };

        var scores = Scores(PopularityCalculator.Compute(decks, intents, flood, Now));

        scores[1].Should().BeGreaterThan(scores[2]);
    }

    [Fact]
    public void Scale_spreads_over_engaged_decks_not_the_catalogue()
    {
        var decks = Enumerable.Range(1, 1000).Select(i => Parent(i)).ToList();
        var intents = new[]
        {
            new IntentEvent(1, PopularityWeights.StudyDeck, Now),
            new IntentEvent(2, PopularityWeights.Planning, Now),
        };

        var scores = Scores(PopularityCalculator.Compute(decks, intents, [], Now));

        scores[1].Should().Be(1);
        scores[2].Should().BeApproximately(0.5, 1e-9);
        scores[3].Should().Be(0);
    }

    [Fact]
    public void Ranks_show_only_inside_the_display_window()
    {
        var decks = Enumerable.Range(1, 40).Select(i => Parent(i)).Concat(Enumerable.Range(101, 5).Select(i => Parent(i, type: MediaType.Manga))).ToList();
        var intents = Enumerable.Range(1, 40).Select(i => new IntentEvent(i, 50 - i, Now))
                                .Concat(Enumerable.Range(101, 5).Select(i => new IntentEvent(i, 10, Now)))
                                .ToList();

        var results = PopularityCalculator.Compute(decks, intents, [], Now);

        results[1].TypeRank.Should().Be(1);
        results[10].TypeRank.Should().Be(10);
        results[11].TypeRank.Should().Be(0);
        results[1].GlobalRank.Should().Be(1);
        results[101].TypeRank.Should().Be(0);
        results[101].GlobalRank.Should().Be(0);
    }

    [Fact]
    public void Trending_needs_activity_history_and_a_burst_over_baseline()
    {
        var decks = new[] { Parent(1), Parent(2) };
        var burst = Enumerable.Range(0, 3).Select(i => new IntentEvent(1, PopularityWeights.Completed, Now.AddDays(-i), $"u{i}")).ToList();
        var steady = Enumerable.Range(0, 60).Select(i => new IntentEvent(2, PopularityWeights.Planning, Now.AddDays(-i), $"s{i}")).ToList();
        var thinActivity = Enumerable.Range(0, 5).Select(i => new ActivityDay(2, DateOnly.FromDateTime(Now.AddDays(-i)), 1, 0)).ToList();
        var richActivity = Enumerable.Range(0, 20).Select(i => new ActivityDay(2, DateOnly.FromDateTime(Now.AddDays(-i)), 1, 0)).ToList();

        var early = PopularityCalculator.Compute(decks, burst.Concat(steady), thinActivity, Now);
        var later = PopularityCalculator.Compute(decks, burst.Concat(steady), richActivity, Now);

        early[1].IsTrending.Should().BeFalse();
        later[1].IsTrending.Should().BeTrue();
        later[2].IsTrending.Should().BeFalse();
    }

    [Fact]
    public void New_decks_never_trend()
    {
        var decks = new[] { Parent(1, Now.AddDays(-3)) };
        var burst = Enumerable.Range(0, 3).Select(i => new IntentEvent(1, PopularityWeights.Completed, Now.AddDays(-i), $"u{i}")).ToList();
        var activity = Enumerable.Range(0, 20).Select(i => new ActivityDay(1, DateOnly.FromDateTime(Now.AddDays(-i)), 1, 0)).ToList();

        PopularityCalculator.Compute(decks, burst, activity, Now)[1].IsTrending.Should().BeFalse();
    }

    [Fact]
    public void Tied_scores_get_distinct_ranks_in_list_order()
    {
        var decks = Enumerable.Range(1, 30).Select(i => new DeckNode(i, null, MediaType.Anime, Old, (byte)(i == 2 ? 90 : 50), new DateOnly(2020, 1, 1))).ToList();
        var intents = Enumerable.Range(1, 30).Select(i => new IntentEvent(i, PopularityWeights.Completed, Now)).ToList();

        var results = PopularityCalculator.Compute(decks, intents, [], Now);

        results[2].TypeRank.Should().Be(1);
        results.Values.Where(r => r.TypeRank > 0).Select(r => r.TypeRank).Should().OnlyHaveUniqueItems();
        results.Values.Count(r => r.TypeRank > 0).Should().Be(7);
    }

    [Fact]
    public void One_account_cannot_trend_a_deck_alone()
    {
        var decks = new[] { Parent(1) };
        var solo = new[]
        {
            new IntentEvent(1, PopularityWeights.StudyDeck, Now, "u1"),
            new IntentEvent(1, PopularityWeights.Completed, Now, "u1"),
            new IntentEvent(1, PopularityWeights.Favourite, Now, "u1"),
            new IntentEvent(1, PopularityWeights.Download, Now, "u1"),
        };
        var activity = Enumerable.Range(0, 20).Select(i => new ActivityDay(1, DateOnly.FromDateTime(Now.AddDays(-i)), 1, 0)).ToList();

        PopularityCalculator.Compute(decks, solo, activity, Now)[1].IsTrending.Should().BeFalse();
    }
}
