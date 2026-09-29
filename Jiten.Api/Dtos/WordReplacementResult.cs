namespace Jiten.Api.Dtos;

public class WordReplacementResult
{
    public int DeckWordsUpdated { get; set; }
    public int DeckWordsMerged { get; set; }
    public int ExampleSentenceWordsUpdated { get; set; }
    public int FsrsCardsUpdated { get; set; }
    /// <summary>Users holding cards on both pairs; the card with more reviews was kept and the other archived.</summary>
    public int FsrsCardsSkipped { get; set; }
    public int StudyDeckWordsUpdated { get; set; }
    public int WordSetMembersUpdated { get; set; }
    public int CardMediaMoved { get; set; }
    public int UserSentencesMoved { get; set; }
    public int CoverageUsersMarked { get; set; }
    public int AffectedDeckCount { get; set; }
    public int ParentDecksQueued { get; set; }
    public bool WasDryRun { get; set; }
}
