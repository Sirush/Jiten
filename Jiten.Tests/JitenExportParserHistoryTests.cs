using FluentAssertions;
using Jiten.Api.Services.ExternalMediaList;
using Jiten.Core.Data;

namespace Jiten.Tests;

public class JitenExportParserHistoryTests
{
    [Fact]
    public void Csv_ReadsTheReadingColumns()
    {
        const string csv = "DeckId,OriginalTitle,Status,StartedOn,FinishedOn,TimesCompleted,CharactersRead\n" +
                           "12,Title,Completed,2024-01-02,2024-02-03,2,800\n";

        var entry = JitenExportParser.Parse(csv).Entries.Single();

        entry.StartedOn.Should().Be(new DateOnly(2024, 1, 2));
        entry.FinishedOn.Should().Be(new DateOnly(2024, 2, 3));
        entry.TimesCompleted.Should().Be(2);
        entry.CharactersRead.Should().Be(800);
    }

    [Fact]
    public void Csv_WithoutReadingColumns_StillParses()
    {
        var entry = JitenExportParser.Parse("DeckId,Status\n12,Ongoing\n").Entries.Single();

        entry.StartedOn.Should().BeNull();
        entry.TimesCompleted.Should().Be(0);
    }

    [Fact]
    public void Json_ReadsTheFullHistory()
    {
        const string json = """
                            [{ "deckId": 12, "status": "Ongoing",
                               "history": [ { "state": "Completed", "finishedOn": "2021-03-04", "charactersRead": 500 },
                                          { "state": "InProgress", "startedOn": "2024-01-01" },
                                          { "state": "Bogus" } ] }]
                            """;

        var history = JitenExportParser.Parse(json).Entries.Single().History!;

        history.Select(r => r.State).Should().Equal(MediaListEntryState.Completed, MediaListEntryState.InProgress);
        history[0].FinishedOn.Should().Be(new DateOnly(2021, 3, 4));
        history[0].CharactersRead.Should().Be(500);
        history[1].StartedOn.Should().Be(new DateOnly(2024, 1, 1));
    }

    [Fact]
    public void Json_ReadsVolumesWithTheirStatusHistoryAndSeriesLink()
    {
        const string json = """
                            [{ "deckId": 12, "status": "Ongoing",
                               "history": [ { "state": "Bogus" }, { "state": "Completed" }, { "state": "InProgress", "isCurrent": true } ],
                               "volumes": [ { "deckId": 13, "status": "Completed",
                                              "history": [ { "state": "Completed", "finishedOn": "2022-01-01", "seriesEntry": 2 },
                                                           { "state": "Completed", "seriesEntry": 0 } ] },
                                            { "deckId": 14, "status": "None" },
                                            { "status": "Ongoing" } ] }]
                            """;

        var volume = JitenExportParser.Parse(json).Entries.Single().Volumes!.Single();

        volume.DeckId.Should().Be(13);
        volume.Status.Should().Be(DeckStatus.Completed);
        volume.History![0].FinishedOn.Should().Be(new DateOnly(2022, 1, 1));
        volume.History.Select(r => r.SeriesEntry).Should().Equal(1, null);
    }
}
