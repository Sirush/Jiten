using System.Text.Json;
using System.Text.RegularExpressions;
using Jiten.Api.Dtos.Requests;

namespace Jiten.Api.Helpers;

public static partial class SignupSourceSanitizer
{
    private static readonly HashSet<string> AgeBuckets = ["0", "1-6", "7-29", "30+"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex(@"^/[A-Za-z0-9/:()._\-\[\]]{0,119}$")]
    private static partial Regex RoutePattern();

    [GeneratedRegex(@"^[a-z0-9.\-]{1,100}$")]
    private static partial Regex HostPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{1,50}$")]
    private static partial Regex UtmPattern();

    [GeneratedRegex(@"^[a-z0-9_]{1,40}$")]
    private static partial Regex PromptPattern();

    /// <summary>Returns the JSON to store, or null when nothing usable was sent.</summary>
    public static string? ToJson(SignupSourceDto? source)
    {
        if (source == null) return null;

        var referrer = source.Referrer?.Trim().ToLowerInvariant();
        var clean = new SignupSourceDto
        {
            Route = Keep(source.Route?.Trim(), RoutePattern()),
            // An empty referrer is a real value: the first visit was direct.
            Referrer = referrer == "" ? "" : Keep(referrer, HostPattern()),
            Utm = Keep(source.Utm?.Trim(), UtmPattern()),
            Age = source.Age != null && AgeBuckets.Contains(source.Age) ? source.Age : null,
            Prompt = Keep(source.Prompt?.Trim(), PromptPattern())
        };

        if (clean.Route == null && clean.Referrer == null && clean.Utm == null && clean.Age == null && clean.Prompt == null)
            return null;

        return JsonSerializer.Serialize(clean, JsonOptions);
    }

    private static string? Keep(string? value, Regex pattern) =>
        !string.IsNullOrEmpty(value) && pattern.IsMatch(value) ? value : null;
}
