using StackExchange.Redis;

namespace Jiten.Api.Services;

/// <summary>
/// Tracks parent decks added/reparsed since the last embedding run, so a periodic sweep can
/// (re)embed them in one batched model load instead of loading the 6.8GB model per deck.
/// </summary>
public interface IPendingEmbeddingQueue
{
    Task AddAsync(int deckId);
    Task<List<int>> DrainAsync();
}

public class PendingEmbeddingQueue(IConnectionMultiplexer redis, ILogger<PendingEmbeddingQueue> logger)
    : RedisPendingDeckSet(redis, logger, "embedding:pending_decks"), IPendingEmbeddingQueue;
