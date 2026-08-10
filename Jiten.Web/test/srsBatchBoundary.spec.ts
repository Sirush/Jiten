import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

// The store relies on Nuxt auto-imports; provide the few it actually touches outside client guards.
vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);

const apiCalls: string[] = [];
let pendingReview: (() => void) | null = null;

function card(wordId: number): StudyCardDto {
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
  } as unknown as StudyCardDto;
}

const $api = vi.fn((path: string) => {
  apiCalls.push(path);

  if (path.startsWith('srs/study-batch')) {
    const first = apiCalls.filter(p => p.startsWith('srs/study-batch')).length === 1;
    return Promise.resolve({
      sessionId: 'session-1',
      cards: first ? [card(1), card(2)] : [],
      newCardsRemaining: 0,
      reviewsRemaining: 0,
      newCardsToday: first ? 0 : 2,
      reviewsToday: 0,
    });
  }

  if (path === 'srs/review') {
    return new Promise(resolve => {
      pendingReview = () => resolve({});
    });
  }

  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });

  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise(resolve => setTimeout(resolve, 0));
const batchCalls = () => apiCalls.filter(p => p.startsWith('srs/study-batch')).length;

describe('batch boundary', () => {
  beforeEach(() => {
    apiCalls.length = 0;
    pendingReview = null;
    setActivePinia(createPinia());
  });

  // study-batch derives the daily budgets and the already-seen set from committed rows, so fetching
  // it while the last grade is still in flight re-serves that card and overshoots the daily limits.
  it('does not request the next batch while a review is still in flight', async () => {
    const store = useSrsStore();
    store.studySettings.batchSize = 2;
    store.studySettings.pauseBetweenBatches = false;

    await store.fetchBatch();
    expect(store.currentBatch).toHaveLength(2);
    expect(batchCalls()).toBe(1);

    store.revealCard();
    store.gradeCard(3);
    pendingReview!();
    await flush();

    // Mid-batch: no fetch is due yet.
    expect(batchCalls()).toBe(1);

    store.revealCard();
    store.gradeCard(3);
    await flush();

    // Last card of the batch — its review has not landed, so the fetch must still be held.
    expect(store.currentCardIndex).toBe(2);
    expect(batchCalls()).toBe(1);

    pendingReview!();
    await flush();

    expect(batchCalls()).toBe(2);
    expect(apiCalls.lastIndexOf('srs/review')).toBeLessThan(
      apiCalls.findIndex((p, i) => p.startsWith('srs/study-batch') && i > 0)
    );
  });
});
