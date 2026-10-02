import { ref, toValue, watch } from 'vue';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.stubGlobal('ref', ref);
vi.stubGlobal('watch', watch);
vi.stubGlobal('toValue', toValue);

type Body = { minDifficulty: number; maxDifficulty: number; descending: boolean; take: number; excludedDeckIds: number[] };

const studySettings = { exampleSentenceSorting: 'HardestFirst' };
const bodies: Body[] = [];
let available = 0;
let nextDeckId = 1;

const $api = vi.fn((_path: string, opts: { body: Body }) => {
  bodies.push(opts.body);
  const count = Math.min(opts.body.take, available);
  available -= count;
  return Promise.resolve({
    sentences: Array.from({ length: count }, () => ({ sourceDeck: { deckId: nextDeckId++ } })),
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
  beforeEach(() => {
    bodies.length = 0;
    nextDeckId = 1;
  });

  it('starts a HardestFirst walk from the top band on the first card', async () => {
    available = 10;
    const { loadMore, canLoadMore } = useExtraExampleSentences(ref({ wordId: 1, readingIndex: 0 }));

    await loadMore();

    expect(bodies[0]).toMatchObject({ minDifficulty: 999, maxDifficulty: 999.5, descending: true });
    expect(canLoadMore.value).toBe(true);
  });

  it('hides load more when the first page already holds every sentence', async () => {
    available = 2;
    const { loadMore, sentences, canLoadMore } = useExtraExampleSentences(ref({ wordId: 1, readingIndex: 0 }));

    await loadMore();

    expect(sentences.value).toHaveLength(2);
    expect(canLoadMore.value).toBe(false);
  });

  it('serves the held sentence on the next page and excludes its deck from the fetch', async () => {
    available = 4;
    const { loadMore, sentences, canLoadMore } = useExtraExampleSentences(ref({ wordId: 1, readingIndex: 0 }));

    await loadMore();
    expect(sentences.value).toHaveLength(3);
    expect(canLoadMore.value).toBe(true);

    await loadMore();
    expect(bodies[1]).toMatchObject({ take: 3, excludedDeckIds: [1, 2, 3, 4] });
    expect(sentences.value.map((s) => s.sourceDeck.deckId)).toEqual([1, 2, 3, 4]);
    expect(canLoadMore.value).toBe(false);
  });

  it('expands with an empty list when the word has no other sentences', async () => {
    available = 0;
    const { toggle, sentences, expanded, canLoadMore } = useExtraExampleSentences(ref({ wordId: 1, readingIndex: 0 }));

    toggle();
    await vi.waitFor(() => expect(expanded.value).toBe(true));

    expect(sentences.value).toHaveLength(0);
    expect(canLoadMore.value).toBe(false);

    toggle();
    expect(expanded.value).toBe(false);
    expect(bodies).toHaveLength(1);
  });
});
