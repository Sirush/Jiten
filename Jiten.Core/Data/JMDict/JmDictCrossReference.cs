namespace Jiten.Core.Data.JMDict;

public enum CrossReferenceType
{
    SeeAlso = 0,
    Antonym = 1,
    Synonym = 2
}

public enum CrossReferenceDict
{
    JMdict = 0,
    JMnedict = 1
}

/// <summary>
/// A JMdict NG &lt;xref&gt; cross-reference (see/ant/syn). One row per xref.
/// Resolved from the target seq# to a WordId in a second import pass; RawText is the display fallback.
/// </summary>
public class JmDictCrossReference
{
    public int Id { get; set; }

    /// <summary>Source word (the entry the xref appears in).</summary>
    public int FromWordId { get; set; }

    /// <summary>Source sense index; null = entry-level (xref currently always sits inside a sense).</summary>
    public int? FromSenseIndex { get; set; }

    public CrossReferenceType Type { get; set; }

    /// <summary>Resolved target WordId (seq# → WordId); null if unresolved or cross-dictionary.</summary>
    public int? TargetWordId { get; set; }

    public CrossReferenceDict TargetDict { get; set; } = CrossReferenceDict.JMdict;

    /// <summary>Target sense number (sno) when the xref points at a single sense.</summary>
    public short? TargetSenseIndex { get; set; }

    /// <summary>Target kanji form (xk attribute), for display/disambiguation.</summary>
    public string? TargetKanji { get; set; }

    /// <summary>Target reading (xr attribute), for display/disambiguation.</summary>
    public string? TargetReading { get; set; }

    /// <summary>Human-readable display string (the xref element text), always present.</summary>
    public string RawText { get; set; } = "";
}

/// <summary>lsource etymology element (stored as jsonb on the word).</summary>
public class JmDictLanguageSource
{
    public string Lang { get; set; } = "eng";
    public string Text { get; set; } = "";
    public bool IsWasei { get; set; }
    public bool IsPartial { get; set; }
}

/// <summary>Entry-level &lt;info&gt; note (stored as jsonb on the word).</summary>
public class JmDictEntryInfo
{
    public string Type { get; set; } = "note";
    public string Text { get; set; } = "";
}
