import type {
  CardImageBlockOptions,
  ConfusableReadingsBlockOptions,
  CustomMeaningBlockOptions,
  DeckOccurrencesBlockOptions,
  DefinitionsBlockOptions,
  DividerBlockOptions,
  EtymologyBlockOptions,
  ExampleSentenceBlockOptions,
  FrequencyRankBlockOptions,
  HeadwordBlockOptions,
  KanjiBreakdownBlockOptions,
  PitchAccentBlockOptions,
  WordCompositionBlockOptions,
  WordUsedInBlockOptions,
} from '~/types';

export const headwordDefaults: HeadwordBlockOptions = { furigana: 'afterFlip', showAudioButton: true, size: 'medium' };
export const exampleSentenceDefaults: ExampleSentenceBlockOptions = { blur: false, showSource: true, showActions: true, unblurOnFlip: false, size: 'medium' };
export const frequencyRankDefaults: FrequencyRankBlockOptions = { onlyAfterFlip: true };
export const definitionsDefaults: DefinitionsBlockOptions = { maxDefinitions: null, size: 'medium', spoiler: false };
export const customMeaningDefaults: CustomMeaningBlockOptions = { size: 'medium', spoiler: false };
export const etymologyDefaults: EtymologyBlockOptions = { spoiler: false };
export const confusableReadingsDefaults: ConfusableReadingsBlockOptions = { spoiler: false };
export const pitchAccentDefaults: PitchAccentBlockOptions = { hideHeading: false, spoiler: false };
export const kanjiBreakdownDefaults: KanjiBreakdownBlockOptions = { hideHeading: false, spoiler: false };
export const wordCompositionDefaults: WordCompositionBlockOptions = { hideHeading: false, spoiler: false };
export const wordUsedInDefaults: WordUsedInBlockOptions = { hideHeading: false, spoiler: false };
export const deckOccurrencesDefaults: DeckOccurrencesBlockOptions = { collapsed: false };
export const cardImageDefaults: CardImageBlockOptions = { layout: 'beside', blur: true };
export const dividerDefaults: DividerBlockOptions = { style: 'line', label: '' };

export function resolveOptions<T extends object>(defaults: T, options: Partial<T> | undefined): T {
  return { ...defaults, ...(options ?? {}) };
}
