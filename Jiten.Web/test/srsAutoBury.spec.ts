import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('trackEvent', () => {});

function card(wordId: number, againSeconds = 600): StudyCardDto {
  return {
    cardId: 0,
    wordId,
    readingIndex: 0,
    state: 2,
    isNewCard: false,
    lapses: 0,
    isLeech: false,
    wordText: `word${wordId}`,
    wordTextPlain: `word${wordId}`,
    readings: [],
    definitions: [],
    partsOfSpeech: [],
    frequencyRank: 0,
    intervalPreview: { againSeconds, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 },
  } as unknown as StudyCardDto;
}

const preview = { againSeconds: 600, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 };
let batchCards: StudyCardDto[] = [];
let reviewResponse: () => Promise<unknown> = () => Promise.resolve({ newState: 3, intervalPreview: preview });
const buried = () => Promise.resolve({ newState: 3, autoBuried: true, intervalPreview: preview });

const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    const cards = batchCards;
    batchCards = [];
    return Promise.resolve({ sessionId: 'session-1', cards, newCardsRemaining: 0, reviewsRemaining: 0, newCardsToday: 0, reviewsToday: 0 });
  }
  if (path === 'srs/review') return reviewResponse();
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

async function start(extra: number, learnAheadMinutes = 20) {
  const store = useSrsStore();
  store.studySettings.pauseBetweenBatches = false;
  store.studySettings.learnAheadMinutes = learnAheadMinutes;
  batchCards = [card(1), ...Array.from({ length: extra }, (_, i) => card(10 + i))];
  await store.fetchBatch();
  store.isFlipped = true;
  return store;
}

describe('auto-bury after repeated Again ratings', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    reviewResponse = () => Promise.resolve({ newState: 3, intervalPreview: preview });
  });

  it('removes the re-queued copy and records the card when the server buries it', async () => {
    reviewResponse = buried;
    const store = await start(3);
    store.gradeCard(1);
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(0);
    expect(store.againCardKeys.has('1-0')).toBe(false);
    expect(store.sessionBuried.map((b) => b.wordId)).toEqual([1]);
    expect(store.lastAutoBuryEvent?.wordText).toBe('word1');
    expect(store.sessionStats.cardsReviewed).toBe(1);
    expect(store.sessionStats.gradeCounts.again).toBe(1);
  });

  it('ends the batch when the buried card was the last one', async () => {
    reviewResponse = buried;
    const store = await start(0);
    store.gradeCard(1);
    await flush();
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(0);
    expect(store.isSessionComplete).toBe(true);
  });

  it('records a buried card that was never re-queued because its step fell outside learn-ahead', async () => {
    reviewResponse = buried;
    const store = await start(1, 0);
    store.gradeCard(1);
    await flush();

    expect(store.currentBatch.slice(store.currentCardIndex).some((c) => c.wordId === 1)).toBe(false);
    expect(store.sessionBuried.map((b) => b.wordId)).toEqual([1]);
    expect(store.currentCardIndex).toBe(1);
  });

  it('does not touch the queue when the server does not bury', async () => {
    const store = await start(3);
    store.gradeCard(1);
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(1);
    expect(store.sessionBuried).toHaveLength(0);
    expect(store.lastAutoBuryEvent).toBeNull();
  });

  it('undo puts the card back where it was and forgets the bury', async () => {
    reviewResponse = buried;
    const store = await start(3);
    store.gradeCard(1);
    await flush();
    expect(store.currentCard?.wordId).toBe(10);

    const ok = await store.undoLastAction();

    expect(ok).toBe(true);
    expect($api).toHaveBeenCalledWith('srs/undo-review', expect.objectContaining({ body: { wordId: 1, readingIndex: 0 } }));
    expect(store.currentCard?.wordId).toBe(1);
    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(1);
    expect(store.sessionBuried).toHaveLength(0);
    expect(store.againCardKeys.has('1-0')).toBe(false);
    expect(store.sessionStats.cardsReviewed).toBe(0);
  });

  it('keeps undo for the buried grade but drops grades made while the copy was still queued', async () => {
    let resolveReview!: (value: unknown) => void;
    reviewResponse = () => new Promise((resolve) => (resolveReview = resolve));
    const store = await start(3);
    store.gradeCard(1);
    await flush();

    // Grade the next card while the bury response is still in flight; its snapshot holds the copy.
    reviewResponse = () => Promise.resolve({ newState: 3, intervalPreview: preview });
    store.isFlipped = true;
    store.gradeCard(3);
    await flush();

    resolveReview({ newState: 3, autoBuried: true, intervalPreview: preview });
    await flush();

    expect(store.canUndo).toBe(true);
    await store.undoLastAction();
    expect(store.currentCard?.wordId).toBe(1);
    expect(store.sessionStats.cardsReviewed).toBe(0);
    expect(store.canUndo).toBe(false);
  });
});
