import { inject, provide, type ComputedRef, type InjectionKey, type Ref } from 'vue';
import type { CardMediaDto, LanguageSource, StudyCardDto, StudyDeckOccurrenceDto, StudyExampleSentenceDto, StudySettingsDto, Word } from '~/types';

export interface CardSampleKanji {
  character: string;
  strokeCount: number;
  meaning: string;
  jlpt: number;
}

export interface CardSampleComposed {
  ruby: string;
  def: string;
}

export interface CardSampleUsedIn {
  ruby: string;
  rank: number;
  def: string;
}

/**
 * Static stand-in for a real card, used to render the block components inside the settings preview
 * where no live card, word data or network is available. Every field is required so a preview that
 * forgets to supply one fails to compile.
 */
export interface CardSampleData {
  isNew: boolean;
  wordRuby: string;
  wordPlain: string;
  reading: string;
  pitchAccent: number;
  frequencyRank: number;
  pos: string;
  definitions: string[];
  confusable: string[];
  kanji: CardSampleKanji[];
  composedOf: CardSampleComposed[];
  usedIn: CardSampleUsedIn[];
  usedInTotal: number;
  example: { text: string; word: string; source: string };
  languageSources: LanguageSource[];
  deckOccurrences: StudyDeckOccurrenceDto[];
  image: string;
  customMeaning: string;
}

export type WriteInFurigana = 'default' | 'hide' | 'show';

/**
 * Shared per-card state and services consumed by the card block components. Provided by
 * `SrsStudyCard` (live) and `SrsCardPreview` (sample); blocks are otherwise presentational.
 */
export interface CardContext {
  card: Ref<StudyCardDto | null>;
  settings: ComputedRef<StudySettingsDto>;
  isFlipped: Ref<boolean>;
  isPreview: boolean;
  sample: CardSampleData | null;

  wordData: Ref<Word | null>;
  wordLoading: Ref<boolean>;
  wordLoadFailed: Ref<boolean>;

  writeInActive: Ref<boolean>;
  writeInFrontFurigana: Ref<WriteInFurigana>;
  writeInOutcome: Ref<'correct' | 'wrong' | null>;
  writeInInputPhase: Ref<boolean>;

  cardImage: ComputedRef<CardMediaDto | null>;
  cardImageUrl: ComputedRef<string>;
  cardAudio: ComputedRef<CardMediaDto | null>;
  customAudioPlaying: Ref<boolean>;
  imageBlurred: ComputedRef<boolean>;
  showBesideImage: ComputedRef<boolean>;
  imageBesideLayout: ComputedRef<boolean>;
  hasCardMedia: ComputedRef<boolean>;
  canEditCardMedia: ComputedRef<boolean>;
  openMediaEditor: () => void;
  headWordTtsText: ComputedRef<string>;
  playCustomAudio: () => void;
  onImageError: () => void;
  revealImage: () => void;

  cardExample: ComputedRef<StudyExampleSentenceDto | null | undefined>;
  exampleRevealed: Ref<boolean>;
  revealExample: (side?: 'front' | 'back') => void;

  registerDictCycler: (fn: ((direction: 1 | -1) => void) | null) => void;
}

const CARD_CONTEXT: InjectionKey<CardContext> = Symbol('cardContext');

export function provideCardContext(ctx: CardContext) {
  provide(CARD_CONTEXT, ctx);
}

export function useCardContext(): CardContext {
  const ctx = inject(CARD_CONTEXT);
  if (!ctx) throw new Error('useCardContext must be used within a card that provides CardContext');
  return ctx;
}
