import { describe, expect, it } from 'vitest';
import { DeckStatus, MediaListEntryState, MediaType, SortOrder } from '../app/types/enums';
import type { Deck, MediaListEntrySummary } from '../app/types/types';
import {
  canTrackUnits,
  closingPassStart,
  dropQuestion,
  entryCharacters,
  entryProgressPercent,
  formatCharacters,
  dateToIso,
  listEntryFacts,
  entryPace,
  mediaWords,
  progressFieldLabel,
  releaseDateChoice,
  restartQuestion,
  unitWord,
  unitsFact,
} from '../app/utils/mediaListEntry';
import { sortDecks } from '../app/utils/deckSorting';

const summary = (overrides: Partial<MediaListEntrySummary> = {}): MediaListEntrySummary => ({
  entryId: 1,
  state: MediaListEntryState.Completed,
  startedOn: null,
  finishedOn: null,
  charactersRead: null,
  completedCount: 1,
  entryCount: 1,
  ...overrides,
});

describe('mediaWords', () => {
  it('follows the media type', () => {
    expect(mediaWords(MediaType.Anime).again).toBe('Watch again');
    expect(mediaWords(MediaType.VideoGame).historyTitle).toBe('Play history');
    expect(mediaWords(MediaType.Novel).past).toBe('Read');
  });
});

describe('entryProgressPercent', () => {
  it('rounds down, so 100% means the whole deck, and shows anything logged as at least 1%', () => {
    const progress = (charactersRead: number) => entryProgressPercent(summary({ state: MediaListEntryState.InProgress, charactersRead }), 1_000);
    expect(progress(996)).toBe(99);
    expect(progress(290)).toBe(29);
    expect(progress(4)).toBe(1);
    expect(progress(1_000)).toBe(100);
  });

  it('caps at 100 and ignores finished or unlogged entries', () => {
    expect(entryProgressPercent(summary({ state: MediaListEntryState.InProgress, charactersRead: 2_000 }), 1_000)).toBe(100);
    expect(entryProgressPercent(summary({ state: MediaListEntryState.Completed, charactersRead: 500 }), 1_000)).toBeNull();
    expect(entryProgressPercent(summary({ state: MediaListEntryState.InProgress }), 1_000)).toBeNull();
    expect(entryProgressPercent(null, 1_000)).toBeNull();
  });
});

describe('canTrackUnits', () => {
  it('only counts volumes or episodes of series watched or read in order', () => {
    expect(canTrackUnits({ parentDeckId: 0, childrenDeckCount: 12, mediaType: MediaType.Novel })).toBe(true);
    expect(canTrackUnits({ parentDeckId: 0, childrenDeckCount: 0, mediaType: MediaType.Novel })).toBe(false);
    expect(canTrackUnits({ parentDeckId: 3, childrenDeckCount: 0, mediaType: MediaType.Anime })).toBe(false);
    expect(canTrackUnits({ parentDeckId: 0, childrenDeckCount: 6, mediaType: MediaType.VisualNovel })).toBe(false);
  });
});

describe('dateToIso', () => {
  it('uses the local calendar day', () => {
    expect(dateToIso(new Date(2024, 0, 5, 23, 59))).toBe('2024-01-05');
  });
});

describe('releaseDateChoice', () => {
  it('offers a known past release date', () => {
    expect(releaseDateChoice('2020-05-01T00:00:00', '2024-01-01')).toBe('2020-05-01');
  });

  it('skips unset and future dates', () => {
    expect(releaseDateChoice('0001-01-01', '2024-01-01')).toBeNull();
    expect(releaseDateChoice('2025-01-01', '2024-01-01')).toBeNull();
    expect(releaseDateChoice(null)).toBeNull();
  });
});

