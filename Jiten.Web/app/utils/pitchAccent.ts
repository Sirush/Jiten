export type PitchCategory = 'heiban' | 'atamadaka' | 'nakadaka' | 'odaka';

const SMALL_KANA = /[ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ]/g;
const KANA = /[ぁ-ゖァ-ヺー]/g;
/** ヵ and ヶ count as ruby base text: in 一[いっ]ヶ月[かげつ] they are read by the furigana. A lone kana base is a counter like 何[なん]ケ[か]月[げつ]. */
const RUBY_BASE = /(?:[^[\]ぁ-ゖゝゞァ-ヴヷ-ヺヽヾー〜～]+|[ぁ-ゖァ-ヺ])(?=\[)/gu;
const DAKUTEN = '゙';
const LIGATURES: Record<string, string> = { ゟ: 'より', ヿ: 'コト' };

function voiced(kana: string): string {
  const composed = (kana + DAKUTEN).normalize('NFC');
  return composed.length === 1 ? composed : kana;
}

/** Expands ゝゞヽヾ to the kana they repeat, since each is a mora of its own. */
function expandIterationMarks(kana: string): string {
  let out = '';
  for (const ch of kana) {
    const previous = out.at(-1);
    if (previous && (ch === 'ゝ' || ch === 'ヽ')) out += previous;
    else if (previous && (ch === 'ゞ' || ch === 'ヾ')) out += voiced(previous);
    else out += ch;
  }
  return out;
}

/** The kana of a reading written as ruby markup (食[た]べる) or as plain kana; 〜 reads as ー and punctuation such as ・ is not a mora. */
export function readingKana(reading: string): string {
  const kana = reading
    .replace(RUBY_BASE, '')
    .replace(/[〜～]/g, 'ー')
    .replace(/[ゟヿ]/g, (ligature) => LIGATURES[ligature]!);
  return (expandIterationMarks(kana).match(KANA) ?? []).join('');
}

/** Small kana share the mora of the kana before them; っ and ー are morae of their own. */
export function moraCount(kana: string): number {
  return kana.replace(SMALL_KANA, '').length;
}

export function pitchCategory(accent: number, morae: number): PitchCategory | null {
  if (accent < 0 || morae <= 0 || accent > morae) return null;
  if (accent === 0) return 'heiban';
  if (accent === 1) return 'atamadaka';
  return accent === morae ? 'odaka' : 'nakadaka';
}

export function wordPitchCategory(reading: string, accents: number[] | null | undefined): PitchCategory | null {
  if (!accents?.length) return null;
  const morae = moraCount(readingKana(reading));
  const categories = new Set(accents.map((accent) => pitchCategory(accent, morae)));
  const [only] = categories;
  return categories.size === 1 && only ? only : null;
}

export function pitchColourClasses(reading: string, accents: number[] | null | undefined): string {
  const category = wordPitchCategory(reading, accents);
  if (!category) return '';
  return reading.includes('[') ? `pitch-coloured pitch-${category}` : `pitch-coloured pitch-kana pitch-${category}`;
}

export const PITCH_CATEGORY_LABELS: Record<PitchCategory, { name: string; description: string }> = {
  heiban: { name: 'Heiban', description: 'Starts low, rises, and never drops' },
  atamadaka: { name: 'Atamadaka', description: 'High on the first mora, then drops' },
  nakadaka: { name: 'Nakadaka', description: 'Drops inside the word' },
  odaka: { name: 'Odaka', description: 'Drops right after the word' },
};
