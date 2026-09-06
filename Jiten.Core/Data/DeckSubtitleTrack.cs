using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data;

/// <summary>One timed subtitle line, as shown beside the player.</summary>
public record SubtitleCue(
    [property: JsonPropertyName("s")] int StartMs,
    [property: JsonPropertyName("e")] int EndMs,
    [property: JsonPropertyName("t")] string Text);

/// <summary>
/// Timed transcript of a video deck, kept so watch mode can highlight the current line and seek by line.
/// Only the cues are stored; each watch request parses them, which is cheap for a transcript's size.
/// </summary>
public class DeckSubtitleTrack
{
    public int DeckId { get; set; }

    public string CuesJson { get; set; } = "[]";

    public Deck Deck { get; set; } = null!;

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static DeckSubtitleTrack FromItems(IEnumerable<SubtitleItem> items)
    {
        var cues = items.Where(i => !string.IsNullOrWhiteSpace(i.Text))
                        .OrderBy(i => i.StartMs)
                        .Select(i => new SubtitleCue(i.StartMs, i.EndMs, i.Text.Trim()))
                        .ToList();
        return new DeckSubtitleTrack { CuesJson = JsonSerializer.Serialize(cues, Json) };
    }

    public List<SubtitleCue> GetCues() => JsonSerializer.Deserialize<List<SubtitleCue>>(CuesJson, Json) ?? [];
}
