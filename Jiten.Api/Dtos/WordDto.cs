using Jiten.Core.Data.JMDict;
using Jiten.Core.Data;

namespace Jiten.Api.Dtos;

public class WordDto
{
    public int WordId { get; set; }
    public WordFormDto MainReading { get; set; } = new();
    public List<WordFormDto> AlternativeReadings { get; set; } = new();
    public List<string> PartsOfSpeech { get; set; } = new();
    public List<DefinitionDto> Definitions { get; set; } = new();
    public int Occurrences { get; set; }
    public List<int>? PitchAccents { get; set; }
    public List<KnownState> KnownStates { get; set; } = new();
    public List<WordSummaryDto>? ComposedOf { get; set; }
    public List<WordSummaryDto>? UsedIn { get; set; }
    public int UsedInTotal { get; set; }

    /// <summary>lsource etymology / wasei entries (entry-level).</summary>
    public List<LanguageSourceDto>? LanguageSources { get; set; }

    /// <summary>Entry-level &lt;info&gt; notes.</summary>
    public List<string>? EntryInfo { get; set; }

    public List<WordDerivationDto>? DerivedFrom { get; set; }
    public List<WordDerivationDto>? Derives { get; set; }

    /// <summary>Set only when this form has no card of its own and an enabled derivation covers it.</summary>
    public DerivationCoverDto? RedundantVia { get; set; }
}

public class LanguageSourceDto
{
    public string Lang { get; set; } = "eng";
    public string Text { get; set; } = "";
    public bool IsWasei { get; set; }
    public bool IsPartial { get; set; }
}

public class UsedInPageDto
{
    public List<WordSummaryDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class WordFormDto
{
    public string Text { get; set; } = "";
    public JmDictReadingType ReadingType { get; set; }
    public byte ReadingIndex { get; set; }
    public int FrequencyRank { get; set; }
    public double FrequencyPercentage { get; set; }
    public int UsedInMediaAmount { get; set; }
    public Dictionary<int, int> UsedInMediaAmountByType { get; set; } = new();
}