describe('listEntryFacts', () => {
  it('says when the date is unknown', () => {
    expect(listEntryFacts(summary(), MediaType.Novel, 1000)).toEqual(['Finish date unknown']);
    expect(listEntryFacts(summary(), MediaType.Novel, 1000, { showUnknownDate: false })).toEqual([]);
  });

  it('counts rereads and a custom character count', () => {
    const facts = listEntryFacts(summary({ completedCount: 3, charactersRead: 800_000 }), MediaType.VisualNovel, 1_200_000);
    expect(facts.slice(1)).toEqual(['Read 3 times', '800K of 1.2M characters']);
  });

  it('shows the start date of a read in progress', () => {
    const facts = listEntryFacts(summary({ state: MediaListEntryState.InProgress, startedOn: '2024-03-01' }), MediaType.Anime, 0);
    expect(facts).toHaveLength(1);
    expect(facts[0]).toMatch(/^Started /);
  });

  it('falls back to the last finish when no entry is current', () => {
    const facts = listEntryFacts(summary({ entryId: null, state: null, finishedOn: '2024-03-01' }), MediaType.Novel, 1000);
    expect(facts[0]).toMatch(/^Last finished /);
  });

  it('shows progress on watched media as a share', () => {
    const watching = summary({ state: MediaListEntryState.InProgress, charactersRead: 340 });
    expect(listEntryFacts(watching, MediaType.Anime, 1000)).toEqual(['34% watched']);
    expect(listEntryFacts(watching, MediaType.Audio, 1000)).toEqual(['34% listened']);
    expect(listEntryFacts(watching, MediaType.Novel, 1000)).toEqual(['340 of 1,000 characters']);
  });
});

describe('progressFieldLabel', () => {
  it('names progress the way the media is taken in', () => {
    expect(progressFieldLabel(MediaType.Novel)).toBe('Characters read');
    expect(progressFieldLabel(MediaType.Drama)).toBe('Watched so far');
    expect(progressFieldLabel(MediaType.Audio)).toBe('Listened so far');
  });
});

describe('formatCharacters', () => {
  it('keeps small counts exact', () => {
    expect(formatCharacters(9_500)).toBe((9_500).toLocaleString());
    expect(formatCharacters(1_234_567)).toBe('1.2M');
  });
});

describe('entryPace', () => {
  it('divides the characters by the days, both ends included', () => {
    const read = { state: MediaListEntryState.Completed, startedOn: '2024-01-01', finishedOn: '2024-01-10', charactersRead: null };
    expect(entryPace(read, 100_000)).toBe(10_000);
    expect(entryPace({ ...read, charactersRead: 50_000 }, 100_000)).toBe(5_000);
  });

  it('needs both dates and a completed read', () => {
    expect(entryPace({ state: MediaListEntryState.Completed, startedOn: null, finishedOn: '2024-01-10', charactersRead: null }, 1000)).toBeNull();
    expect(entryPace({ state: MediaListEntryState.Dropped, startedOn: '2024-01-01', finishedOn: '2024-01-10', charactersRead: null }, 1000)).toBeNull();
  });
});

describe('finishedDate sort', () => {
  const deck = (deckId: number, lastCompletedOn: string | null, entry: Partial<MediaListEntrySummary> = {}) =>
    ({ deckId, originalTitle: `t${deckId}`, listEntry: summary({ lastCompletedOn, ...entry }) }) as unknown as Deck;

  // A title dropped in 2025 after finishing it in 2023 sorts by the finish, not the drop.
  const decks = [deck(1, '2023-05-01', { state: MediaListEntryState.Dropped, finishedOn: '2025-01-01' }), deck(2, null), deck(3, '2024-02-01')];

  it('sorts by the last completion and keeps unfinished titles last in both directions', () => {
    expect(sortDecks(decks, 'finishedDate', SortOrder.Descending).map((d) => d.deckId)).toEqual([3, 1, 2]);
    expect(sortDecks(decks, 'finishedDate', SortOrder.Ascending).map((d) => d.deckId)).toEqual([1, 3, 2]);
  });
});

