namespace Jiten.Core.Data.FSRS;

public class FsrsImportResultDto
{
    public required int CardsImported { get; set; }
    public required int CardsSkipped { get; set; }
    public required int CardsUpdated { get; set; }
    public required int ReviewLogsImported { get; set; }
    public int CustomSentencesImported { get; set; }
    public int CustomSentencesSkipped { get; set; }
    public int CustomMeaningsImported { get; set; }
    public int CustomMeaningsSkipped { get; set; }
    public List<string> ValidationErrors { get; set; } = [];
}
