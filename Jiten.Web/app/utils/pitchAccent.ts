export type PitchCategory = 'heiban' | 'atamadaka' | 'nakadaka' | 'odaka';

const SMALL_KANA = /[ぁぃぅぇぉゃゅょゎァィゥェォャュョヮ]/g;
const KANA = /[ぁ-ゖァ-ヺー]/g;

/** The kana of a reading written as ruby markup (食[た]べる) or as plain kana. */
export function readingKana(reading: string): string {
  return (reading.replace(/[一-鿿０-ｚ々ヵヶ]+(?=\[)/g, '').match(KANA) ?? []).join('');
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
