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
    public void Marking_every_episode_counts_like_marking_the_series_once()
    {
        var rootOf = new Dictionary<int, int> { [1] = 1, [2] = 2 };
        for (var i = 11; i <= 40; i++) rootOf[i] = 1;
        var episodes = Enumerable.Range(0, 5).SelectMany(u => Enumerable.Range(11, 30)
            .Select(ep => new IntentEvent(ep, PopularityWeights.Completed, Now.AddDays(-ep), $"u{u}")));
        var series = Enumerable.Range(0, 20).Select(u => new IntentEvent(2, PopularityWeights.Completed, Now.AddDays(-20), $"s{u}"));

        var collapsed = PopularityCalculator.CollapsePerRoot(episodes.Concat(series), rootOf);

        collapsed.Count(e => e.DeckId == 1).Should().Be(5);
        collapsed.Where(e => e.DeckId == 1).Should().OnlyContain(e => e.Weight == PopularityWeights.Completed && e.At == Now.AddDays(-11));
        collapsed.Count(e => e.DeckId == 2).Should().Be(20);
    }

    [Fact]
    public void Collapse_keeps_the_strongest_weight_and_the_latest_date()
    {
        var rootOf = new Dictionary<int, int> { [1] = 1, [11] = 1 };
        var events = new[]
        {
            new IntentEvent(11, PopularityWeights.Planning, Now.AddDays(-1), "u"),
            new IntentEvent(1, PopularityWeights.Completed, Now.AddDays(-30), "u"),
            new IntentEvent(11, PopularityWeights.Ongoing, Now.AddDays(-400)),
        };

        var collapsed = PopularityCalculator.CollapsePerRoot(events, rootOf);

        collapsed.Should().HaveCount(2);
        collapsed.Single(e => e.UserId == "u").Should().Be(new IntentEvent(1, PopularityWeights.Completed, Now.AddDays(-1), "u"));
        collapsed.Single(e => e.UserId == null).DeckId.Should().Be(1);
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

    private static List<ActivityDay> DailyViews(int deckId, int fromDaysAgo, int toDaysAgo, int views) =>
        Enumerable.Range(fromDaysAgo, toDaysAgo - fromDaysAgo + 1)
                  .Select(i => new ActivityDay(deckId, DateOnly.FromDateTime(Now.AddDays(-i)), views, 0))
                  .ToList();

    [Fact]
    public void A_burst_of_visitors_trends_a_deck_nobody_usually_visits()
    {
        var decks = new[] { Parent(1), Parent(2) };
        var quietThenBurst = DailyViews(1, 2, 27, 1).Concat(DailyViews(1, 0, 1, 8)).ToList();
        var flat = DailyViews(2, 0, 27, 8);

        var results = PopularityCalculator.Compute(decks, [], quietThenBurst.Concat(flat), Now);

        results[1].IsTrending.Should().BeTrue();
        results[2].IsTrending.Should().BeFalse();
    }

    [Fact]
    public void Trending_ends_once_the_burst_leaves_the_window()
    {
        var decks = new[] { Parent(1) };
        var burst = DailyViews(1, 0, 1, 10);

        PopularityCalculator.Compute(decks, [], burst, Now)[1].IsTrending.Should().BeTrue();
        PopularityCalculator.Compute(decks, [], burst, Now.AddDays(3))[1].IsTrending.Should().BeFalse();
    }

    [Fact]
    public void Steady_popular_decks_need_a_spike_over_their_own_usual()
    {
        var decks = new[] { Parent(1) };
        var usual = DailyViews(1, 2, 27, 30);
        var mild = usual.Concat(DailyViews(1, 0, 1, 60)).ToList();
        var spike = usual.Concat(DailyViews(1, 0, 1, 100)).ToList();

        PopularityCalculator.Compute(decks, [], mild, Now)[1].IsTrending.Should().BeFalse();
        PopularityCalculator.Compute(decks, [], spike, Now)[1].IsTrending.Should().BeTrue();
    }

    [Fact]
    public void A_few_people_acting_trend_a_deck_immediately()
    {
        var decks = new[] { Parent(1) };
        var adds = Enumerable.Range(0, 3).Select(i => new IntentEvent(1, PopularityWeights.StudyDeck, Now, $"u{i}")).ToList();

        PopularityCalculator.Compute(decks, adds, [], Now)[1].IsTrending.Should().BeTrue();
    }

    [Fact]
    public void Brand_new_decks_can_trend()
    {
        var decks = new[] { Parent(1, Now.AddDays(-1)) };
        var burst = DailyViews(1, 0, 1, 12);

        PopularityCalculator.Compute(decks, [], burst, Now)[1].IsTrending.Should().BeTrue();
    }

    [Fact]
    public void Two_visitors_against_an_empty_baseline_do_not_trend()
    {
        var decks = new[] { Parent(1) };

        PopularityCalculator.Compute(decks, [], DailyViews(1, 0, 1, 2), Now)[1].IsTrending.Should().BeFalse();
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
        var ownView = DailyViews(1, 0, 1, 1);

        PopularityCalculator.Compute(decks, solo, ownView, Now)[1].IsTrending.Should().BeFalse();
    }
}
