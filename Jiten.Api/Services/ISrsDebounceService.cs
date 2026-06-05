namespace Jiten.Api.Services;

public interface ISrsDebounceService
{
    /// <summary>
    /// Rate-limits rapid duplicate operations on the same card. The <paramref name="operation"/>
    /// namespaces the bucket so different operations (e.g. review vs undo) never block each other.
    /// </summary>
    bool TryAcquire(string operation, string userId, int wordId, byte readingIndex);
}
