import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});

function card(wordId: number, goodSeconds: number): StudyCardDto {
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
  if (path === 'srs/review') return Promise.resolve({ newState: 1, intervalPreview: { againSeconds: 60, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 } });
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('learn-ahead re-queue', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  async function start(goodSeconds: number, learnAheadMinutes: number) {
    const store = useSrsStore();
    store.studySettings.pauseBetweenBatches = false;
    store.studySettings.learnAheadMinutes = learnAheadMinutes;
    batchCards = [card(1, goodSeconds), ...Array.from({ length: 20 }, (_, i) => card(10 + i, 86400))];
    await store.fetchBatch();
    store.isFlipped = true;
    return store;
  }

  it('re-queues a step-0 Good card whose step ends inside the window', async () => {
    const store = await start(600, 20);
    store.gradeCard(3);
    await flush();

    const copies = store.currentBatch.filter((c) => c.wordId === 1);
    expect(copies).toHaveLength(2);
    expect(store.learningCardKeys.has('1-0')).toBe(true);
    expect(store.learningCardsAhead).toBe(1);
    const copy = store.currentBatch.slice(store.currentCardIndex).find((c) => c.wordId === 1)!;
    expect(copy.isNewCard).toBe(false);
    expect(copy.state).toBe(1);
    expect(copy.intervalPreview?.goodSeconds).toBe(86400);
    expect(store.sessionStats.newCardsLearned).toBe(1);
  });

  it('does not re-queue a step that ends outside the window', async () => {
    const store = await start(600, 5);
    store.gradeCard(3);
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(1);
    expect(store.learningCardKeys.size).toBe(0);
  });

  it('clears the learning chip and does not count a new card twice when the copy is graded', async () => {
    const store = await start(600, 20);
    store.gradeCard(3);
    await flush();

    // Jump to the copy and grade it.
    const idx = store.currentBatch.findIndex((c, i) => i >= store.currentCardIndex && c.wordId === 1);
    store.currentCardIndex = idx;
    store.isFlipped = true;
    store.gradeCard(3);
    await flush();

    expect(store.learningCardKeys.has('1-0')).toBe(false);
    expect(store.sessionStats.newCardsLearned).toBe(1);
    expect(store.sessionStats.cardsReviewed).toBe(2);
  });

  it('wrap-up keeps a pending learning repeat', async () => {
    const store = await start(600, 20);
    store.gradeCard(3);
    await flush();
    store.wrapUp();

    expect(store.currentBatch.slice(store.currentCardIndex).some((c) => c.wordId === 1)).toBe(true);
    expect(store.currentBatch.slice(store.currentCardIndex).filter((c) => c.wordId !== 1)).toHaveLength(1);
  });
});
