import type { DeckSentenceStats } from '~/types';
import { useAuthStore } from '~/stores/authStore';
import { useJitenStore } from '~/stores/jitenStore';

/**
 * Loads a deck's sentence comprehension stats for the signed-in user. Fetches only once the Jiten+ status has resolved
 * and grants the feature, so a free user never issues a request the API would refuse.
 */
export function useSentenceStats(deckId: MaybeRefOrGetter<number | string>, summaryOnly: boolean) {
  const auth = useAuthStore();
  const jitenStore = useJitenStore();
  const { hasFeature, fetched } = useJitenPlus();

  // Shared across components and route changes; keyed by coverage version so a refresh or review session reloads it.
  const cache = useState<Record<string, DeckSentenceStats>>('sentence-stats-cache', () => ({}));

  const stats = ref<DeckSentenceStats | null>(null);
  const loading = ref(false);
  const failed = ref(false);
  const rateLimited = ref(false);

  const granted = computed(() => auth.isAuthenticated && fetched.value && hasFeature('sentence-stats'));
  const knownVersion = computed(() => `${jitenStore.coverageVersion}.${jitenStore.deckCoverageVersions[Number(toValue(deckId))] ?? 0}`);
  const cacheKey = computed(() => `${toValue(deckId)}:${summaryOnly ? 's' : 'f'}:${knownVersion.value}`);

  async function load(force = false) {
    const id = toValue(deckId);
    if (!granted.value || !id) return;

    const key = cacheKey.value;
    if (!force && cache.value[key]) {
      stats.value = cache.value[key];
      failed.value = false;
      rateLimited.value = false;
      return;
    }

    loading.value = true;
    failed.value = false;
    rateLimited.value = false;
    try {
      const { $api } = useNuxtApp();
      const result = await $api<DeckSentenceStats>(`media-deck/${id}/${summaryOnly ? 'sentence-summary' : 'sentence-stats'}`, {
        query: { v: knownVersion.value },
      });
      stats.value = result;
      if (result.hasData) cache.value = { ...cache.value, [key]: result };
    } catch (err) {
      stats.value = null;
      failed.value = true;
      rateLimited.value = isRateLimited(err);
    } finally {
      loading.value = false;
    }
  }

  if (import.meta.client) {
    watch([granted, cacheKey], () => load(), { immediate: true });
  }

  return { stats, loading, failed, rateLimited, granted, statusReady: fetched, knownVersion, retry: () => load(true) };
}
