using Jiten.Core.Data.JMDict;

namespace Jiten.Parser;

// Common surface for ConjugationTableGenerator (BFS) and
// ForwardConjugationGenerator (JMdictDB paradigms). Lets
// ConjugationCommands.cs pick a mode behind a flag.
public interface IConjugationGenerator
{
    bool IsConjugable(JmDictWord word);
    IEnumerable<ConjugatedFormRecord> Generate(JmDictWord word, int maxDepth = 3, int perWordCap = 300);
}
