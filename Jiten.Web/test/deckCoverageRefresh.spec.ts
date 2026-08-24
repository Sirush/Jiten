import { computed, ref, watch } from 'vue';
import { createPinia, setActivePinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Deck } from '../app/types';
import { isRateLimited } from '../app/utils/isRateLimited';
import { useDeckCoverageRefresh } from '../app/composables/useDeckCoverageRefresh';
import { useJitenStore } from '../app/stores/jitenStore';

// The composable and the store rely on Nuxt auto-imports; provide the few they touch in node.
vi.stubGlobal('ref', ref);
vi.stubGlobal('computed', computed);
vi.stubGlobal('watch', watch);
vi.stubGlobal('onMounted', () => {});
vi.stubGlobal('useCookie', () => ref(undefined));
vi.stubGlobal('isRateLimited', isRateLimited);

let apiImpl: (path: string, opts?: unknown) => Promise<unknown>;
const $api = vi.fn((path: string, opts?: unknown) => apiImpl(path, opts));
vi.stubGlobal('useNuxtApp', () => ({ $api }));

const deck = (overrides: Partial<Deck> = {}): Deck =>
  ({
    deckId: 10,
    parentDeckId: 0,
    coverage: 10,
    uniqueCoverage: 11,
    youngCoverage: 12,
    youngUniqueCoverage: 13,
    ...overrides,
  }) as Deck;

describe('useDeckCoverageRefresh', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    $api.mockClear();
    apiImpl = () => Promise.resolve({ status: 'refreshed' });
  });

  it('applies the fresh numbers and bumps the per-deck versions on success', async () => {
    apiImpl = () => Promise.resolve({ status: 'refreshed', coverage: 42.5, uniqueCoverage: 40, youngCoverage: 5, youngUniqueCoverage: 4 });
    const updates: Deck[] = [];
    const store = useJitenStore();
    const { refresh } = useDeckCoverageRefresh(
      () => deck({ deckId: 10, parentDeckId: 3 }),
      (d) => updates.push(d)
    );

    const result = await refresh();

    expect(result).toBe('refreshed');
    expect($api).toHaveBeenCalledWith('media-deck/10/coverage/refresh', { method: 'POST' });
    expect(updates).toHaveLength(1);
    expect(updates[0]!.coverage).toBe(42.5);
    expect(updates[0]!.youngUniqueCoverage).toBe(4);
    expect(store.deckCoverageVersions[10]).toBe(1);
    expect(store.deckCoverageVersions[3]).toBe(1);
  });

  it('bumps only the deck itself when it has no parent', async () => {
    const store = useJitenStore();
    const { refresh } = useDeckCoverageRefresh(
      () => deck({ deckId: 10, parentDeckId: 0 }),
      () => {}
    );

    await refresh();

    expect(store.deckCoverageVersions[10]).toBe(1);
    expect(Object.keys(store.deckCoverageVersions)).toHaveLength(1);
  });

  it('blocks re-entry while a refresh is in flight', async () => {
    let resolveApi!: (value: unknown) => void;
    apiImpl = () => new Promise((resolve) => (resolveApi = resolve));
    const { isRefreshing, refresh } = useDeckCoverageRefresh(
      () => deck(),
      () => {}
    );

    const first = refresh();
    expect(isRefreshing.value).toBe(true);
    const second = await refresh();

    expect(second).toBeNull();
    expect($api).toHaveBeenCalledTimes(1);

    resolveApi({ status: 'refreshed' });
    await first;
    expect(isRefreshing.value).toBe(false);
  });

  it.each(['not_eligible', 'no_baseline'] as const)('passes %s through without touching the deck', async (status) => {
    apiImpl = () => Promise.resolve({ status });
    const updates: Deck[] = [];
    const store = useJitenStore();
    const { refresh } = useDeckCoverageRefresh(
      () => deck(),
      (d) => updates.push(d)
    );

    const result = await refresh();

    expect(result).toBe(status);
    expect(updates).toHaveLength(0);
    expect(Object.keys(store.deckCoverageVersions)).toHaveLength(0);
  });

  it('reports a rate-limited call distinctly from other errors', async () => {
    apiImpl = () => Promise.reject({ statusCode: 429 });
    const { refresh, isRefreshing } = useDeckCoverageRefresh(
      () => deck(),
      () => {}
    );

    expect(await refresh()).toBe('rate_limited');
    expect(isRefreshing.value).toBe(false);

    apiImpl = () => Promise.reject(new Error('boom'));
    expect(await refresh()).toBe('error');
    expect(isRefreshing.value).toBe(false);
  });
});
