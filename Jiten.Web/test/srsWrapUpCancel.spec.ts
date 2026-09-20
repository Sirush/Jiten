import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('trackEvent', () => {});

function card(wordId: number, goodSeconds = 86400): StudyCardDto {
  return {
    cardId: 0,
    wordId,
    readingIndex: 0,
    state: 0,
    isNewCard: true,
    lapses: 0,
    isLeech: false,
    wordText: `word${wordId}`,
    wordTextPlain: `word${wordId}`,
    readings: [],
    definitions: [],
    partsOfSpeech: [],
    frequencyRank: 0,
    intervalPreview: { againSeconds: 60, hardSeconds: goodSeconds, goodSeconds, easySeconds: 86400 * 4 },
  } as unknown as StudyCardDto;
}

let batchCards: StudyCardDto[] = [];
let reviewFails = false;
const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    return Promise.resolve({
      sessionId: 'session-1',
      cards: batchCards,
      newCardsRemaining: 0,
      reviewsRemaining: 0,
      newCardsToday: 0,
      reviewsToday: 0,
    });
  }
  if (path === 'srs/review') {
    if (reviewFails) return Promise.reject(Object.assign(new Error('boom'), { status: 500 }));
    return Promise.resolve({ newState: 1, intervalPreview: { againSeconds: 60, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 } });
  }
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));
const ids = (store: ReturnType<typeof useSrsStore>) => store.currentBatch.map((c) => c.wordId);

async function start(cards: StudyCardDto[]) {
  const store = useSrsStore();
  store.studySettings.pauseBetweenBatches = false;
  store.studySettings.learnAheadMinutes = 20;
  batchCards = cards;
  await store.fetchBatch();
  return store;
}

describe('cancelling wrap-up after grading', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    reviewFails = false;
  });

  it('keeps the cursor on the first ungraded card and brings the set-aside cards back', async () => {
    const store = await start([1, 2, 3, 4, 5].map((w) => card(w)));
    store.revealCard();
    store.gradeCard(3); // 1 graded, now on 2
    await flush();

    store.wrapUp();
    expect(ids(store)).toEqual([1, 2]);

    store.revealCard();
    store.gradeCard(3); // 2 graded during the wrap-up
    await flush();
    expect(store.isSessionComplete).toBe(true);

    store.cancelWrapUp();

    expect(store.isSessionComplete).toBe(false);
    expect(ids(store)).toEqual([1, 2, 3, 4, 5]);
    expect(store.currentCard?.wordId).toBe(3);
    expect(store.sessionStats.cardsReviewed).toBe(2);
  });

  it('keeps a pending Again repeat from the wrap-up ahead of the returned cards', async () => {
    const store = await start([1, 2, 3, 4].map((w) => card(w)));
    store.revealCard();
    store.gradeCard(3);
    await flush();
    store.wrapUp();

    store.revealCard();
    store.gradeCard(1); // Again moves 2 back into the queue as a pending repeat
    await flush();
    expect(ids(store)).toEqual([1, 2]);
    expect(store.currentCard?.wordId).toBe(2);
    expect(store.againCardKeys.has('2-0')).toBe(true);

    store.cancelWrapUp();

    expect(ids(store)).toEqual([1, 2, 3, 4]);
    expect(store.currentCard?.wordId).toBe(2);
    expect(store.againCardKeys.has('2-0')).toBe(true);
  });
});

describe('failed review with a learning-step copy', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    reviewFails = true;
  });

  it('removes the optimistic copy before re-queuing the original once', async () => {
    const store = await start([card(1, 600), ...[2, 3, 4, 5, 6, 7, 8].map((w) => card(w))]);
    store.revealCard();
    store.gradeCard(3);
    await flush();
    expect(ids(store).filter((w) => w === 1)).toHaveLength(2);

    // submitReview retries once after the first failure; both rejections resolve on the microtask queue.
    await flush();
    await flush();

    expect(ids(store).filter((w) => w === 1)).toHaveLength(2);
    expect(store.currentBatch.slice(store.currentCardIndex).filter((c) => c.wordId === 1)).toHaveLength(1);
    expect(store.learningCardKeys.has('1-0')).toBe(false);
    expect(store.sessionStats.cardsReviewed).toBe(0);
  });
});
