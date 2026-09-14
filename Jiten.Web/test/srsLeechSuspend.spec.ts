import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
    lapses: 7,
    isLeech: true,
    wordText: `word${wordId}`,
    wordTextPlain: `word${wordId}`,
    readings: [],
    definitions: [],
    partsOfSpeech: [],
    frequencyRank: 0,
    intervalPreview: { againSeconds, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 },
  } as unknown as StudyCardDto;
}

let batchCards: StudyCardDto[] = [];
let reviewResponse: () => Promise<unknown> = () => Promise.resolve({ newState: 3, intervalPreview: { againSeconds: 600, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 } });

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

async function start(extra: number) {
  const store = useSrsStore();
  store.studySettings.pauseBetweenBatches = false;
  store.studySettings.learnAheadMinutes = 20;
  batchCards = [card(1), ...Array.from({ length: extra }, (_, i) => card(10 + i))];
  await store.fetchBatch();
  store.isFlipped = true;
  return store;
}

describe('leech auto-suspend during a session', () => {
  beforeEach(() => setActivePinia(createPinia()));
  afterEach(() => vi.useRealTimers());

  it('removes the re-queued copy when the server suspends the card, even after the queue was settled', async () => {
    reviewResponse = () => Promise.resolve({ newState: 4, leechDetected: true, leechSuspended: true, isLeech: true, lapses: 8 });
    const store = await start(1);
    store.gradeCard(1);
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(0);
    expect(store.againCardKeys.has('1-0')).toBe(false);
  });

  it('removes the re-queued copy when the suspended card is the last one in the batch', async () => {
    reviewResponse = () => Promise.resolve({ newState: 4, leechDetected: true, leechSuspended: true, isLeech: true, lapses: 8 });
    const store = await start(0);
    store.gradeCard(1);
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(0);
  });

  it('drops a card the server rejects as suspended instead of re-queuing it forever', async () => {
    reviewResponse = () => Promise.reject({ status: 400, data: 'Card is Suspended and cannot be reviewed. Release it first.' });
    const store = await start(0);
    store.gradeCard(3);
    await flush();
    await flush();

    expect(store.currentBatch.filter((c) => c.wordId === 1)).toHaveLength(0);
    expect(store.sessionStats.cardsReviewed).toBe(0);
  });
});
