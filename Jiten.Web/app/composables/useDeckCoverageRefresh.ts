import type { Deck } from '~/types';
import { useJitenStore } from '~/stores/jitenStore';

interface DeckCoverageRefreshResponse {
  status: 'refreshed' | 'not_eligible' | 'no_baseline';
  coverage?: number;
  uniqueCoverage?: number;
  youngCoverage?: number;
  youngUniqueCoverage?: number;
}

export type DeckCoverageRefreshResult = 'refreshed' | 'not_eligible' | 'no_baseline' | 'rate_limited' | 'error';

/**
 * Synchronously recomputes the viewer's coverage for one media (root + all subdecks) and applies the
 * fresh numbers to the deck via onUpdate. The caller maps the returned status to toasts/dialogs.
 */
export function useDeckCoverageRefresh(deck: () => Deck, onUpdate: (updated: Deck) => void) {
  const { $api } = useNuxtApp();
  const store = useJitenStore();

  const isRefreshing = ref(false);

  const refresh = async (): Promise<DeckCoverageRefreshResult | null> => {
    if (isRefreshing.value) return null;
    isRefreshing.value = true;
    try {
      const d = deck();
      const response = await $api<DeckCoverageRefreshResponse>(`media-deck/${d.deckId}/coverage/refresh`, { method: 'POST' });

      if (response.status !== 'refreshed') return response.status;

      onUpdate({
        ...d,
        coverage: response.coverage ?? d.coverage,
        uniqueCoverage: response.uniqueCoverage ?? d.uniqueCoverage,
        youngCoverage: response.youngCoverage ?? d.youngCoverage,
        youngUniqueCoverage: response.youngUniqueCoverage ?? d.youngUniqueCoverage,
      });
      // The whole media was recomputed, so both the deck itself and its root go stale.
      store.bumpDeckCoverageVersion(d.deckId);
      if (d.parentDeckId) store.bumpDeckCoverageVersion(d.parentDeckId);
      return 'refreshed';
    } catch (error) {
      if (isRateLimited(error)) return 'rate_limited';
      console.error('Failed to refresh deck coverage:', error);
      return 'error';
    } finally {
      isRefreshing.value = false;
    }
  };

  return { isRefreshing, refresh };
}
