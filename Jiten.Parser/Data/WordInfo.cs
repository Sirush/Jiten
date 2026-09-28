using System.Globalization;
using System.Text;
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

    /// <summary>Merged ん was Sudachi's negative ぬ (認め+られ+ん); the deconjugator prefers the slurred-る path (してん).</summary>
    public bool IsSlurredNegative { get; set; }

    public bool WasReclassifiedFromSuffix { get; set; }
    public bool IsMergedInflection { get; set; }
    public int? ResolvedWordId { get; set; }

    /// <summary>Null for tokens that are not vocabulary; set once the misparse gates ran.</summary>
    public (int WordId, byte ReadingIndex)? KeptForm { get; set; }

    /// <summary>Sudachi's original interjection tag, kept across POS rewrites; blocks reading matches (イエーイ → 遺影).</summary>
    public bool IsKanaExclamation { get; set; }

    /// <summary>Sudachi's original katakana-noun tag, kept across POS rewrites; blocks kanji deconjugation (ハガナ → 剥ぐ).</summary>
    public bool IsKatakanaNounSurface { get; set; }

    /// <summary>Misparse gates must not overrule it (え|っつった); PreMatchedWordId is also set by compound matches.</summary>
    public bool PinnedByRewriteRule { get; set; }

    /// <summary>Compounds must not absorb it (ordinal 目); soft pins stay absorbable by attested spans (そうした).</summary>
    public bool HardPinned { get; set; }

    /// <summary>Extra cost of the cheapest rival lattice path; 99999 = no rival, null = not requested.</summary>
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
        KeptForm = other.KeptForm;
        SudachiBoundaryMargin = other.SudachiBoundaryMargin;
        IsKanaExclamation = other.IsKanaExclamation;
        IsKatakanaNounSurface = other.IsKatakanaNounSurface;
        PinnedByRewriteRule = other.PinnedByRewriteRule;
        HardPinned = other.HardPinned;
    }

    /// <summary>Parses one UTF-8 Sudachi output line, drawing the string fields from <paramref name="strings"/>.</summary>
    public WordInfo(ReadOnlySpan<byte> sudachiLine, SudachiStringPool strings)
    {
        // Text\tPOS\tNormalizedForm\tDictionaryForm\tKatakanaReading\tPitchIndex\tSplits[\tM=<margin>]
        var span = sudachiLine;

        int marginIdx = span.LastIndexOf("\tM="u8);
        if (marginIdx >= 0 && int.TryParse(span[(marginIdx + 3)..], NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out int margin))
        {
            if (margin >= 0)
                SudachiBoundaryMargin = margin;
            span = span[..marginIdx];
        }

        Span<int> tabPositions = stackalloc int[6];
        int tabCount = 0;
        for (int i = 0; i < span.Length && tabCount < 6; i++)
        {
            if (span[i] == (byte)'\t')
            {
                tabPositions[tabCount++] = i;
            }
        }

        if (tabCount < 5)
        {
            IsInvalid = true;
            return;
        }

        Text = strings.GetString(span[..tabPositions[0]]);

        var posBytes = span[(tabPositions[0] + 1)..tabPositions[1]];
        Span<char> posChars = posBytes.Length <= 256 ? stackalloc char[posBytes.Length] : new char[posBytes.Length];
        ReadOnlySpan<char> posSpan = posChars[..Encoding.UTF8.GetChars(posBytes, posChars)];

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

        NormalizedForm = strings.GetString(span[(tabPositions[1] + 1)..tabPositions[2]]);
        DictionaryForm = strings.GetString(span[(tabPositions[2] + 1)..tabPositions[3]]);
        Reading = strings.GetString(span[(tabPositions[3] + 1)..tabPositions[4]]);

        // 6th POS field is the conjugation form.
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
