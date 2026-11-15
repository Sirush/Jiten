using System.Text.Json;
using FluentAssertions;
using Jiten.Api.Jobs;
using Jiten.Core.Data;
using Jiten.Core.Data.Jimaku;
using Jiten.Core.Jimaku;
using Xunit;

namespace Jiten.Tests;

public class JimakuTrackingTests
{
    private const string NetflixPattern = "薬屋のひとりごと.S03E**Netflix.ja[cc].srt";
    private const string NanakoPattern = "[NanakoRaws] Kusuriya no Hitorigoto S3 - ** (CTV TV 1080p HEVC AAC).srt";

    private static JimakuRemoteFile Remote(string name) => new(name, $"https://jimaku.cc/entry/1/download/{name}", 1000, null);

    private static string Netflix(int episode, string title = "題名") => $"薬屋のひとりごと.S03E{episode:D2}.{title}.WEBRip.Netflix.ja[cc].srt";

    private static string Nanako(int episode, string extension = "srt") =>
        $"[NanakoRaws] Kusuriya no Hitorigoto S3 - {episode:D2} (CTV TV 1080p HEVC AAC).{extension}";

    [Fact]
    public void Pattern_SeparatesProvidersAndExtensions()
    {
        var netflix = JimakuFilePattern.Compile(NetflixPattern);
        var nanako = JimakuFilePattern.Compile(NanakoPattern);

        netflix.IsMatch(Netflix(49)).Should().BeTrue();
        netflix.IsMatch(Nanako(49)).Should().BeFalse();
        netflix.IsMatch("薬屋のひとりごと.S02E25.題名.WEBRip.Netflix.ja[cc].srt").Should().BeFalse();

        nanako.IsMatch(Nanako(49)).Should().BeTrue();
        nanako.IsMatch(Nanako(49, "ass")).Should().BeFalse();
        nanako.IsMatch(Nanako(49).ToUpperInvariant()).Should().BeTrue();
    }

    [Theory]
    [InlineData("*", false)]
    [InlineData("**.*", false)]
    [InlineData("", false)]
    [InlineData("*.srt", true)]
    public void Pattern_NeedsLiteralText(string pattern, bool valid)
    {
        JimakuFilePattern.IsValid(pattern).Should().Be(valid);
    }

    [Fact]
    public void EpisodeNumbers_ReadTheCommonMarkerStyles()
    {
        JimakuEpisodeNumbers.Detect([Netflix(49), Netflix(50)]).Should().Equal(49, 50);
        JimakuEpisodeNumbers.Detect([Nanako(1), Nanako(12)]).Should().Equal(1, 12);
        JimakuEpisodeNumbers.Detect(["Inukami!.E01.Bandai.ja.srt", "Inukami!.E15.Bandai.ja.srt"]).Should().Equal(1, 15);
        JimakuEpisodeNumbers.Detect(["だから私は推しました＃01.srt", "だから私は推しました＃08.srt"]).Should().Equal(1, 8);
        JimakuEpisodeNumbers.Detect(["Kiratto Pri-chan - 103 (TX).srt", "Kiratto Pri-chan - 153 (TX).srt"]).Should().Equal(103, 153);
        JimakuEpisodeNumbers.Detect(["DanDaDan - 01 「それって恋のはじまりじゃんよ」 (TBS 1920x1080 x264 AAC).srt"]).Should().Equal(1);
    }

    [Fact]
    public void EpisodeNumbers_FallBackToTheNumberAfterTheSharedPrefix()
    {
        JimakuEpisodeNumbers.Detect(["Ultraman Orb 001.srt", "Ultraman Orb 014.srt"]).Should().Equal(1, 14);
        JimakuEpisodeNumbers.Detect(["[JPJ][School_Rumble][01]_converted.srt", "[JPJ][School_Rumble][06]_converted.srt"]).Should().Equal(1, 6);
        JimakuEpisodeNumbers.Detect(["Show 40 [1080p].srt", "Show 41 [1080p].srt", "Show 49 [1080p].srt"]).Should().Equal(40, 41, 49);
        JimakuEpisodeNumbers.Detect(["Ultraman Orb 001.srt"]).Should().Equal(new double?[] { null });
    }

