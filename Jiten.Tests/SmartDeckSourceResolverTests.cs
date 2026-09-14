using FluentAssertions;
using Jiten.Api.Services.SmartDeck;
using Jiten.Core.Data;
using Jiten.Core.Services.SmartDeck;

namespace Jiten.Tests;

public class SmartDeckSourceResolverTests
{
    private static readonly DateTime Now = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static SmartDeckPreferenceRow Parent(int id, DeckStatus status, int ageDays = 0)
        => new(id, null, status, Now.AddDays(-ageDays));

    private static SmartDeckPreferenceRow Child(int id, int parentId, DeckStatus status, int ageDays = 0)
        => new(id, parentId, status, Now.AddDays(-ageDays));

    [Fact]
    public void OnlyOngoingParents_FormTheDefaultSet()
    {
        var titles = SmartDeckSourceResolver.ResolveTitles(new SmartDeckSettings(),
        [
            Parent(1, DeckStatus.Ongoing), Parent(2, DeckStatus.Planning), Parent(3, DeckStatus.Completed),
            Parent(4, DeckStatus.Dropped), Child(41, 4, DeckStatus.Ongoing),
        ], Now);

        titles.Select(t => t.ParentDeckId).Should().Equal(1);
    }

    [Fact]
    public void ExclusionWins_IncludeIgnoresStatus()
    {
        var settings = new SmartDeckSettings { ExcludedDeckIds = [1], IncludedDeckIds = [3, 9] };
        var titles = SmartDeckSourceResolver.ResolveTitles(settings,
            [Parent(1, DeckStatus.Ongoing), Parent(2, DeckStatus.Ongoing), Parent(3, DeckStatus.Completed)], Now);

        titles.Select(t => t.ParentDeckId).Should().BeEquivalentTo([2, 3, 9]);
        titles.Single(t => t.ParentDeckId == 9).Weight.Should().Be(1.0, "an untouched manual title counts as active now");
        titles.Single(t => t.ParentDeckId == 3).ManuallyIncluded.Should().BeTrue();
    }

    [Fact]
    public void PinnedTitles_ComeFirstInPinOrder_AtFullWeight()
    {
        var settings = new SmartDeckSettings { PinnedDeckIds = [3, 1] };
        var titles = SmartDeckSourceResolver.ResolveTitles(settings,
            [Parent(1, DeckStatus.Ongoing, ageDays: 60), Parent(2, DeckStatus.Ongoing, ageDays: 0), Parent(3, DeckStatus.Ongoing, ageDays: 30)], Now);

        titles.Select(t => t.ParentDeckId).Should().Equal(3, 1, 2);
        titles[0].Weight.Should().Be(1.0);
        titles[1].Weight.Should().Be(1.0);
        titles[0].Pinned.Should().BeTrue();
    }

