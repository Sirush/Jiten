import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { StudyCardDto } from '../app/types';

// The store relies on Nuxt auto-imports; provide the few it actually touches outside client guards.
vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('trackActivation', () => {});

const apiCalls: string[] = [];
let sentenceCounter = 0;

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

const $api = vi.fn((path: string, opts?: { body?: { pairs?: { wordId: number; readingIndex: number }[] } }) => {
  apiCalls.push(path);

  if (path.startsWith('srs/study-batch')) {
    const first = apiCalls.filter(p => p.startsWith('srs/study-batch')).length === 1;
    return Promise.resolve({
      sessionId: 'session-1',
      cards: first ? [card(1), card(2)] : [],
      newCardsRemaining: 0,
      reviewsRemaining: 0,
      newCardsToday: 0,
      reviewsToday: 0,
    });
  }

  if (path === 'srs/card-examples') {
    const examples: Record<string, unknown> = {};
    for (const pair of opts?.body?.pairs ?? []) {
      sentenceCounter++;
      examples[`${pair.wordId}-${pair.readingIndex}`] = {
        sentenceId: sentenceCounter,
        text: `sentence ${sentenceCounter}`,
        wordPosition: 0,
        wordLength: 1,
      };
    }
    return Promise.resolve({ examples });
  }

  return Promise.resolve({});
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));

const { useSrsStore } = await import('../app/stores/srsStore');

const flush = () => new Promise(resolve => setTimeout(resolve, 0));
const exampleCalls = () => apiCalls.filter(p => p === 'srs/card-examples').length;

describe('example reroll cache', () => {
  beforeEach(() => {
    apiCalls.length = 0;
    sentenceCounter = 0;
    setActivePinia(createPinia());
  });

  it('refetches the example when a card is requeued in Random mode', async () => {
    const store = useSrsStore();
    store.studySettings.batchSize = 2;
    store.studySettings.pauseBetweenBatches = false;
    store.studySettings.exampleSentenceSource = 'Random';

    await store.fetchBatch();
    await flush();
    const before = store.getCardExample(1, 0)?.sentenceId;
    expect(before).toBeDefined();
    expect(exampleCalls()).toBe(1);

    store.revealCard();
    store.gradeCard(1);
    await flush();

    expect(exampleCalls()).toBe(2);
    expect(store.getCardExample(1, 0)?.sentenceId).not.toBe(before);
  });

  it('keeps the cached example when a card is requeued in StudyDecks mode', async () => {
    const store = useSrsStore();
    store.studySettings.batchSize = 2;
    store.studySettings.pauseBetweenBatches = false;
    store.studySettings.exampleSentenceSource = 'StudyDecks';

    await store.fetchBatch();
    await flush();
    const before = store.getCardExample(1, 0)?.sentenceId;

    store.revealCard();
    store.gradeCard(1);
    await flush();

    expect(exampleCalls()).toBe(1);
    expect(store.getCardExample(1, 0)?.sentenceId).toBe(before);
  });
});
