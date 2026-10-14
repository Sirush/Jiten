using Jiten.Core;
using Jiten.Core.Services;
using Jiten.Parser;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Api.Services;

public interface IParseVersionProvider
{
    Task<string> GetAsync(CancellationToken ct = default);
}

/// <summary>Reads the epoch once per process: parser caches keep serving pre-bump dictionary data until a Redis flush and restart.</summary>
public class ParseVersionProvider(IDbContextFactory<JitenDbContext> contextFactory) : IParseVersionProvider
{
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile string? _version;

    public async Task<string> GetAsync(CancellationToken ct = default)
    {
        if (_version != null)
            return _version;

        await _loadLock.WaitAsync(ct);
        try
        {
            if (_version != null)
                return _version;

            await using var db = await contextFactory.CreateDbContextAsync(ct);
            _version = ParseVersion.Format(await ParseDataEpochStore.ReadAsync(db, ct));
            return _version;
        }
        finally
        {
            _loadLock.Release();
        }
    }
}