    [Fact]
    public void Recency_UsesLatestChildActivity_AndDecays()
    {
        var titles = SmartDeckSourceResolver.ResolveTitles(new SmartDeckSettings(),
        [
            Parent(1, DeckStatus.Ongoing, ageDays: 40), Child(11, 1, DeckStatus.Completed, ageDays: 1),
            Parent(2, DeckStatus.Ongoing, ageDays: 14),
        ], Now);

        titles.Select(t => t.ParentDeckId).Should().Equal(1, 2);
        titles[0].Weight.Should().BeApproximately(Math.Pow(0.5, 1.0 / 14), 1e-9);
        titles[1].Weight.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Planning_OnlyWhenEnabled_AllOfThem_FlatWeight_BehindOngoing()
    {
        var rows = Enumerable.Range(1, 12).Select(i => Parent(i, DeckStatus.Planning, ageDays: i * 30)).ToList();
        rows.Add(Parent(100, DeckStatus.Ongoing, ageDays: 400));

        SmartDeckSourceResolver.ResolveTitles(new SmartDeckSettings(), rows, Now).Select(t => t.ParentDeckId).Should().Equal(100);

        var titles = SmartDeckSourceResolver.ResolveTitles(new SmartDeckSettings { WeighPlanning = true }, rows, Now);
        titles.Should().HaveCount(13);
        titles[0].ParentDeckId.Should().Be(100, "an Ongoing title outranks every Planning title however stale it is");
        titles.Where(t => t.Planning).Select(t => t.ParentDeckId).Should().BeEquivalentTo(Enumerable.Range(1, 12));
        titles.Where(t => t.Planning).Select(t => t.Weight).Should().AllSatisfy(w => w.Should().Be(SmartDeckConstants.PlanningWeight),
            "when a title was added to the plan says nothing about when it starts");
    }

    [Fact]
    public void Boosted_IsTheFirstFive_AndCapHolds()
    {
        var rows = Enumerable.Range(1, 120).Select(i => Parent(i, DeckStatus.Ongoing, ageDays: i)).ToList();
        var titles = SmartDeckSourceResolver.ResolveTitles(new SmartDeckSettings(), rows, Now);

        titles.Should().HaveCount(SmartDeckConstants.MaxTitles);
        titles.Take(5).Should().OnlyContain(t => t.Boosted);
        titles.Skip(5).Should().OnlyContain(t => !t.Boosted);
        titles.Select(t => t.ParentDeckId).Should().BeInAscendingOrder("most recent first, and age grows with id");
    }

    [Fact]
    public void Window_FollowsLastCompletedUnit_SkippedUnitsCountAsPassed()
    {
        var units = new List<(int DeckId, int DeckOrder)> { (11, 0), (12, 1), (13, 2), (14, 3), (15, 4) };

        var window = SmartDeckSourceResolver.ResolveWindow(1, units, new HashSet<int> { 11, 13 }, lookaheadUnits: 2);
        window.CursorDeckId.Should().Be(13);
        window.WindowDeckIds.Should().Equal(14, 15);
        window.CompletedUnits.Should().Be(3, "unit 12 sits below the cursor and counts as passed");

        var fresh = SmartDeckSourceResolver.ResolveWindow(1, units, new HashSet<int>(), lookaheadUnits: 1);
        fresh.CursorDeckId.Should().BeNull();
        fresh.WindowDeckIds.Should().Equal(11);

        var finished = SmartDeckSourceResolver.ResolveWindow(1, units, new HashSet<int> { 15 }, lookaheadUnits: 1);
        finished.WindowDeckIds.Should().BeEmpty();
    }

    [Fact]
    public void ResolveWindow_OngoingUnitsWin_AndUnorderedTitlesNeverGuess()
    {
        var units = new List<(int DeckId, int DeckOrder)> { (11, 0), (12, 1), (13, 2), (14, 3), (15, 4) };
        var completed = new HashSet<int> { 11 };

        var routes = SmartDeckSourceResolver.ResolveWindow(1, units, completed, lookaheadUnits: 1, ongoingUnitIds: new HashSet<int> { 14, 11 }, sequential: false);
        routes.Source.Should().Be(SmartDeckWindowSource.Ongoing);
        routes.WindowDeckIds.Should().Equal(new[] { 14 }, "a completed unit is never a window even if still marked Ongoing");
        routes.CursorDeckId.Should().BeNull();
        routes.CompletedUnits.Should().Be(1);

        var sequentialWithOngoing = SmartDeckSourceResolver.ResolveWindow(1, units, completed, lookaheadUnits: 1, ongoingUnitIds: new HashSet<int> { 15 }, sequential: true);
        sequentialWithOngoing.WindowDeckIds.Should().Equal(new[] { 15 }, "an explicit Ongoing unit beats the DeckOrder guess");

        var extended = SmartDeckSourceResolver.ResolveWindow(1, units, completed, lookaheadUnits: 3, ongoingUnitIds: new HashSet<int> { 12 }, sequential: true);
        extended.Source.Should().Be(SmartDeckWindowSource.Ongoing);
        extended.WindowDeckIds.Should().Equal(new[] { 12, 13, 14 }, "a sequential title fills the lookahead after the last Ongoing unit");

        var skipsCompleted = SmartDeckSourceResolver.ResolveWindow(1, units, new HashSet<int> { 11, 13 }, lookaheadUnits: 2, ongoingUnitIds: new HashSet<int> { 12 }, sequential: true);
        skipsCompleted.WindowDeckIds.Should().Equal(new[] { 12, 14 }, "completed units after the Ongoing one are skipped, not counted");

        var ongoingOnly = SmartDeckSourceResolver.ResolveWindow(1, units, completed, lookaheadUnits: 3, ongoingUnitIds: new HashSet<int> { 12 }, sequential: false);
        ongoingOnly.WindowDeckIds.Should().Equal(new[] { 12 }, "Follows Ongoing ignores the lookahead");

        var unordered = SmartDeckSourceResolver.ResolveWindow(1, units, completed, lookaheadUnits: 1, ongoingUnitIds: new HashSet<int>(), sequential: false);
        unordered.Source.Should().Be(SmartDeckWindowSource.None);
        unordered.WindowDeckIds.Should().BeEmpty();
        unordered.CompletedUnits.Should().Be(1);

        var tooMany = SmartDeckSourceResolver.ResolveWindow(1, units, new HashSet<int>(), lookaheadUnits: 1,
                                                             ongoingUnitIds: new HashSet<int> { 11, 12, 13, 14, 15 }, sequential: false);
        tooMany.WindowDeckIds.Should().HaveCount(SmartDeckConstants.MaxOngoingUnits);
    }
}
