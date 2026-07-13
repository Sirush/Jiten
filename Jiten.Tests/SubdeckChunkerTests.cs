using FluentAssertions;
using Jiten.Core.WebNovel;
using Xunit;

namespace Jiten.Tests;

public class SubdeckChunkerTests
{
    private static List<ChunkEpisode> Episodes(int from, int count, int charsEach) =>
        Enumerable.Range(from, count)
                  .Select(n => new ChunkEpisode(n, $"第{n}話", charsEach))
                  .ToList();

    [Fact]
    public void FirstImport_MedianEpisodes_SplitsAtBudget()
    {
        // 2,500 chars is the median Narou episode: a 150k budget holds 60 of them
        var plans = SubdeckChunker.Plan([], Episodes(1, 130, 2_500));

        plans.Should().HaveCount(3);
        plans[0].StartEpisode.Should().Be(1);
        plans[0].EndEpisode.Should().Be(60);
        plans[1].StartEpisode.Should().Be(61);
        plans[1].EndEpisode.Should().Be(120);

        // The trailing partial subdeck stays open for the next sync
        plans[2].StartEpisode.Should().Be(121);
        plans[2].EndEpisode.Should().Be(130);
        plans.Should().OnlyContain(p => p.IsNew);
        plans.Select(p => p.ChunkIndex).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void MicroChapters_CapAtMaxEpisodes()
    {
        // 300-char chapters would need 500 episodes to reach the budget; the episode cap closes first
        var plans = SubdeckChunker.Plan([], Episodes(1, 200, 300));

        plans[0].EpisodesToAppend.Should().HaveCount(SubdeckChunker.MaxEpisodesPerChunk);
        plans[0].EndEpisode.Should().Be(150);
        plans[1].StartEpisode.Should().Be(151);
    }

    [Fact]
    public void SingleEpisodeLargerThanBudget_GetsItsOwnSubdeck()
    {
        var plans = SubdeckChunker.Plan([], [new ChunkEpisode(1, "長編", 400_000), new ChunkEpisode(2, "続き", 1_000)]);

        plans.Should().HaveCount(2);
        plans[0].EpisodesToAppend.Should().ContainSingle();
        plans[0].Title.Should().Be("第1話");
        plans[1].StartEpisode.Should().Be(2);
    }

    [Fact]
    public void Sync_ExtendsOpenSubdeck_KeepingItsDeckId()
    {
        // Open subdeck: episodes 121-130, well under budget
        var existing = new List<ExistingChunk> { new(1, ChildDeckId: 42, StartEpisode: 121, EndEpisode: 130, EpisodeCount: 10, CharCount: 25_000) };

        var plans = SubdeckChunker.Plan(existing, Episodes(131, 2, 2_500));

        plans.Should().ContainSingle();
        plans[0].IsNew.Should().BeFalse();
        plans[0].ChildDeckId.Should().Be(42);
        plans[0].ChunkIndex.Should().Be(1);

        // Only the new episodes are appended; the range covers everything the subdeck now holds
        plans[0].EpisodesToAppend.Should().HaveCount(2);
        plans[0].StartEpisode.Should().Be(121);
        plans[0].EndEpisode.Should().Be(132);
        plans[0].Title.Should().Be("第121話〜第132話");
    }

    [Fact]
    public void Sync_ClosesOpenSubdeck_AndOpensNext()
    {
        // 148k of a 150k budget: one more episode closes it
        var existing = new List<ExistingChunk> { new(2, ChildDeckId: 42, StartEpisode: 61, EndEpisode: 119, EpisodeCount: 59, CharCount: 148_000) };

        var plans = SubdeckChunker.Plan(existing, Episodes(120, 3, 2_500));

        plans.Should().HaveCount(2);

        plans[0].ChildDeckId.Should().Be(42);
        plans[0].EpisodesToAppend.Should().ContainSingle();
        plans[0].EndEpisode.Should().Be(120);

        plans[1].IsNew.Should().BeTrue();
        plans[1].ChunkIndex.Should().Be(3);
        plans[1].StartEpisode.Should().Be(121);
        plans[1].EndEpisode.Should().Be(122);
    }

    [Fact]
    public void Sync_WhenLastSubdeckIsFull_OpensNewOne()
    {
        var existing = new List<ExistingChunk> { new(1, ChildDeckId: 42, StartEpisode: 1, EndEpisode: 60, EpisodeCount: 60, CharCount: 150_000) };

        var plans = SubdeckChunker.Plan(existing, Episodes(61, 1, 2_500));

        plans.Should().ContainSingle();
        plans[0].IsNew.Should().BeTrue();
        plans[0].ChunkIndex.Should().Be(2);
        plans[0].ChildDeckId.Should().BeNull();
        plans[0].Title.Should().Be("第61話");
    }

    [Fact]
    public void NoNewEpisodes_PlansNothing()
    {
        var existing = new List<ExistingChunk> { new(1, 42, 1, 60, 60, 150_000) };

        SubdeckChunker.Plan(existing, []).Should().BeEmpty();
    }

    [Fact]
    public void ExactBudgetBoundary_ClosesSubdeck()
    {
        var plans = SubdeckChunker.Plan([], Episodes(1, 2, 75_000));

        plans.Should().ContainSingle();
        plans[0].EndEpisode.Should().Be(2);

        // Exactly at budget counts as full, so the next episode opens a new subdeck
        var next = SubdeckChunker.Plan([new ExistingChunk(1, 42, 1, 2, 2, 150_000)], Episodes(3, 1, 1_000));
        next[0].IsNew.Should().BeTrue();
    }

    [Fact]
    public void BudgetRunsOutJustPastABoundary_RoundsDown()
    {
        // 2,840-char episodes fill the budget on episode 53; the split belongs at 50
        var plans = SubdeckChunker.Plan([], Episodes(1, 130, 2_840));

        plans.Select(p => (p.StartEpisode, p.EndEpisode)).Should().Equal((1, 50), (51, 100), (101, 130));
        plans[0].EpisodesToAppend.Should().HaveCount(50);
    }

    [Fact]
    public void BudgetRunsOutJustBeforeABoundary_GrowsIntoIt()
    {
        // 2,640-char episodes fill the budget on episode 57; the subdeck keeps taking episodes up to 60
        var plans = SubdeckChunker.Plan([], Episodes(1, 130, 2_640));

        plans.Select(p => (p.StartEpisode, p.EndEpisode)).Should().Equal((1, 60), (61, 120), (121, 130));
        plans[0].Title.Should().Be("第1話〜第60話");
    }

    [Fact]
    public void OpenSubdeckAlreadyPastTheBoundary_CanOnlyMoveForward()
    {
        // Episodes 1-63 are already parsed into subdeck 42, so the split can't be pulled back to 60
        var existing = new List<ExistingChunk> { new(1, ChildDeckId: 42, StartEpisode: 1, EndEpisode: 63, EpisodeCount: 63, CharCount: 148_000) };

        var plans = SubdeckChunker.Plan(existing, Episodes(64, 12, 2_500));

        plans[0].ChildDeckId.Should().Be(42);
        plans[0].EndEpisode.Should().Be(70);
        plans[0].EpisodesToAppend.Should().HaveCount(7);

        plans[1].IsNew.Should().BeTrue();
        plans[1].StartEpisode.Should().Be(71);
    }

    [Fact]
    public void OpenSubdeckSittingOnABoundary_IsLeftAloneAndTheNextOneStartsFresh()
    {
        // Subdeck 42 holds 1-60 and is just short of the budget. One more episode would overflow it, and 60 is
        // already where it wants to end — so it takes nothing and closes as it stands.
        var existing = new List<ExistingChunk> { new(1, ChildDeckId: 42, StartEpisode: 1, EndEpisode: 60, EpisodeCount: 60, CharCount: 148_000) };

        var plans = SubdeckChunker.Plan(existing, Episodes(61, 65, 2_500));

        plans.Should().OnlyContain(p => p.IsNew);
        plans.Select(p => (p.StartEpisode, p.EndEpisode)).Should().Equal((61, 120), (121, 125));
        plans[0].ChunkIndex.Should().Be(2);
    }

    [Fact]
    public void MidLengthChapters_FallBackToTheSmallerBoundary()
    {
        // 11k-char episodes only fit 14 to a subdeck — too few to round to tens, so it rounds to fives
        var plans = SubdeckChunker.Plan([], Episodes(1, 30, 11_000));

        plans.Select(p => (p.StartEpisode, p.EndEpisode)).Should().Equal((1, 15), (16, 30));
    }

    [Fact]
    public void VeryLongChapters_SplitAtTheBudgetWithNoRounding()
    {
        // 8 episodes to a subdeck: rounding to tens or fives would redraw the boundary, not nudge it
        var plans = SubdeckChunker.Plan([], Episodes(1, 20, 20_000));

        plans.Select(p => (p.StartEpisode, p.EndEpisode)).Should().Equal((1, 8), (9, 16), (17, 20));
    }

    [Fact]
    public void CountCharacters_IgnoresFuriganaReadingsAndWhitespace()
    {
        // The reading in {愛機'パソコン} is an annotation, not text the reader counts
        SubdeckChunker.CountCharacters("俺の{愛機'パソコン}だ。").Should().Be(6);
        SubdeckChunker.CountCharacters("あ い\nう").Should().Be(3);
        SubdeckChunker.CountCharacters("").Should().Be(0);
    }

    [Fact]
    public void CustomBudget_IsHonoured()
    {
        var plans = SubdeckChunker.Plan([], Episodes(1, 40, 2_500), charBudget: 50_000);

        plans[0].EpisodesToAppend.Should().HaveCount(20);
        plans.Should().HaveCount(2);
    }
}
