using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Jiten.Core.Data;

public class WordSet
{
    public int SetId { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int WordCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? RuleJson { get; set; }
    public DateTime? RegeneratedAt { get; set; }

    public ICollection<WordSetMember> Members { get; set; } = new List<WordSetMember>();
    public ICollection<WordSetExclusion> Exclusions { get; set; } = new List<WordSetExclusion>();

    [NotMapped]
    public WordSetRule? Rule
    {
        get => string.IsNullOrEmpty(RuleJson) ? null : JsonSerializer.Deserialize<WordSetRule>(RuleJson);
        set => RuleJson = value == null ? null : JsonSerializer.Serialize(value);
    }
}
