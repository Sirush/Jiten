using System.Security.Claims;
using Jiten.Core.Data;

namespace Jiten.Api.Services;

/// <summary>Cards created (Inserted) versus pre-existing cards moved to the target state (Updated). Restored counts inserted cards that reclaimed an archived history.</summary>
public record VocabularyUpsertResult(int Inserted, int Updated, int Restored = 0);

public interface ICurrentUserService
{
    string? UserId { get; }
    bool IsAuthenticated { get; }
    ClaimsPrincipal? Principal { get; }
    Task<Dictionary<(int WordId, byte ReadingIndex), List<KnownState>>> GetKnownWordsState(IEnumerable<(int WordId, byte ReadingIndex)> keys);
    Task<List<KnownState>> GetKnownWordState(int wordId, byte readingIndex);
    Task<Dictionary<(int, byte), WordSetStateType>> GetWordSetDerivedStates();
    /// <summary>countAsNewlyLearned spaces the inserted cards out so the coverage journey dates them instead of treating the batch as prior knowledge.</summary>
    Task<VocabularyUpsertResult> AddKnownWords(IEnumerable<DeckWord> deckWords, bool overwriteExisting = true, bool countAsNewlyLearned = false);
    Task<VocabularyUpsertResult> BlacklistWords(IEnumerable<DeckWord> deckWords, bool overwriteExisting = true);
    Task<VocabularyUpsertResult> AddKnownWord(int wordId, byte readingIndex);
    Task RemoveKnownWord(int wordId, byte readingIndex);
}
