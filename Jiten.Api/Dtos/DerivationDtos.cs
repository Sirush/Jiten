namespace Jiten.Api.Dtos;

public class DerivationCategoryDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string ExampleBase { get; set; } = "";
    public string ExampleDerived { get; set; } = "";
    public string Explanation { get; set; } = "";
    public int PairCount { get; set; }
}

public class DerivationCategoryGroupDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Explanation { get; set; } = "";
    public int PairCount { get; set; }
    public List<DerivationCategoryDto> Categories { get; set; } = [];
}

/// <summary>Per-group marginal coverage for the viewer's own vocabulary.</summary>
public class DerivationPersonalSummaryDto
{
    /// <summary>Words covered for free under the current selection.</summary>
    public int TotalCoveredWords { get; set; }
    public List<DerivationGroupPersonalDto> Groups { get; set; } = [];
}

public class DerivationGroupPersonalDto
{
    public string Key { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Enabled: words covered thanks to this group; disabled: words enabling it would newly cover.
    /// Marginal against the current selection - transitivity makes group counts non-additive.</summary>
    public int CoveredWords { get; set; }
}

/// <summary>
/// Per-user marking for one group's preview list. Keys are (WordId &lt;&lt; 8) | ReadingIndex, never word ids:
/// one entry can hold both a covered and an uncovered reading, and a word-level mark would claim both.
/// </summary>
public class DerivationPersonalPairsDto
{
    /// <summary>Forms already redundant under the current selection, whichever group earns them.</summary>
    public List<long> RedundantKeys { get; set; } = [];
    /// <summary>
    /// This group's own marginal contribution, matching its count in the personal summary. Disjoint from
    /// <see cref="RedundantKeys"/> while the group is off, a subset of it once the group is on.
    /// </summary>
    public List<long> AddedByGroupKeys { get; set; } = [];
    /// <summary>Forms in this group that already count as known, on either side of the arrow.</summary>
    public List<long> StudiedKeys { get; set; } = [];
}

/// <summary>One base→derived mapping in the settings-page preview list.</summary>
public class DerivationPairDto
{
    public int BaseWordId { get; set; }
    public byte BaseReadingIndex { get; set; }
    public string BaseText { get; set; } = "";
    public string? BaseDefinition { get; set; }
    public int DerivedWordId { get; set; }
    public byte DerivedReadingIndex { get; set; }
    public string DerivedText { get; set; } = "";
    public string? DerivedDefinition { get; set; }
    /// <summary>The derived form's rank; 0 when unranked.</summary>
    public int FrequencyRank { get; set; }
    public string CategoryLabel { get; set; } = "";
    /// <summary>False on one-way pairs: the base covers the derived form but not the reverse.</summary>
    public bool Bidirectional { get; set; }
}

/// <summary>One end of a derivation link as shown on the word page.</summary>
public class WordDerivationDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string RubyText { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    /// <summary>Null on the anonymous, publicly-cached word endpoint, where no setting applies.</summary>
    public bool? Enabled { get; set; }
}

/// <summary>The family member whose knowledge makes the requested form redundant.</summary>
public class DerivationCoverDto
{
    public int WordId { get; set; }
    public byte ReadingIndex { get; set; }
    public string Text { get; set; } = "";
    public string CategoryKey { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
}
