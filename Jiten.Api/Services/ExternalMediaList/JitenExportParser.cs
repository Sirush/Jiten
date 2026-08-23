using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Jiten.Core.Data;

namespace Jiten.Api.Services.ExternalMediaList;

public record JitenExportEntry(int DeckId, string Title, string SourceStatus, DeckStatus MappedStatus, bool IsFavourite, int? Progress);

public record JitenExportParseResult(List<JitenExportEntry> Entries, string? Error)
{
    public static JitenExportParseResult Fail(string error) => new([], error);
}

/// <summary>Reads back the CSV and JSON files produced by the media list export endpoint.</summary>
public static partial class JitenExportParser
{
    private const string NotAnExport = "This file does not look like a Jiten media list export.";

    public static JitenExportParseResult Parse(string content)
    {
        var text = content.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (text.Length == 0)
            return JitenExportParseResult.Fail("The file is empty.");

        var result = text[0] is '[' or '{' ? ParseJson(text) : ParseCsv(text);
        if (result.Error != null)
            return result;

        return result.Entries.Count == 0
            ? JitenExportParseResult.Fail("No entries with a status were found in the file.")
            : result;
    }

    private static JitenExportParseResult ParseJson(string text)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JitenExportParseResult.Fail("The file is not valid JSON.");
        }

        if (root.ValueKind != JsonValueKind.Array)
            return JitenExportParseResult.Fail(NotAnExport);

        var entries = new List<JitenExportEntry>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var deckId = ReadInt(item, "deckId") ?? DeckIdFromUrl(ReadString(item, "jitenUrl"));
            if (deckId is null or <= 0)
                continue;

            var raw = ReadString(item, "status");
            if (!TryParseStatus(raw, out var status))
                continue;

            var title = ReadString(item, "originalTitle") ?? ReadString(item, "romajiTitle") ?? ReadString(item, "englishTitle") ?? string.Empty;
            entries.Add(new JitenExportEntry(deckId.Value, title, raw ?? status.ToString(), status, ReadBool(item, "isFavourite"),
                                             NormalizeProgress(ReadInt(item, "progress"))));
        }

        return new JitenExportParseResult(entries, null);
    }

    private static JitenExportParseResult ParseCsv(string text)
    {
        var rows = SplitCsv(text);
        if (rows.Count == 0)
            return JitenExportParseResult.Fail(NotAnExport);

        var header = rows[0];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Length; i++)
            columns.TryAdd(header[i].Trim(), i);

        var hasId = columns.ContainsKey("DeckId") || columns.ContainsKey("JitenUrl");
        if (!hasId || !columns.ContainsKey("Status"))
            return JitenExportParseResult.Fail(NotAnExport);

        string? Cell(string[] row, string column) =>
            columns.TryGetValue(column, out var index) && index < row.Length && row[index].Length > 0 ? row[index] : null;

        var entries = new List<JitenExportEntry>();
        foreach (var row in rows.Skip(1))
        {
            if (row.All(c => c.Length == 0))
                continue;

            var deckId = int.TryParse(Cell(row, "DeckId"), out var parsed) ? parsed : DeckIdFromUrl(Cell(row, "JitenUrl"));
            if (deckId is null or <= 0)
                continue;

            var raw = Cell(row, "Status");
            if (!TryParseStatus(raw, out var status))
                continue;

            var title = Cell(row, "OriginalTitle") ?? Cell(row, "RomajiTitle") ?? Cell(row, "EnglishTitle") ?? string.Empty;
            var progress = NormalizeProgress(int.TryParse(Cell(row, "Progress"), out var units) ? units : null);
            entries.Add(new JitenExportEntry(deckId.Value, title, raw ?? status.ToString(), status, ParseBool(Cell(row, "IsFavourite")), progress));
        }

        return new JitenExportParseResult(entries, null);
    }

    private static List<string[]> SplitCsv(string text)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
                     {
                         HasHeaderRecord = false, BadDataFound = null, MissingFieldFound = null
                     };

        var rows = new List<string[]>();
        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, config);
        try
        {
            while (csv.Read())
            {
                if (csv.Parser.Record is { Length: > 0 } record)
                    rows.Add(record);
            }
        }
        catch (CsvHelperException)
        {
            return [];
        }

        return rows;
    }

    private static bool TryParseStatus(string? value, out DeckStatus status)
    {
        status = DeckStatus.None;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!Enum.TryParse(value.Trim(), ignoreCase: true, out status) || !Enum.IsDefined(status))
            return false;

        return status != DeckStatus.None;
    }

    private static int? DeckIdFromUrl(string? url) =>
        url != null && JitenDeckUrlRegex().Match(url) is { Success: true } match ? int.Parse(match.Groups[1].Value) : null;

    private static int? NormalizeProgress(int? value) => value is > 0 ? value : null;

    private static bool ParseBool(string? value) =>
        value != null && (bool.TryParse(value.Trim(), out var flag) ? flag : value.Trim() == "1");

    private static bool TryGetProperty(JsonElement item, string name, out JsonElement value)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement item, string name) =>
        TryGetProperty(item, name, out var value)
            ? value.ValueKind switch
              {
                  JsonValueKind.String => value.GetString(),
                  JsonValueKind.Number => value.ToString(),
                  _ => null,
              }
            : null;

    private static int? ReadInt(JsonElement item, string name)
    {
        if (!TryGetProperty(item, name, out var value))
            return null;

        return value.ValueKind switch
               {
                   JsonValueKind.Number when value.TryGetInt32(out var number) => number,
                   JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
                   _ => null,
               };
    }

    private static bool ReadBool(JsonElement item, string name)
    {
        if (!TryGetProperty(item, name, out var value))
            return false;

        return value.ValueKind switch
               {
                   JsonValueKind.True => true,
                   JsonValueKind.String => ParseBool(value.GetString()),
                   JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
                   _ => false,
               };
    }

    [GeneratedRegex(@"/decks/media/(\d{1,9})")]
    private static partial Regex JitenDeckUrlRegex();
}
