namespace Jiten.Parser.Conjugation;

// Loads conjo.csv + kwpos.csv + conj.csv from Shared/resources/jmdictdb/ and
// exposes lookups by POS name (e.g. "v1", "v5k", "adj-i").
public sealed class JmdictConjRuleSet
{
    private readonly Dictionary<string, int> _posNameToId;
    private readonly Dictionary<int, string> _posIdToName;
    private readonly Dictionary<int, string> _conjIdToName;
    private readonly Dictionary<int, List<JmdictConjRule>> _rulesByPosId;

    public IReadOnlyDictionary<int, string> ConjIdToName => _conjIdToName;
    public IReadOnlyDictionary<int, string> PosIdToName => _posIdToName;

    private JmdictConjRuleSet(
        Dictionary<string, int> posNameToId,
        Dictionary<int, string> posIdToName,
        Dictionary<int, string> conjIdToName,
        Dictionary<int, List<JmdictConjRule>> rulesByPosId)
    {
        _posNameToId = posNameToId;
        _posIdToName = posIdToName;
        _conjIdToName = conjIdToName;
        _rulesByPosId = rulesByPosId;
    }

    public bool TryGetRules(string posName, out List<JmdictConjRule> rules)
    {
        if (_posNameToId.TryGetValue(posName, out var id) &&
            _rulesByPosId.TryGetValue(id, out var list))
        {
            rules = list;
            return true;
        }
        rules = null!;
        return false;
    }

    public static JmdictConjRuleSet FromSharedResources()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "jmdictdb");
        return Load(
            Path.Combine(dir, "kwpos.csv"),
            Path.Combine(dir, "conj.csv"),
            Path.Combine(dir, "conjo.csv"));
    }

    public static JmdictConjRuleSet Load(string kwposPath, string conjPath, string conjoPath)
    {
        var posNameToId = new Dictionary<string, int>(StringComparer.Ordinal);
        var posIdToName = new Dictionary<int, string>();
        foreach (var row in ReadTsv(kwposPath))
        {
            // id, kw, descr, ents
            int id = int.Parse(row[0]);
            string name = row[1];
            posNameToId[name] = id;
            posIdToName[id] = name;
        }

        var conjIdToName = new Dictionary<int, string>();
        foreach (var row in ReadTsv(conjPath))
        {
            // id, name
            int id = int.Parse(row[0]);
            conjIdToName[id] = row[1];
        }

        var rulesByPosId = new Dictionary<int, List<JmdictConjRule>>();
        foreach (var row in ReadTsv(conjoPath))
        {
            // pos, conj, neg, fml, onum, stem, okuri, euphr, euphk, pos2
            // pos2 is only populated for rules that re-classify the result to
            // a different POS for secondary conjugation chaining — we don't
            // need it for primary generation.
            // JMdictDB uses the literal sentinel "" (two double-quote chars)
            // to denote empty okurigana for conj=13 (continuative / masu-stem)
            // where the lemma's stem IS the surface (食べ from 食べる). Without
            // this normalization the forward generator emits garbage like
            // 屈し"" as a paradigm row.
            static string NormEmpty(string s) => s == "\"\"" ? string.Empty : s;

            var rule = new JmdictConjRule(
                PosId: int.Parse(row[0]),
                ConjId: int.Parse(row[1]),
                Negative: row[2] == "t",
                Formal: row[3] == "t",
                OrderNum: int.Parse(row[4]),
                Stem: int.Parse(row[5]),
                Okuri: row.Length > 6 ? NormEmpty(row[6]) : string.Empty,
                Euphr: row.Length > 7 ? NormEmpty(row[7]) : string.Empty,
                Euphk: row.Length > 8 ? NormEmpty(row[8]) : string.Empty);

            if (!rulesByPosId.TryGetValue(rule.PosId, out var list))
            {
                list = new List<JmdictConjRule>();
                rulesByPosId[rule.PosId] = list;
            }
            list.Add(rule);
        }

        return new JmdictConjRuleSet(posNameToId, posIdToName, conjIdToName, rulesByPosId);
    }

    private static IEnumerable<string[]> ReadTsv(string path)
    {
        bool first = true;
        foreach (var raw in File.ReadLines(path))
        {
            if (first) { first = false; continue; } // header
            if (string.IsNullOrWhiteSpace(raw)) continue;
            yield return raw.Split('\t');
        }
    }
}
