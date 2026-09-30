import { describe, expect, it } from 'vitest';
import {
  DEFAULT_MEDIA_CARD_COLUMNS,
  MEDIA_CARD_STAT_IDS,
  deckHasStatData,
  isDefaultMediaCardStatColumns,
  mediaCardStatColumns,
  richestPreviewDeck,
  sanitiseMediaCardStatColumns,
  type MediaCardStatId,
} from '../app/utils/mediaCardStats';
import { type Deck, MediaType } from '../app/types';

const all = () => true;

describe('mediaCardStatColumns', () => {
  it('renders the built-in three columns when the profile has none', () => {
    expect(mediaCardStatColumns(null, all)).toEqual(DEFAULT_MEDIA_CARD_COLUMNS);
  });

  it('drops stats without data but keeps every column in place', () => {
    const noSpeech = (id: MediaCardStatId) => !['speechDuration', 'speechSpeed', 'runtime'].includes(id);
    const columns = mediaCardStatColumns(null, noSpeech);

    expect(columns).toHaveLength(3);
    expect(columns[0]).toEqual(['characters', 'wordCount', 'uniqueWords']);
    expect(columns[1]).toEqual(['uniqueKanji', 'averageSentenceLength', 'difficulty']);
  });

  it('renders custom columns as placed, never rebalancing them', () => {
    const columns: MediaCardStatId[][] = [[], ['difficulty', 'wordCount', 'uniqueKanji', 'dialogue'], ['characters']];

    expect(mediaCardStatColumns(columns, all)).toEqual(columns);
    expect(mediaCardStatColumns(columns, (id) => id !== 'characters')).toEqual([[], ['difficulty', 'wordCount', 'uniqueKanji', 'dialogue'], []]);
  });
});

describe('sanitiseMediaCardStatColumns', () => {
  it('always returns three columns, dropping unknown ids and repeats across columns', () => {
    expect(sanitiseMediaCardStatColumns([['wordCount', 'bogus'], ['wordCount', 7, 'difficulty']])).toEqual([['wordCount'], ['difficulty'], []]);
    expect(sanitiseMediaCardStatColumns([[], [], [], ['dialogue']])).toEqual([[], [], []]);
    expect(sanitiseMediaCardStatColumns([])).toEqual([[], [], []]);
  });

  it('returns null for anything that is not a list of columns, including the old flat list', () => {
    expect(sanitiseMediaCardStatColumns(null)).toBeNull();
    expect(sanitiseMediaCardStatColumns('wordCount')).toBeNull();
    expect(sanitiseMediaCardStatColumns(['wordCount', 'difficulty'])).toBeNull();
  });
});

describe('isDefaultMediaCardStatColumns', () => {
  it('recognises null and the built-in columns only', () => {
    expect(isDefaultMediaCardStatColumns(null)).toBe(true);
    expect(isDefaultMediaCardStatColumns(DEFAULT_MEDIA_CARD_COLUMNS.map((c) => [...c]))).toBe(true);
    expect(isDefaultMediaCardStatColumns([...DEFAULT_MEDIA_CARD_COLUMNS].reverse())).toBe(false);
  });

  it('covers every stat id in the built-in layout', () => {
    expect(DEFAULT_MEDIA_CARD_COLUMNS.flat().sort()).toEqual([...MEDIA_CARD_STAT_IDS].sort());
  });
});

const deck = (overrides: Partial<Deck>): Deck =>
  ({
    deckId: 1,
    mediaType: MediaType.Novel,
    coverName: 'cover.jpg',
    speechDuration: 0,
    speechSpeed: 0,
    averageSentenceLength: 0,
    hideAverageSentenceLength: false,
    difficulty: -1,
    dialoguePercentage: 0,
    hideDialoguePercentage: false,
    childrenDeckCount: 0,
    externalRating: 0,
    selectedWordOccurrences: 0,
    parentDeckId: 0,
    ...overrides,
  }) as Deck;

describe('deckHasStatData', () => {
  it('swaps character count for speech duration only on timed-speech media', () => {
    const anime = deck({ mediaType: MediaType.Anime, speechDuration: 60_000 });
    expect(deckHasStatData(anime, 'speechDuration')).toBe(true);
    expect(deckHasStatData(anime, 'characters')).toBe(false);
    const novel = deck({ speechDuration: 60_000 });
    expect(deckHasStatData(novel, 'speechDuration')).toBe(false);
    expect(deckHasStatData(novel, 'characters')).toBe(true);
  });

  it('reads a video its own runtime and a channel its median', () => {
    expect(deckHasStatData(deck({ mediaType: MediaType.YouTube, parentDeckId: 5, runtimeSeconds: 300 }), 'runtime')).toBe(true);
    expect(deckHasStatData(deck({ mediaType: MediaType.YouTube, medianChildRuntimeSeconds: 300 }), 'runtime')).toBe(true);
    expect(deckHasStatData(deck({ mediaType: MediaType.YouTube, runtimeSeconds: 300 }), 'runtime')).toBe(false);
  });

  it('leaves out an all-dialogue or no-dialogue percentage', () => {
    expect(deckHasStatData(deck({ dialoguePercentage: 100 }), 'dialogue')).toBe(false);
    expect(deckHasStatData(deck({ dialoguePercentage: 40 }), 'dialogue')).toBe(true);
  });
});

describe('richestPreviewDeck', () => {
  it('prefers the deck with the most stats over a more popular one', () => {
    const popular = deck({ deckId: 1, difficulty: 3 });
    const rich = deck({ deckId: 2, difficulty: 3, externalRating: 80, childrenDeckCount: 4 });
    expect(richestPreviewDeck([popular, rich])?.deckId).toBe(2);
  });

  it('breaks ties on a cover, then on list order', () => {
    const noCover = deck({ deckId: 1, coverName: 'nocover.jpg' });
    const withCover = deck({ deckId: 2 });
    const later = deck({ deckId: 3 });
    expect(richestPreviewDeck([noCover, withCover, later])?.deckId).toBe(2);
  });

  it('returns null for an empty list', () => {
    expect(richestPreviewDeck([])).toBeNull();
  });
});