    [Fact]
    public void Plan_QueuesNewEpisodesOfTheMatchingProviderOnly()
    {
        var files = new[] { Netflix(49), Netflix(50), Netflix(51), Nanako(49), Nanako(51, "ass") }.Select(Remote).ToList();
        var ledger = new[] { new JimakuLedgerEntry(Netflix(49), JimakuFileStatus.Imported, 49) };

        var decisions = JimakuSyncPlanner.Plan(NetflixPattern, files, ledger, 24);

        decisions.Select(d => (d.File.Name, d.Status)).Should().Equal(
            (Netflix(50), JimakuFileStatus.Queued),
            (Netflix(51), JimakuFileStatus.Queued));
    }

    [Fact]
    public void Plan_HoldsARepeatedEpisodeNumber()
    {
        var files = new[] { Netflix(49), Netflix(49, "別題"), Netflix(50), Netflix(50, "v2") }.Select(Remote).ToList();
        var ledger = new[] { new JimakuLedgerEntry(Netflix(49), JimakuFileStatus.Imported, 49) };

        var decisions = JimakuSyncPlanner.Plan(NetflixPattern, files, ledger, null);

        decisions.Should().OnlyContain(d => d.Status == JimakuFileStatus.Held && d.HoldReason!.StartsWith("duplicate:"));
        decisions.Should().HaveCount(3);
    }

    [Fact]
    public void Plan_HoldsEverythingPastAGapUntilItIsFilled()
    {
        var ledger = new[] { new JimakuLedgerEntry(Nanako(5), JimakuFileStatus.Imported, 5) };

        var withGap = JimakuSyncPlanner.Plan(NanakoPattern, new[] { Nanako(5), Nanako(7), Nanako(8) }.Select(Remote).ToList(), ledger, null);
        withGap.Should().OnlyContain(d => d.Status == JimakuFileStatus.Held && d.HoldReason == "gap: episode 6 is missing");

        var held = ledger.Concat(withGap.Select(d => new JimakuLedgerEntry(d.File.Name, d.Status, d.Episode))).ToList();
        var filled = JimakuSyncPlanner.Plan(NanakoPattern, new[] { Nanako(5), Nanako(6), Nanako(7), Nanako(8) }.Select(Remote).ToList(), held, null);
        filled.Should().OnlyContain(d => d.Status == JimakuFileStatus.Queued);
        filled.Select(d => d.Episode).Should().Equal(6, 7, 8);
    }

    [Fact]
    public void Plan_HoldsEpisodesPastTheExpectedTotal()
    {
        var files = Enumerable.Range(1, 13).Select(e => Remote(Nanako(e))).ToList();

        var decisions = JimakuSyncPlanner.Plan(NanakoPattern, files, [], 12);

        decisions.Count(d => d.Status == JimakuFileStatus.Queued).Should().Be(12);
        decisions.Last().HoldReason.Should().Be("over: only 12 episodes expected");
    }

    [Fact]
    public void Plan_CountsEpisodesByPositionWhenTheSeasonContinuesTheNumbering()
    {
        var files = Enumerable.Range(49, 3).Select(e => Remote(Netflix(e))).ToList();

        JimakuSyncPlanner.Plan(NetflixPattern, files, [], 24).Should().OnlyContain(d => d.Status == JimakuFileStatus.Queued);
    }

    [Fact]
    public void Plan_LeavesIgnoredAndApprovedFilesAlone()
    {
        var files = new[] { Nanako(1), Nanako(2), Nanako(2, "srt").Replace("- 02", "- 02v2"), Nanako(4) }.Select(Remote).ToList();
        var ledger = new[]
        {
            new JimakuLedgerEntry(Nanako(1), JimakuFileStatus.Imported, 1),
            new JimakuLedgerEntry(Nanako(2), JimakuFileStatus.Imported, 2),
            new JimakuLedgerEntry(files[2].Name, JimakuFileStatus.Ignored, 2),
            new JimakuLedgerEntry(Nanako(4), JimakuFileStatus.Queued, 4)
        };

        JimakuSyncPlanner.Plan("[NanakoRaws] Kusuriya no Hitorigoto S3 - *.srt", files, ledger, null).Should().BeEmpty();
    }

    [Fact]
    public void Plan_HoldsArchivesAndUnnumberedFiles()
    {
        var files = new[] { "Show - 01.srt", "Show - 02.srt", "Show - Special.srt", "Show - 01-12.zip" }.Select(Remote).ToList();

        var decisions = JimakuSyncPlanner.Plan("Show - *", files, [], null).ToDictionary(d => d.File.Name);

        decisions["Show - 01.srt"].Status.Should().Be(JimakuFileStatus.Queued);
        decisions["Show - Special.srt"].HoldReason.Should().StartWith("unnumbered:");
        decisions["Show - 01-12.zip"].HoldReason.Should().StartWith("archive:");
    }

