using Jiten.Api.Services;

namespace Jiten.Parser.Tests.Integration.Infrastructure;

/// <summary>Passes everything unless a test opts into the real one-second window via <see cref="Enabled"/>.</summary>
public class NoOpSrsDebounceService : ISrsDebounceService
{
    private readonly SrsDebounceService _real = new();

    public bool Enabled { get; set; }

    public bool TryAcquire(string operation, string userId, int wordId, byte readingIndex)
        => !Enabled || _real.TryAcquire(operation, userId, wordId, readingIndex);
}
