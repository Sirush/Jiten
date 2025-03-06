using System.Text;
using System.Text.Json.Serialization;

namespace Jiten.Core.Data.JMDict;

public class JMDictFurigana
{
    [JsonPropertyName("text")] public string Text { get; set; }
    [JsonPropertyName("reading")] public string Reading { get; set; }
    [JsonPropertyName("furigana")] public List<Furigana> Furiganas { get; set; }

    public string Parse()
    {
        // form ruby[rt]
        var sb = new StringBuilder();
        foreach (var f in Furiganas)
        {
            if (!string.IsNullOrEmpty(f.Rt))
            {
                sb.Append($"{f.Ruby}[{f.Rt}]");
            }
            else
            {
                sb.Append(f.Ruby);
            }
        }

        return sb.ToString();
    }
}

public class Furigana
{
    [JsonPropertyName("ruby")] public string Ruby { get; set; }
    [JsonPropertyName("rt")] public string Rt { get; set; }
}