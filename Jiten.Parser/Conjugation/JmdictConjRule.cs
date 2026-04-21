namespace Jiten.Parser.Conjugation;

// One row from JMdictDB's conjo.csv.
//
// Semantics (ported from Ichiran dict-load.lisp:construct-conjugation):
//   total_stem = Stem + (1 if the chosen euph is non-empty)
//   euph       = iskana(word) ? Euphr : Euphk
//   surface    = word[0..len-total_stem] + euph + Okuri
//
// iskana(word): true iff the last 2 chars are all kana.
public readonly record struct JmdictConjRule(
    int PosId,      // kwpos.csv id (e.g. 28=v1, 33=v5k, 1=adj-i)
    int ConjId,     // conj.csv id (1=non-past, 2=past, 3=te, …, 13=continuative ~i)
    bool Negative,
    bool Formal,
    int OrderNum,   // onum — disambiguates alt forms of the same (pos, conj, neg, fml) slot
    int Stem,       // base chars to drop from the lemma
    string Okuri,   // suffix to append
    string Euphr,   // euphonic replacement when the lemma ends in kana
    string Euphk);  // euphonic replacement when the lemma ends in kanji
