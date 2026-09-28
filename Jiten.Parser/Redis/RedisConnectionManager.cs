using Microsoft.Extensions.Configuration;
using StackExchange.Redis;

namespace Jiten.Parser.Data.Redis;

/// <summary>One shared ConnectionMultiplexer; multiple instances are expensive.</summary>
public static class RedisConnectionManager
{
    private static ConnectionMultiplexer? _connection;
    private static readonly Lock _lock = new();
    private static string? _connectionString;

    public static IDatabase GetDatabase(IConfiguration configuration)
    {
        if (_connection is { IsConnected: true })
            return _connection.GetDatabase();

        lock (_lock)
        {
            if (_connection is { IsConnected: true })
                return _connection.GetDatabase();

            _connectionString = configuration.GetConnectionString("Redis")!;
            _connection = ConnectionMultiplexer.Connect(_connectionString);
            return _connection.GetDatabase();
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
