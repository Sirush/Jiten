using System.Text.Json;
using System.Text.Json.Serialization;
using Jiten.Core.Data;

namespace Jiten.Core.Services.SmartDeck;

/// <summary>Stored as the smartDeck document in UserSettings.SmartDeckJson; unknown fields are dropped on save.</summary>
public sealed record SmartDeckSettings
{
    /// <summary>Log-spaced steps; 0 means a title never fades (NeverFadesHalfLife).</summary>
    public static readonly int[] AllowedHalfLives = [7, 14, 30, 60, 90, 180, 365, SmartDeckConstants.NeverFadesHalfLife];

    /// <summary>True exactly while the user's Smart Deck row exists; removing the deck resets the whole document.</summary>
    public bool Enabled { get; init; }
    /// <summary>Survives deck removal: someone who dismissed the nudge or once had a Smart Deck is never shown it again.</summary>
    public bool PromoDismissed { get; init; }
    public bool WeighPlanning { get; init; }
    public int LookaheadUnits { get; init; } = SmartDeckConstants.DefaultLookaheadUnits;
    public Dictionary<MediaType, int> LookaheadByMediaType { get; init; } = new();
    public int TargetPercentage { get; init; } = SmartDeckConstants.DefaultTargetPercentage;
    public int RestTargetPercentage { get; init; } = SmartDeckConstants.DefaultRestTargetPercentage;
    public int RecencyHalfLifeDays { get; init; } = SmartDeckConstants.DefaultRecencyHalfLifeDays;
    public List<int> PinnedDeckIds { get; init; } = [];
    public List<int> IncludedDeckIds { get; init; } = [];
    public List<int> ExcludedDeckIds { get; init; } = [];
    /// <summary>Per-title override of the media-type default: true forces the DeckOrder cursor, false turns it off.</summary>
    public Dictionary<int, bool> SequenceOverrides { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public int LookaheadFor(MediaType mediaType)
        => LookaheadByMediaType.TryGetValue(mediaType, out var units) ? units : LookaheadUnits;

    public static SmartDeckSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new SmartDeckSettings();
        try
        {
            return (JsonSerializer.Deserialize<SmartDeckSettings>(json, JsonOptions) ?? new SmartDeckSettings()).Normalized();
        }
        catch (JsonException)
        {
            return new SmartDeckSettings();
        }
    }

    public string Serialize() => JsonSerializer.Serialize(Normalized(), JsonOptions);

    /// <summary>Clamps ranges, dedupes ids and resolves list conflicts: an exclusion removes the same id from the pinned and included lists.</summary>
    public SmartDeckSettings Normalized()
    {
        var excluded = ExcludedDeckIds.Where(id => id > 0).Distinct().ToList();
        var pinned = PinnedDeckIds.Where(id => id > 0 && !excluded.Contains(id)).Distinct().Take(SmartDeckConstants.MaxPins).ToList();
        var included = IncludedDeckIds.Where(id => id > 0 && !excluded.Contains(id)).Distinct().ToList();

        return this with
        {
            LookaheadUnits = Math.Clamp(LookaheadUnits, 1, SmartDeckConstants.MaxLookaheadUnits),
            LookaheadByMediaType = LookaheadByMediaType
                                   .Where(kv => SmartDeckConstants.LookaheadMediaTypes.Contains(kv.Key))
                                   .ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, 1, SmartDeckConstants.MaxLookaheadUnits)),
            TargetPercentage = Math.Clamp(TargetPercentage, SmartDeckConstants.MinTargetPercentage, SmartDeckConstants.MaxTargetPercentage),
            RestTargetPercentage = Math.Clamp(RestTargetPercentage, SmartDeckConstants.MinRestTargetPercentage, SmartDeckConstants.MaxTargetPercentage),
            RecencyHalfLifeDays = AllowedHalfLives.Contains(RecencyHalfLifeDays) ? RecencyHalfLifeDays : SmartDeckConstants.DefaultRecencyHalfLifeDays,
            PinnedDeckIds = pinned,
            IncludedDeckIds = included,
            ExcludedDeckIds = excluded,
            SequenceOverrides = SequenceOverrides.Where(kv => kv.Key > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
    }
}