describe('entryCharacters', () => {
  it('lets a series follow its volumes until its own count goes past them', () => {
    expect(entryCharacters({ charactersRead: null, volumeCharacters: 500 })).toBe(500);
    expect(entryCharacters({ charactersRead: 300, volumeCharacters: 500 })).toBe(500);
    expect(entryCharacters({ charactersRead: 800, volumeCharacters: 500 })).toBe(800);
    expect(entryCharacters({ charactersRead: 300, volumeCharacters: null })).toBe(300);
  });

  it('drives the series percentage', () => {
    const series = summary({ state: MediaListEntryState.InProgress, charactersRead: 100, volumeCharacters: 400 });
    expect(entryProgressPercent(series, 1000)).toBe(40);
  });
});

describe('unit wording', () => {
  it('never calls audio or game parts entries', () => {
    expect(unitWord(MediaType.Audio)).toBe('Parts');
    expect(unitWord(MediaType.VideoGame)).toBe('Parts');
    expect(unitWord(MediaType.Novel)).toBe('Volumes');
    expect(unitsFact({ unitCount: 12, completedUnits: 4 }, MediaType.Audio)).toBe('4 of 12 parts');
    expect(unitsFact({ unitCount: null, completedUnits: null }, MediaType.Novel)).toBeNull();
  });
});

describe('closingPassStart', () => {
  it('bounds the finish date only when the change closes the current pass', () => {
    const started = { startedOn: '2026-03-01' };
    expect(closingPassStart(summary({ ...started, state: MediaListEntryState.InProgress }), DeckStatus.Ongoing)).toBe('2026-03-01');
    expect(closingPassStart(summary({ ...started, state: MediaListEntryState.Dropped }), DeckStatus.Dropped)).toBe('2026-03-01');
    expect(closingPassStart(summary({ ...started, state: MediaListEntryState.Dropped }), DeckStatus.Planning)).toBeNull();
    expect(closingPassStart(summary(started), DeckStatus.Completed)).toBeNull();
    expect(closingPassStart(summary(started), DeckStatus.Completed, { undoCompletion: true })).toBe('2026-03-01');
  });
});

describe('restartQuestion', () => {
  it('asks to reread a completion and to resume a stopped pass, never to resume a kept completion', () => {
    const stopped = summary({ state: MediaListEntryState.Dropped });
    expect(restartQuestion(DeckStatus.Completed, summary())).toBe('reread');
    expect(restartQuestion(DeckStatus.Dropped, stopped)).toBe('resume');
    expect(restartQuestion(DeckStatus.Planning, stopped)).toBe('resume');
    expect(restartQuestion(DeckStatus.Dropped, summary())).toBeNull();
    expect(restartQuestion(DeckStatus.Planning, summary())).toBeNull();
  });

  it('asks nothing when switching between Ongoing and Paused on the same pass', () => {
    const inProgress = summary({ state: MediaListEntryState.InProgress });
    expect(restartQuestion(DeckStatus.Paused, inProgress)).toBeNull();
    expect(restartQuestion(DeckStatus.Ongoing, inProgress)).toBeNull();
  });
});

describe('dropQuestion', () => {
  it('asks whether a Completed title was finished, when a pass under way stopped, and nothing otherwise', () => {
    expect(dropQuestion(DeckStatus.Completed, summary())).toBe('unfinish');
    expect(dropQuestion(DeckStatus.Planning, summary())).toBeNull();
    expect(dropQuestion(DeckStatus.Ongoing, summary({ state: MediaListEntryState.InProgress }))).toBe('stopped');
    expect(dropQuestion(DeckStatus.Planning, summary({ state: MediaListEntryState.Dropped }))).toBeNull();
    expect(dropQuestion(DeckStatus.None, null)).toBeNull();
    expect(dropQuestion(DeckStatus.Paused, summary({ state: MediaListEntryState.InProgress }))).toBe('stopped');
  });
});
