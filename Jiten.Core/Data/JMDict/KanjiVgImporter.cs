using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;

namespace Jiten.Core.Data.JMDict;

public static class KanjiVgImporter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly XNamespace Kvg = "http://kanjivg.tagaini.net";

    public record ParsedKanji(KanjiStrokes Strokes, List<KanjiComponent> Components);

    public static async Task<bool> Import(IDbContextFactory<JitenDbContext> contextFactory, string zipPath)
    {
        Console.WriteLine("Parsing KanjiVG archive...");
        var parsed = new List<ParsedKanji>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                var character = CharacterFromFileName(entry.Name);
                if (character == null)
                    continue;

                await using var stream = entry.Open();
                parsed.Add(Parse(character, stream));
            }
        }

        var strokes = parsed.Select(p => p.Strokes).ToList();
        var components = parsed.SelectMany(p => p.Components).ToList();
        Console.WriteLine($"Parsed {strokes.Count} kanji with {components.Count} component nodes.");

        await using var context = await contextFactory.CreateDbContextAsync();

        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"KanjiStrokes\"");
        await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE jmdict.\"KanjiComponents\"");

        const int batchSize = 1000;
        for (int i = 0; i < strokes.Count; i += batchSize)
        {
            context.KanjiStrokes.AddRange(strokes.Skip(i).Take(batchSize));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        for (int i = 0; i < components.Count; i += batchSize)
        {
            context.KanjiComponents.AddRange(components.Skip(i).Take(batchSize));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
        }

        Console.WriteLine("KanjiVG import complete.");
        return true;
    }

    public static string? CharacterFromFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length != 5 || !int.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint))
            return null;

        return IsIdeograph(codePoint) ? char.ConvertFromUtf32(codePoint) : null;
    }

    public static ParsedKanji Parse(string character, Stream svg)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };
        using var reader = XmlReader.Create(svg, settings);
        var document = XDocument.Load(reader);

        var groups = document.Descendants(Svg + "g").ToList();
        var strokeGroup = groups.First(g => GroupIdStartsWith(g, "kvg:StrokePaths_"));
        var numberGroup = groups.FirstOrDefault(g => GroupIdStartsWith(g, "kvg:StrokeNumbers_"));

        var strokes = new KanjiStrokes
                      {
                          Character = character,
                          Paths = strokeGroup.Descendants(Svg + "path").Select(p => (string)p.Attribute("d")!).ToList(),
                          NumberPositions = numberGroup == null ? [] : ParseNumberPositions(numberGroup)
                      };

        var components = new List<KanjiComponent>();
        var root = strokeGroup.Element(Svg + "g");
        if (root != null)
            CollectComponents(character, root, null, components);

        return new ParsedKanji(strokes, components);
    }

    private static void CollectComponents(string character, XElement group, short? parentIndex, List<KanjiComponent> components)
    {
        foreach (var child in group.Elements(Svg + "g"))
        {
            var node = ToComponent(character, child, parentIndex, components);
            CollectComponents(character, child, node?.NodeIndex ?? parentIndex, components);
        }
    }

    private static KanjiComponent? ToComponent(string character, XElement group, short? parentIndex, List<KanjiComponent> components)
    {
        var phonetic = (string?)group.Attribute(Kvg + "phon");
        var element = (string?)group.Attribute(Kvg + "element") ?? (IsSingleCodePoint(phonetic) ? phonetic : null);
        if (element == null)
            return null;

        var radical = (string?)group.Attribute(Kvg + "radical");
        var isRadical = radical is "general" or "tradit";
        var isPhonetic = phonetic != null;

        if ((string?)group.Attribute(Kvg + "part") is { } part && part != "1")
        {
            var first = components.LastOrDefault(c => c.Component == element);
            if (first != null)
            {
                first.IsRadical |= isRadical;
                first.IsPhonetic |= isPhonetic;
                return first;
            }
        }

        var component = new KanjiComponent
                        {
                            KanjiCharacter = character, NodeIndex = (short)components.Count, ParentIndex = parentIndex, Component = element,
                            Original = (string?)group.Attribute(Kvg + "original"), IsRadical = isRadical, IsPhonetic = isPhonetic
                        };
        components.Add(component);
        return component;
    }

    private static List<float> ParseNumberPositions(XElement numberGroup)
    {
        var positions = new List<float>();
        foreach (var text in numberGroup.Elements(Svg + "text"))
        {
            // transform="matrix(1 0 0 1 x y)"
            var transform = ((string?)text.Attribute("transform") ?? "").Replace("matrix(", "").TrimEnd(')');
            var parts = transform.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 6)
                continue;

            positions.Add(float.Parse(parts[4], CultureInfo.InvariantCulture));
            positions.Add(float.Parse(parts[5], CultureInfo.InvariantCulture));
        }

        return positions;
    }

    private static bool GroupIdStartsWith(XElement group, string prefix) =>
        ((string?)group.Attribute("id"))?.StartsWith(prefix, StringComparison.Ordinal) == true;

    private static bool IsSingleCodePoint(string? value) =>
        value != null && (value.Length == 1 || (value.Length == 2 && char.IsSurrogatePair(value[0], value[1])));

    private static bool IsIdeograph(int codePoint) =>
        codePoint is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x3FFFF;
}