    [Fact]
    public void MatchExisting_PrefersSubdeckFileNamesOverTheCount()
    {
        var files = Enumerable.Range(1, 5).Select(e => Remote(Nanako(e))).ToList();

        JimakuSyncPlanner.MatchExisting(NanakoPattern, files, new HashSet<string> { Nanako(2), Nanako(3) }, 4)
                         .Select(x => x.Episode).Should().Equal(2, 3);
        JimakuSyncPlanner.MatchExisting(NanakoPattern, files, new HashSet<string>(), 4)
                         .Select(x => x.Episode).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void ArrangeEpisodes_TitlesNewEpisodesByPosition()
    {
        var first = new Deck { DeckId = 1, DeckOrder = 1, OriginalTitle = "Episode 1" };
        var added = new Deck { DeckOrder = 2 };
        var episodes = new Dictionary<Deck, double?> { [first] = 49, [added] = 50 };

        JimakuSyncJob.ArrangeEpisodes([first, added], episodes, new HashSet<Deck> { added });

        added.OriginalTitle.Should().Be("Episode 2");
        added.DeckOrder.Should().Be(2);
    }

    [Fact]
    public void ArrangeEpisodes_SlotsALateEpisodeInAndKeepsCustomTitles()
    {
        var one = new Deck { DeckId = 1, DeckOrder = 1, OriginalTitle = "Episode 1" };
        var three = new Deck { DeckId = 2, DeckOrder = 2, OriginalTitle = "Episode 2" };
        var four = new Deck { DeckId = 3, DeckOrder = 3, OriginalTitle = "Finale" };
        var two = new Deck { DeckOrder = 4 };
        var episodes = new Dictionary<Deck, double?> { [one] = 1, [three] = 3, [four] = 4, [two] = 2 };

        JimakuSyncJob.ArrangeEpisodes([one, three, four, two], episodes, new HashSet<Deck> { two });

        new[] { one, two, three, four }.Select(d => d.DeckOrder).Should().Equal(1, 2, 3, 4);
        new[] { one, two, three, four }.Select(d => d.OriginalTitle).Should().Equal("Episode 1", "Episode 2", "Episode 3", "Finale");
    }

    [Theory]
    [InlineData(true, 12, 12, 0, true)]
    [InlineData(true, 12, 11, 0, false)]
    [InlineData(true, 12, 12, 1, false)]
    [InlineData(false, 12, 12, 0, false)]
    [InlineData(true, null, 12, 0, false)]
    public void Schedule_CompletesOnlyWhenTheFinishedShowIsFullyIngested(bool finished, int? expected, int imported, int unresolved, bool complete)
    {
        JimakuSchedule.IsComplete(finished, expected, imported, unresolved).Should().Be(complete);
    }

    [Fact]
    public void Tmdb_CountsTheRequestedSeason()
    {
        using var show = JsonDocument.Parse("""
            {
              "status": "Returning Series",
              "number_of_episodes": 34,
              "seasons": [
                { "season_number": 0, "episode_count": 2 },
                { "season_number": 1, "episode_count": 10 },
                { "season_number": 2, "episode_count": 12 }
              ],
              "last_episode_to_air": { "season_number": 2, "episode_number": 7 },
              "next_episode_to_air": { "season_number": 2, "episode_number": 8 }
            }
            """);

        EpisodeAiringLookup.ParseTmdb(show.RootElement, 2).Should().Be(new EpisodeAiring(12, 7, false));
        EpisodeAiringLookup.ParseTmdb(show.RootElement, 1).Should().Be(new EpisodeAiring(10, 10, true));
        EpisodeAiringLookup.ParseTmdb(show.RootElement, null).Should().Be(new EpisodeAiring(34, null, false));
    }

    [Theory]
    [InlineData("https://jimaku.cc/entry/1/download/a.srt", true)]
    [InlineData("http://jimaku.cc/entry/1/download/a.srt", false)]
    [InlineData("https://jimaku.cc.evil.example/a.srt", false)]
    [InlineData("https://example.com/a.srt", false)]
    public void Client_OnlyFetchesJimakuUrls(string url, bool allowed)
    {
        JimakuClient.IsJimakuUrl(url).Should().Be(allowed);
    }
}
