namespace Jiten.Core.Data.JMDict;

/// <summary>One row per form pair: knowing (BaseWordId, BaseReadingIndex) covers (DerivedWordId,
/// DerivedReadingIndex). A pure artifact of --build-derivations; never hand-edited.</summary>
public class JmDictWordDerivation
{
    public int DerivationId { get; set; }
    public int BaseWordId { get; set; }
    public byte BaseReadingIndex { get; set; }
    public int DerivedWordId { get; set; }
    public byte DerivedReadingIndex { get; set; }
    public DerivationCategory Category { get; set; }
    public DerivationSource Source { get; set; }
    public DerivationDirection Direction { get; set; }
}
