import { ref, toValue, watch } from 'vue';
import { describe, expect, it, vi } from 'vitest';

vi.stubGlobal('ref', ref);
vi.stubGlobal('watch', watch);
vi.stubGlobal('toValue', toValue);

const studySettings = { exampleSentenceSorting: 'HardestFirst' };
const bodies: { minDifficulty: number; maxDifficulty: number; descending: boolean }[] = [];

const $api = vi.fn((_path: string, opts: { body: { minDifficulty: number; maxDifficulty: number; descending: boolean } }) => {
  bodies.push(opts.body);
  return Promise.resolve({
    sentences: [{ sourceDeck: { deckId: bodies.length } }],
    minDifficulty: 1,
    maxDifficulty: 6,
    searchedBandMin: opts.body.minDifficulty,
    searchedBandMax: opts.body.maxDifficulty,
  });
});

vi.stubGlobal('useNuxtApp', () => ({ $api }));
vi.stubGlobal('useSrsStore', () => ({ studySettings }));
vi.stubGlobal('useAuthStore', () => ({ isAuthenticated: true }));

const { useExtraExampleSentences } = await import('../app/composables/useExtraExampleSentences');

describe('useExtraExampleSentences', () => {
  it('starts a HardestFirst walk from the top band on the first card', async () => {
    const { loadMore, canLoadMore } = useExtraExampleSentences(ref({ wordId: 1, readingIndex: 0 }));

    await loadMore();

    expect(bodies[0]).toMatchObject({ minDifficulty: 999, maxDifficulty: 999.5, descending: true });
    expect(canLoadMore.value).toBe(true);
  });
});
