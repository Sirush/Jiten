import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});
vi.stubGlobal('trackEvent', () => {});

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
    intervalPreview: { againSeconds: 60, hardSeconds: 86400, goodSeconds: 86400, easySeconds: 86400 * 4 },
  } as unknown as StudyCardDto;
}

let batchCards: StudyCardDto[] = [];
const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    return Promise.resolve({ sessionId: 'session-1', cards: batchCards, newCardsRemaining: 0, reviewsRemaining: 0, newCardsToday: 0, reviewsToday: 0 });
  }
  if (path === 'srs/review') {
    return Promise.resolve({ newState: 1, intervalPreview: { againSeconds: 60, hardSeconds: 900, goodSeconds: 86400, easySeconds: 86400 * 4 } });
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

describe('session time spent', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.useFakeTimers({ toFake: ['Date'] });
  });
  afterEach(() => vi.useRealTimers());

  it('counts front and back time per card, not just thinking time', async () => {
    const store = await start([card(1), card(2)]);
    vi.advanceTimersByTime(5_000);
    store.revealCard();
    vi.advanceTimersByTime(7_000);
    store.gradeCard(3);
    await flush();

    expect($api).toHaveBeenCalledWith('srs/review', expect.objectContaining({ body: expect.objectContaining({ reviewDuration: 5_000 }) }));
    expect(store.sessionStats.activeMs).toBe(12_000);
  });

  it('caps a card left open at two minutes', async () => {
    const store = await start([card(1), card(2)]);
    store.revealCard();
    vi.advanceTimersByTime(60 * 60_000);
    store.gradeCard(3);
    await flush();

    expect(store.sessionStats.activeMs).toBe(120_000);
  });

  it('includes the card currently on screen in the live total', async () => {
    const store = await start([card(1), card(2)]);
    store.revealCard();
    vi.advanceTimersByTime(4_000);
    store.gradeCard(3);
    await flush();
    vi.advanceTimersByTime(3_000);

    expect(store.sessionActiveMs()).toBe(7_000);
  });

  it('undo gives the card time back', async () => {
    const store = await start([card(1), card(2)]);
    store.revealCard();
    vi.advanceTimersByTime(4_000);
    store.gradeCard(3);
    await flush();
    await store.undoLastAction();

    expect(store.sessionStats.activeMs).toBe(0);
  });
});
