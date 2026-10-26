using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jiten.Api.Services.Forum;

public static class DiscourseWebhook
{
    private const string SignaturePrefix = "sha256=";

    /// <summary>Discourse signs the raw body with HMAC-SHA256 of the webhook secret, sent as <c>sha256=&lt;hex&gt;</c> in X-Discourse-Event-Signature.</summary>
    public static bool IsSigned(byte[] body, string? signature, string secret)
    {
        if (string.IsNullOrEmpty(secret) || signature is null || !signature.StartsWith(SignaturePrefix, StringComparison.Ordinal)) return false;

        byte[] given;
        try
        {
            given = Convert.FromHexString(signature.AsSpan(SignaturePrefix.Length));
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(given, HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));
    }

    /// <summary>Post events carry <c>post.topic_id</c>, topic events <c>topic.id</c>; anything else (ping, users) has no topic.</summary>
    public static int? TopicId(byte[] body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return ReadInt(root, "post", "topic_id") ?? ReadInt(root, "topic", "id");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement root, string parent, string property) =>
        root.TryGetProperty(parent, out var node) && node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty(property, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
}
