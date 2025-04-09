using System.Text.RegularExpressions;
using Jiten.Core;
using Jiten.Core.Data;

namespace Jiten.Parser;

public class WordInfo
{
    public string Text { get; set; } = string.Empty;
    public PartOfSpeech PartOfSpeech { get; set; }
    public PartOfSpeechSection PartOfSpeechSection1 { get; set; }
    public PartOfSpeechSection PartOfSpeechSection2 { get; set; }
    public PartOfSpeechSection PartOfSpeechSection3 { get; set; }
    public string NormalizedForm { get; set; } = string.Empty;
    public string DictionaryForm { get; set; } = string.Empty;
    public string Reading { get; set; } = string.Empty;
    public bool IsInvalid { get; set; }
    
    public WordInfo(){}
    
    public WordInfo(WordInfo other)
    {
        Text = other.Text;
        PartOfSpeech = other.PartOfSpeech;
        PartOfSpeechSection1 = other.PartOfSpeechSection1;
        PartOfSpeechSection2 = other.PartOfSpeechSection2;
        PartOfSpeechSection3 = other.PartOfSpeechSection3;
        NormalizedForm = other.NormalizedForm;
        DictionaryForm = other.DictionaryForm;
        Reading = other.Reading;
        IsInvalid = other.IsInvalid;
    }
    
    public WordInfo(string sudachiLine)
    {
        var parts = Regex.Split(sudachiLine, @"\t");

        if (parts.Length < 6)
        {
            IsInvalid = true;
            return;
        }

        var pos = parts[1].Split(",");
        
        if (pos.Length < 4)
        {
            IsInvalid = true;
            return;
        }

        Text = parts[0];
        PartOfSpeech = pos[0].ToPartOfSpeech();
        PartOfSpeechSection1 = pos[1].ToPartOfSpeechSection();
        PartOfSpeechSection2 = pos[2].ToPartOfSpeechSection();
        PartOfSpeechSection3 = pos[3].ToPartOfSpeechSection();
        NormalizedForm = parts[2];
        DictionaryForm = parts[3];
        Reading = parts[5];
    }
    
    public bool HasPartOfSpeechSection(PartOfSpeechSection section)
    {
        return PartOfSpeechSection1 == section || PartOfSpeechSection2 == section || PartOfSpeechSection3 == section;
    }
}