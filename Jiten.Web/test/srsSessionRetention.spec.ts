import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';
import { FsrsRating, FsrsState } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('trackEvent', () => {});

const DAY_MS = 24 * 60 * 60 * 1000;

function card(wordId: number, state: FsrsState, lastReviewAgoMs: number | null = state === FsrsState.New ? null : 3 * DAY_MS): StudyCardDto {
  return {
    cardId: 0,
    wordId,
    readingIndex: 0,
    state,
    isNewCard: state === FsrsState.New,
    lastReview: lastReviewAgoMs == null ? null : new Date(Date.now() - lastReviewAgoMs).toISOString(),
    lapses: 0,
    isLeech: false,
    wordText: `word${wordId}`,
    wordTextPlain: `word${wordId}`,
    readings: [],
    definitions: [],
    partsOfSpeech: [],
    frequencyRank: 0,
    intervalPreview: { againSeconds: 0, hardSeconds: 86400, goodSeconds: 86400, easySeconds: 86400 * 4 },
  } as unknown as StudyCardDto;
}

let batchCards: StudyCardDto[] = [];
const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    return Promise.resolve({ sessionId: 'session-1', cards: batchCards, newCardsRemaining: 0, reviewsRemaining: 0, newCardsToday: 0, reviewsToday: 0 });
  }
  if (path === 'srs/review') {
    return Promise.resolve({ newState: 2, intervalPreview: { againSeconds: 0, hardSeconds: 86400, goodSeconds: 86400, easySeconds: 86400 * 4 } });
  }
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

async function start(cards: StudyCardDto[]) {
  const store = useSrsStore();
  store.studySettings.pauseBetweenBatches = false;
  batchCards = cards;
  await store.fetchBatch();
  return store;
}

async function grade(store: ReturnType<typeof useSrsStore>, rating: FsrsRating) {
  store.revealCard();
  store.gradeCard(rating);
  await flush();
}

describe('session retention', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('ignores new cards, even when they are failed', async () => {
    const store = await start([card(1, FsrsState.New), card(2, FsrsState.New), card(3, FsrsState.Review)]);
    await grade(store, FsrsRating.Again);
    await grade(store, FsrsRating.Again);
    await grade(store, FsrsRating.Good);

    expect(store.sessionStats.gradeCounts.again).toBe(2);
    expect(store.sessionStats.retention).toEqual({ total: 1, passed: 1 });
  });

  it('counts only the first answer of a failed review card', async () => {
    const store = await start([card(1, FsrsState.Review)]);
    await grade(store, FsrsRating.Again);
    expect(store.currentCard?.wordId).toBe(1);
    await grade(store, FsrsRating.Good);

    expect(store.sessionStats.gradeCounts).toEqual({ again: 1, hard: 0, good: 1, easy: 0 });
    expect(store.sessionStats.retention).toEqual({ total: 1, passed: 0 });
  });

  it('counts a step carried over from an earlier day, but not a same-day step', async () => {
    const store = await start([card(1, FsrsState.Relearning), card(2, FsrsState.Learning, 10 * 60_000)]);
    await grade(store, FsrsRating.Again);
    await grade(store, FsrsRating.Good);

    expect(store.sessionStats.retention).toEqual({ total: 1, passed: 0 });
  });

  it('ignores the next learning step of an overdue step answered this session', async () => {
    const overdue = card(1, FsrsState.Learning);
    overdue.intervalPreview = { againSeconds: 60, hardSeconds: 300, goodSeconds: 600, easySeconds: 86400 * 4, goodIsStep: true } as StudyCardDto['intervalPreview'];
    const store = await start([overdue]);
    await grade(store, FsrsRating.Good);
    expect(store.currentCard?.wordId).toBe(1);
    await grade(store, FsrsRating.Good);

    expect(store.sessionStats.retention).toEqual({ total: 1, passed: 1 });
  });

  it('counts a review card due less than a day after its last review', async () => {
    const store = await start([card(1, FsrsState.Review, 6 * 60 * 60_000)]);
    await grade(store, FsrsRating.Good);

    expect(store.sessionStats.retention).toEqual({ total: 1, passed: 1 });
  });

  it('undo takes the answer back out', async () => {
    const store = await start([card(1, FsrsState.Review), card(2, FsrsState.Review)]);
    await grade(store, FsrsRating.Good);
    await store.undoLastAction();

    expect(store.sessionStats.retention).toEqual({ total: 0, passed: 0 });
  });
});
