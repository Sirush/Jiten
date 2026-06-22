namespace Jiten.Core.Data.JMDict;

public class SyncParseResult
{
    public List<SyncEntry> Entries { get; set; } = [];
    public string? Created { get; set; }
    public string? Version { get; set; }
}

public class SyncEntry
{
    public int WordId { get; set; }
    public List<SyncForm> KanjiForms { get; set; } = [];
    public List<SyncForm> KanaForms { get; set; } = [];
    public List<SyncSense> Senses { get; set; } = [];

    // Entry-level NG fields
    public List<SyncLanguageSource> LanguageSources { get; set; } = [];
    public List<SyncEntryInfo> EntryInfos { get; set; } = [];
}

public class SyncForm
{
    public string Text { get; set; } = "";
    public JmDictFormType FormType { get; set; }
    public List<string> Priorities { get; set; } = [];
    public List<string> InfoTags { get; set; } = [];
    public bool IsNoKanji { get; set; }
    public List<string> Restrictions { get; set; } = [];
}

public class SyncSense
{
    public int SenseIndex { get; set; }
    public List<string> Pos { get; set; } = [];
    public List<string> Misc { get; set; } = [];
    public List<string> Field { get; set; } = [];
    public List<string> Dial { get; set; } = [];
    public List<string> StagK { get; set; } = [];
    public List<string> StagR { get; set; } = [];
    public List<string> SenseInfo { get; set; } = [];
    public List<string> EnglishMeanings { get; set; } = [];

    /// <summary>g_type per gloss, index-aligned with <see cref="EnglishMeanings"/>. "" = plain gloss.</summary>
    public List<string> GlossTypes { get; set; } = [];

    // Cross-references (xref); resolved to WordIds in a second pass.
    public List<SyncXref> Xrefs { get; set; } = [];
}

public class SyncXref
{
    public string Type { get; set; } = "see"; // see | ant | syn
    public int? Seq { get; set; }
    public short? Sno { get; set; }
    public string? Xk { get; set; }
    public string? Xr { get; set; }
    public string? Dict { get; set; }
    public string RawText { get; set; } = "";
}

public class SyncLanguageSource
{
    public string Lang { get; set; } = "eng";
    public string Text { get; set; } = "";
    public bool IsWasei { get; set; }
    public bool IsPartial { get; set; }
}

public class SyncEntryInfo
{
    public string Type { get; set; } = "note";
    public string Text { get; set; } = "";
}
