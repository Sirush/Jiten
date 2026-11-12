using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using StackExchange.Redis;

namespace Jiten.Api.Services;

/// <summary>Its message is shown to the user as the 400 body.</summary>
public class CheckException(string message) : Exception(message);

/// <summary>Answered with 410 so the client restarts the check, where any other <see cref="CheckException"/> lets it retry.</summary>
public sealed class CheckExpiredException(string message) : CheckException(message);

public sealed record CheckConfirmRequest(string SessionId, List<int> UnknownIds);

public interface ICheckSession
{
    string Id { get; }
    string UserId { get; }
}

public sealed class CheckSessionStore(IConnectionMultiplexer redis)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(2);
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<T> LoadAsync<T>(string prefix, string userId, string sessionId, string expiredMessage) where T : class, ICheckSession
    {
        var value = await redis.GetDatabase().StringGetAsync(Key(prefix, sessionId));
        var session = value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
        if (session == null || session.UserId != userId) throw new CheckExpiredException(expiredMessage);
        return session;
    }

    public Task SaveAsync<T>(string prefix, T session) where T : ICheckSession
        => redis.GetDatabase().StringSetAsync(Key(prefix, session.Id), JsonSerializer.Serialize(session, JsonOptions), Ttl);

    public Task DeleteAsync(string prefix, string sessionId) => redis.GetDatabase().KeyDeleteAsync(Key(prefix, sessionId));

    private static string Key(string prefix, string sessionId) => $"{prefix}:{sessionId}";
}

public static class CheckEndpoint
{
    public const int MaxAnswers = 64;

    public static async Task<IResult> Run<T>(string? userId, Func<string, Task<T>> action)
    {
        if (string.IsNullOrEmpty(userId)) return Results.Unauthorized();
        try
        {
            return Results.Ok(await action(userId));
        }
        catch (CheckExpiredException ex)
        {
            return Results.Json(ex.Message, statusCode: StatusCodes.Status410Gone);
        }
        catch (CheckException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }
}
