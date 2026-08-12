using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser;

public class WordInfo
{
    public string Text { get; set; } = string.Empty;
    public int StartOffset { get; set; } = -1;
    public int EndOffset { get; set; } = -1;
    public PartOfSpeech PartOfSpeech { get; set; }
    public PartOfSpeechSection PartOfSpeechSection1 { get; set; }
    public PartOfSpeechSection PartOfSpeechSection2 { get; set; }
    public PartOfSpeechSection PartOfSpeechSection3 { get; set; }
    public string NormalizedForm { get; set; } = string.Empty;
    public string DictionaryForm { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public bool IsInvalid { get; set; }
    public bool IsPersonNameContext { get; set; }
    public int? PreMatchedWordId { get; set; }
    public byte? PreMatchedReadingIndex { get; set; }
    public List<string>? PreMatchedConjugations { get; set; }
    public List<int>? PreMatchedCandidateWordIds { get; set; }
    public bool IsImperative { get; set; }

    /// Set when a merge absorbed an ん that Sudachi tagged as the negative auxiliary ぬ
    /// (認め+られ+ん). The deconjugator alone can't tell that ん from a slurred る (してん),
    /// and its slur path is shorter, so chain selection needs this to keep the negative sense.
    public bool IsSlurredNegative { get; set; }

    public bool WasReclassifiedFromSuffix { get; set; }
    public bool IsMergedInflection { get; set; }
    public int? ResolvedWordId { get; set; }

    /// Set when Sudachi originally tagged this pure-kana token as an interjection/filler.
    /// POS-relaxed lookup fallbacks must not let such exclamations match kanji-backed words
    /// through their reading keys (イエーイ → 遺影/家居). Survives the POS rewrites that the
    /// ProcessWord escalation chain performs.
    public bool IsKanaExclamation { get; set; }

    /// Set when Sudachi originally tagged this all-katakana token as a noun — a name/loanword
    /// shape. Deconjugation of its hiragana conversion must not land on kanji-primary words
    /// (ハガナ → はが+な → 剥ぐ fabricates vocabulary out of a name). Survives the POS rewrites
    /// that the ProcessWord escalation chain performs.
    public bool IsKatakanaNounSurface { get; set; }

    /// Set when a rewrite-rule template pinned this token — a deliberate lexical decision the
    /// misparse gates must not overrule (え|っつった). PreMatchedWordId alone can't carry this:
    /// parser-level machinery (compound matches, fallbacks) reuses it for ordinary tokens.
    public bool PinnedByRewriteRule { get; set; }

    /// Set when a gate's pin is a final word decision that compound formation must not absorb
    /// or override (the ぶん of a fraction frame, the ordinal 目). Soft pins — reading defaults
    /// like the する-family — stay absorbable: an attested expression spanning them (そうした,
    /// 臆病風に吹かれる) is the better parse.
    public bool HardPinned { get; set; }

    /// Sudachi lattice segmentation margin: extra cost of the cheapest competing lattice path
    /// crossing one of this token's boundaries (clamped to 99999 = no competitor).
    /// Null when margin output was not requested. Low values = uncertain segmentation.
    public int? SudachiBoundaryMargin { get; set; }

    public WordInfo(){}

    public WordInfo(WordInfo other)
    {
        Text = other.Text;
        StartOffset = other.StartOffset;
        EndOffset = other.EndOffset;
        PartOfSpeech = other.PartOfSpeech;
        PartOfSpeechSection1 = other.PartOfSpeechSection1;
        PartOfSpeechSection2 = other.PartOfSpeechSection2;
        PartOfSpeechSection3 = other.PartOfSpeechSection3;
        NormalizedForm = other.NormalizedForm;
        DictionaryForm = other.DictionaryForm;
        Reading = other.Reading;
        IsInvalid = other.IsInvalid;
        IsPersonNameContext = other.IsPersonNameContext;
        PreMatchedWordId = other.PreMatchedWordId;
        PreMatchedReadingIndex = other.PreMatchedReadingIndex;
        PreMatchedConjugations = other.PreMatchedConjugations?.ToList();
        PreMatchedCandidateWordIds = other.PreMatchedCandidateWordIds?.ToList();
        IsImperative = other.IsImperative;
        IsSlurredNegative = other.IsSlurredNegative;
        WasReclassifiedFromSuffix = other.WasReclassifiedFromSuffix;
        IsMergedInflection = other.IsMergedInflection;
        ResolvedWordId = other.ResolvedWordId;
        SudachiBoundaryMargin = other.SudachiBoundaryMargin;
        IsKanaExclamation = other.IsKanaExclamation;
        IsKatakanaNounSurface = other.IsKatakanaNounSurface;
        PinnedByRewriteRule = other.PinnedByRewriteRule;
        HardPinned = other.HardPinned;
    }

    public WordInfo(string sudachiLine)
    {
        // Parse tab-separated Sudachi output without Regex.Split
        // Format: Text\tPOS\tNormalizedForm\tDictionaryForm\tKatakanaReading\tPitchIndex\tSplits
        var span = sudachiLine.AsSpan();

        // Optional trailing segmentation margin column ("\tM=<int>", emitted by FFI v3)
        int marginIdx = sudachiLine.LastIndexOf("\tM=", StringComparison.Ordinal);
        if (marginIdx >= 0 && int.TryParse(span[(marginIdx + 3)..], out int margin))
        {
            if (margin >= 0)
                SudachiBoundaryMargin = margin;
            span = span[..marginIdx];
        }

        // Find first 6 tab positions
        Span<int> tabPositions = stackalloc int[6];
        int tabCount = 0;
        for (int i = 0; i < span.Length && tabCount < 6; i++)
        {
            if (span[i] == '\t')
            {
                tabPositions[tabCount++] = i;
            }
        }

        if (tabCount < 5)
        {
            IsInvalid = true;
            return;
        }

        // Extract Text (before first tab)
        Text = span[..tabPositions[0]].ToString();

        // Extract and parse POS (between first and second tab)
        var posSpan = span[(tabPositions[0] + 1)..tabPositions[1]];

        Span<int> commaPositions = stackalloc int[5];
        int commaCount = 0;
        for (int i = 0; i < posSpan.Length && commaCount < 5; i++)
        {
            if (posSpan[i] == ',')
            {
                commaPositions[commaCount++] = i;
            }
        }

        if (commaCount < 3)
        {
            IsInvalid = true;
            return;
        }

        PartOfSpeech = PosMapper.FromAny(posSpan[..commaPositions[0]]);
        PartOfSpeechSection1 = PartOfSpeechExtension.ToPartOfSpeechSection(posSpan[(commaPositions[0] + 1)..commaPositions[1]]);
        PartOfSpeechSection2 = PartOfSpeechExtension.ToPartOfSpeechSection(posSpan[(commaPositions[1] + 1)..commaPositions[2]]);
        PartOfSpeechSection3 = PartOfSpeechExtension.ToPartOfSpeechSection(commaCount >= 4
            ? posSpan[(commaPositions[2] + 1)..commaPositions[3]]
            : posSpan[(commaPositions[2] + 1)..]);

        // Extract remaining fields
        NormalizedForm = span[(tabPositions[1] + 1)..tabPositions[2]].ToString();
        DictionaryForm = span[(tabPositions[2] + 1)..tabPositions[3]].ToString();
        Reading = tabCount >= 5
            ? span[(tabPositions[3] + 1)..tabPositions[4]].ToString()
            : span[(tabPositions[3] + 1)..].ToString();

        // Parse conjugation form (6th POS field) for imperative detection
        if (commaCount >= 5)
        {
            var conjForm = posSpan[(commaPositions[4] + 1)..];
            IsImperative = conjForm.SequenceEqual("命令形".AsSpan());
        }
    }

    public bool HasPartOfSpeechSection(PartOfSpeechSection section)
    {
        return PartOfSpeechSection1 == section || PartOfSpeechSection2 == section || PartOfSpeechSection3 == section;
    }
}
