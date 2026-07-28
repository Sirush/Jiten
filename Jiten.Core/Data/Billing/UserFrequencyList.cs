using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Jiten.Core.Data.Billing;

public enum FrequencyListMode
{
    Filters = 0,
    HandPicked = 1
}

public enum FrequencyListStatus
{
    Pending = 0,
    Generating = 1,
    Ready = 2,
    Failed = 3,
    /// <summary>
    /// A transient (unsaved) list whose generated files were removed after 48h. The row and its filter
    /// definition are kept so the user can regenerate it in one click.
    /// </summary>
    Expired = 4
}

/// <summary>
/// A user-built frequency list generated from a filtered subset of decks (or a hand-picked set).
/// Non-Full users create transient rows (<see cref="IsSaved"/> = false) that a cleanup job removes after
/// 48h; Full users can persist them, opt into monthly auto-update, and expose a public share slug.
/// </summary>
public class UserFrequencyList
{
    public long Id { get; set; }

    public string UserId { get; set; } = default!;

    public string Name { get; set; } = string.Empty;

    public FrequencyListMode Mode { get; set; }

    /// <summary>
    /// Serialised <see cref="FrequencyListDefinition"/> (filter blob or hand-picked deck ids).
    /// </summary>
    public string DefinitionJson { get; set; } = "{}";

    /// <summary>False for a transient result kept for 48h; true once persisted by a Full user.</summary>
    public bool IsSaved { get; set; }

    /// <summary>Full-only: regenerate monthly to track the growing catalogue.</summary>
    public bool AutoUpdate { get; set; }

    /// <summary>Full-only: URL-safe slug for anonymous download, null when not shared.</summary>
    public string? PublicSlug { get; set; }

    public string? ZipUrl { get; set; }
    public string? CsvUrl { get; set; }

    public int WordCount { get; set; }
    public int DeckCount { get; set; }

    public FrequencyListStatus Status { get; set; } = FrequencyListStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? GeneratedAt { get; set; }

    [NotMapped]
    public FrequencyListDefinition Definition
    {
        get
        {
            try { return JsonSerializer.Deserialize<FrequencyListDefinition>(DefinitionJson) ?? new(); }
            catch (JsonException) { return new(); }
        }
        set => DefinitionJson = JsonSerializer.Serialize(value);
    }
}

/// <summary>
/// The stored builder definition. In <see cref="FrequencyListMode.Filters"/> mode the filter fields drive
/// deck selection; in <see cref="FrequencyListMode.HandPicked"/> mode only <see cref="DeckIds"/> is used.
/// </summary>
public class FrequencyListDefinition
{
    public List<int> MediaTypes { get; set; } = new();
    public int? YearFrom { get; set; }
    public int? YearTo { get; set; }
    public List<int> GenresInclude { get; set; } = new();
    public List<int> GenresExclude { get; set; } = new();
    public List<int> TagsInclude { get; set; } = new();
    public List<int> TagsExclude { get; set; } = new();
    public double? DifficultyMin { get; set; }
    public double? DifficultyMax { get; set; }
    public List<int> DeckIds { get; set; } = new();
}
