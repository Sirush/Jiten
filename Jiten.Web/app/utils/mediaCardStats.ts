import { type Deck, MediaType } from '~/types';

/** Mirrors DisplayProfileSanitizer.MediaCardStatIds on the API; both sides must gain an id together. */
export const MEDIA_CARD_STAT_IDS = [
  'speechDuration',
  'characters',
  'wordCount',
  'uniqueWords',
  'uniqueKanji',
  'averageSentenceLength',
  'speechSpeed',
  'difficulty',
  'dialogue',
  'children',
  'runtime',
  'readingDuration',
  'externalRating',
  'appears',
] as const;

export type MediaCardStatId = (typeof MEDIA_CARD_STAT_IDS)[number];

export const MEDIA_CARD_STATS: Record<MediaCardStatId, { label: string; hint?: string }> = {
  speechDuration: { label: 'Speech duration', hint: 'Anime, dramas, movies, audio and YouTube' },
  characters: { label: 'Character count', hint: 'Media without timed speech' },
  wordCount: { label: 'Word count' },
  uniqueWords: { label: 'Unique words' },
  uniqueKanji: { label: 'Unique kanji' },
  averageSentenceLength: { label: 'Average sentence length' },
  speechSpeed: { label: 'Speech speed', hint: 'Media with timed speech' },
  difficulty: { label: 'Difficulty' },
  dialogue: { label: 'Dialogue percentage' },
  children: { label: 'Episodes, volumes or subdecks' },
  runtime: { label: 'Video length', hint: 'YouTube' },
  readingDuration: { label: 'Reading duration', hint: 'Novels, non-fiction, visual novels and web novels' },
  externalRating: { label: 'External rating' },
  appears: { label: 'Appearances of a word', hint: 'Media lists on word pages' },
};

export const MEDIA_CARD_COLUMN_COUNT = 3;

/** Stat ids per card column, top to bottom; sanitised values always hold exactly three columns. */
export type MediaCardStatColumns = MediaCardStatId[][];

/** The built-in layout; a profile without its own columns renders exactly this. */
export const DEFAULT_MEDIA_CARD_COLUMNS: MediaCardStatColumns = [
  ['speechDuration', 'characters', 'wordCount', 'uniqueWords'],
  ['uniqueKanji', 'averageSentenceLength', 'speechSpeed', 'difficulty'],
  ['dialogue', 'children', 'runtime', 'readingDuration', 'externalRating', 'appears'],
];

const statIdSet = new Set<string>(MEDIA_CARD_STAT_IDS);

export function isMediaCardStatId(value: unknown): value is MediaCardStatId {
  return typeof value === 'string' && statIdSet.has(value);
}

/** Null keeps the built-in layout; extra columns, unknown ids and repeats are dropped, and empty columns stay empty. */
export function sanitiseMediaCardStatColumns(value: unknown): MediaCardStatColumns | null {
  if (!Array.isArray(value) || !value.every(Array.isArray)) return null;
  const seen = new Set<MediaCardStatId>();
  return Array.from({ length: MEDIA_CARD_COLUMN_COUNT }, (_, i) => {
    const kept: MediaCardStatId[] = [];
    for (const id of (value[i] as unknown[] | undefined) ?? []) {
      if (isMediaCardStatId(id) && !seen.has(id)) {
        seen.add(id);
        kept.push(id);
      }
    }
    return kept;
  });
}

export function isDefaultMediaCardStatColumns(columns: MediaCardStatColumns | null): boolean {
  return columns === null || JSON.stringify(columns) === JSON.stringify(DEFAULT_MEDIA_CARD_COLUMNS);
}

/** Every column keeps its place even when none of its stats have data, so a stat never jumps to another column. */
export function mediaCardStatColumns(columns: MediaCardStatColumns | null, isVisible: (id: MediaCardStatId) => boolean): MediaCardStatColumns {
  return (columns ?? DEFAULT_MEDIA_CARD_COLUMNS).map((column) => column.filter(isVisible));
}

const TIMED_SPEECH_TYPES = [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube];
const READING_TYPES = [MediaType.Novel, MediaType.NonFiction, MediaType.VisualNovel, MediaType.WebNovel];

/** Whether a deck carries the data a stat needs; display settings and the card's own context can still leave it out. */
export function deckHasStatData(deck: Deck, id: MediaCardStatId): boolean {
  const hasSpeechDuration = TIMED_SPEECH_TYPES.includes(deck.mediaType) && deck.speechDuration > 0;
  switch (id) {
    case 'speechDuration':
      return hasSpeechDuration;
    case 'characters':
      return !hasSpeechDuration;
    case 'wordCount':
    case 'uniqueWords':
    case 'uniqueKanji':
      return true;
    case 'averageSentenceLength':
      return deck.averageSentenceLength !== 0 && !deck.hideAverageSentenceLength;
    case 'speechSpeed':
      return (deck.speechSpeed ?? 0) > 0;
    case 'difficulty':
      return deck.difficulty != -1;
    case 'dialogue':
      return !deck.hideDialoguePercentage && deck.dialoguePercentage != 0 && deck.dialoguePercentage != 100;
    case 'children':
      return deck.childrenDeckCount != 0;
    case 'runtime':
      return !!(deck.parentDeckId ? deck.runtimeSeconds : deck.medianChildRuntimeSeconds);
    case 'readingDuration':
      return READING_TYPES.includes(deck.mediaType);
    case 'externalRating':
      return deck.externalRating != 0;
    case 'appears':
      return deck.selectedWordOccurrences != 0;
  }
}

/** The deck showing the most stats, then the most card sections, then one with a cover; ties keep list order. */
export function richestPreviewDeck(decks: Deck[]): Deck | null {
  let best: Deck | null = null;
  let bestScore = -1;
  for (const deck of decks) {
    const stats = MEDIA_CARD_STAT_IDS.filter((id) => deckHasStatData(deck, id)).length;
    const sections = [deck.description, deck.genres?.length, deck.tags?.length, deck.relationships?.length].filter(Boolean).length;
    const score = stats * 10 + sections * 2 + (deck.coverName && deck.coverName !== 'nocover.jpg' ? 1 : 0);
    if (score > bestScore) {
      best = deck;
      bestScore = score;
    }
  }
  return best;
}
