using Jiten.Api.Dtos;
using Jiten.Core.Data.JMDict;

namespace Jiten.Api.Helpers;

public static class ApiExtensions
{
    public static string ToWireString(this CrossReferenceType type) => type switch
    {
        CrossReferenceType.Antonym => "ant",
        CrossReferenceType.Synonym => "syn",
        _ => "see"
    };

    /// <summary>Groups cross-references for many words: WordId → (SenseIndex → xrefs). Pass the per-word
    /// inner map to <see cref="ToDefinitionDtos"/>.</summary>
    public static Dictionary<int, Dictionary<int, List<CrossReferenceDto>>> GroupXrefsByWord(
        this List<JmDictCrossReference> xrefs)
        => xrefs.GroupBy(x => x.FromWordId)
                .ToDictionary(g => g.Key, g => g.ToList().ToXrefsBySense());

    /// <summary>Groups a word's cross-references by source sense index for attachment to definitions.</summary>
    public static Dictionary<int, List<CrossReferenceDto>> ToXrefsBySense(this List<JmDictCrossReference> xrefs)
    {
        return xrefs
            .GroupBy(x => x.FromSenseIndex ?? -1)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new CrossReferenceDto
                {
                    Type = x.Type.ToWireString(),
                    TargetWordId = x.TargetWordId,
                    TargetText = x.RawText,
                    TargetKanji = x.TargetKanji,
                    TargetReading = x.TargetReading,
                    TargetSenseIndex = x.TargetSenseIndex
                }).ToList());
    }

    public static List<LanguageSourceDto>? ToDto(this List<JmDictLanguageSource> sources) =>
        sources.Count == 0
            ? null
            : sources.Select(s => new LanguageSourceDto
            {
                Lang = s.Lang, Text = s.Text, IsWasei = s.IsWasei, IsPartial = s.IsPartial
            }).ToList();

    public static List<DefinitionDto> ToDefinitionDtos(this List<JmDictDefinition> definitions,
        IReadOnlyDictionary<int, List<CrossReferenceDto>>? xrefsBySense = null)
    {
        int i = 1;
        List<DefinitionDto> definitionDtos = new();
        foreach (var definition in definitions.OrderBy(d => d.SenseIndex))
        {
            if (definition.EnglishMeanings.Count == 0)
                continue;

            List<CrossReferenceDto>? xrefs = null;
            xrefsBySense?.TryGetValue(definition.SenseIndex, out xrefs);

            // misc is dual-written into PartsOfSpeech for the parser (uk/arch checks); exclude it from the
            // displayed POS badges so it shows once, via the dedicated Misc field. Old words need a re-sync
            // to populate their Misc column before this separation takes effect.
            var posOnly = definition.Misc.Count > 0
                ? definition.PartsOfSpeech.Where(p => !definition.Misc.Contains(p)).ToList()
                : definition.PartsOfSpeech;

            definitionDtos.Add(new DefinitionDto
                               {
                                   Index = i++,
                                   Meanings = definition.EnglishMeanings,
                                   PartsOfSpeech = posOnly.ToHumanReadablePartsOfSpeech(),
                                   Pos = definition.Pos.Count > 0 ? definition.Pos : null,
                                   Misc = definition.Misc.Count > 0 ? definition.Misc : null,
                                   Field = definition.Field.Count > 0 ? definition.Field.ToHumanReadablePartsOfSpeech() : null,
                                   Dial = definition.Dial.Count > 0 ? definition.Dial.ToHumanReadablePartsOfSpeech() : null,
                                   SenseInfo = definition.SenseInfo.Count > 0 ? definition.SenseInfo : null,
                                   GlossTypes = definition.GlossTypes.Any(g => g.Length > 0) ? definition.GlossTypes : null,
                                   RestrictedToReadingIndices = definition.RestrictedToReadingIndices,
                                   CrossReferences = xrefs
                               });
        }

        return definitionDtos;
    }
}