import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});

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
    intervalPreview: { againSeconds: 60, hardSeconds: 600, goodSeconds: 86400, easySeconds: 86400 * 4 },
  } as unknown as StudyCardDto;
}

let batchCalls = 0;
const $api = vi.fn((path: string) => {
  if (path.startsWith('srs/study-batch')) {
    batchCalls++;
    return Promise.resolve({
      sessionId: `session-${batchCalls}`,
      cards: [card(1), card(2), card(3)],
      newCardsRemaining: 0,
      reviewsRemaining: 0,
      newCardsToday: 0,
      reviewsToday: 0,
    });
  }
  if (path === 'srs/review') return Promise.resolve({ newState: 1, intervalPreview: { againSeconds: 60, hardSeconds: 600, goodSeconds: 86400, easySeconds: 86400 * 4 } });
  if (path === 'srs/card-examples') return Promise.resolve({ examples: {} });
  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('session resume after in-app navigation', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    batchCalls = 0;
  });

  // Grade, leave within the save debounce, come back: the in-memory store already knows the grade.
  it('keeps the live in-memory session instead of an older persisted blob', async () => {
    const store = useSrsStore();
    store.studySettings.pauseBetweenBatches = false;
    await store.fetchBatch();
    store.revealCard();
    store.gradeCard(3);
    await flush();
    const indexAfterGrade = store.currentCardIndex;
    const sessionAfterGrade = store.sessionId;

    const restored = await store.tryRestoreSession();

    expect(restored).toBe(true);
    expect(store.currentCardIndex).toBe(indexAfterGrade);
    expect(store.sessionId).toBe(sessionAfterGrade);
    expect(store.sessionStats.cardsReviewed).toBe(1);
    expect(store.isFlipped).toBe(false);
    expect(batchCalls).toBe(1);
  });

  it('does not resume when the session was invalidated while away', async () => {
    const store = useSrsStore();
    store.studySettings.pauseBetweenBatches = false;
    await store.fetchBatch();
    store.invalidateSession();

    const restored = await store.tryRestoreSession();

    expect(restored).toBe(false);
    expect(store.sessionDirty).toBe(true);
    await store.fetchBatch();
    expect(store.sessionDirty).toBe(false);
    expect(batchCalls).toBe(2);
  });

  it('returns false with no session in memory', async () => {
    const store = useSrsStore();
    expect(await store.tryRestoreSession()).toBe(false);
  });
});
