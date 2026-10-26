using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Jiten.Api.Services;

/// <summary>Admits one visit per visitor per entity per UTC day</summary>
public sealed class DailyVisitorFilter
{
    private readonly object _dayLock = new();
    private DateOnly _visitorDay = DateOnly.FromDateTime(DateTime.UtcNow);
    private byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private ConcurrentDictionary<int, ConcurrentDictionary<ulong, byte>> _seenToday = new();

    public bool TryAdmit(int entityId, string visitorKey)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (today != _visitorDay)
        {
            lock (_dayLock)
            {
                if (today != _visitorDay)
                {
                    _visitorDay = today;
                    _salt = RandomNumberGenerator.GetBytes(32);
                    _seenToday = new ConcurrentDictionary<int, ConcurrentDictionary<ulong, byte>>();
                }
            }
        }

        var visitor = VisitorHash(visitorKey);
        var seen = _seenToday.GetOrAdd(entityId, _ => new ConcurrentDictionary<ulong, byte>());
        return seen.TryAdd(visitor, 0);
    }

    private ulong VisitorHash(string visitorKey)
    {
        var input = new byte[_salt.Length + Encoding.UTF8.GetByteCount(visitorKey)];
        _salt.CopyTo(input, 0);
        Encoding.UTF8.GetBytes(visitorKey, 0, visitorKey.Length, input, _salt.Length);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        return BitConverter.ToUInt64(digest);
    }
}
