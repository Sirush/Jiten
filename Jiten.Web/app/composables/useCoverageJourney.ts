import type { CoverageJourney } from '~/types';
import { useAuthStore } from '~/stores/authStore';
import { useJitenStore } from '~/stores/jitenStore';

/**
 * Loads a deck's coverage journey for the signed-in user. Fetches only once the Jiten+ status has
 * resolved and grants the feature, so a free user never issues a request the API would refuse.
 */
export function useCoverageJourney(deckId: MaybeRefOrGetter<number | string>) {
  const auth = useAuthStore();
  const jitenStore = useJitenStore();
  const { hasFeature, fetched } = useJitenPlus();

  // Shared across components and route changes: detail -> stats -> back is one request, not three.
  // Keyed by deck and coverage version so a refresh still invalidates it.
  const cache = useState<Record<string, CoverageJourney>>('journey-cache', () => ({}));

  const journey = ref<CoverageJourney | null>(null);
  const loading = ref(false);
  const failed = ref(false);
  const rateLimited = ref(false);

  const granted = computed(() => auth.isAuthenticated && fetched.value && hasFeature('coverage-journey'));
  const versionFor = (id: number | string) => `${jitenStore.coverageVersion}:${jitenStore.deckCoverageVersions[Number(id)] ?? 0}`;
  const cacheKey = computed(() => `${toValue(deckId)}:${versionFor(toValue(deckId))}`);

  async function load(force = false) {
    const id = toValue(deckId);
    if (!granted.value || !id) return;

    const key = cacheKey.value;
    if (!force && cache.value[key]) {
      journey.value = cache.value[key];
      failed.value = false;
      rateLimited.value = false;
      return;
    }

    loading.value = true;
    failed.value = false;
    rateLimited.value = false;
    try {
      const { $api } = useNuxtApp();
      const result = await $api<CoverageJourney>(`media-deck/${id}/coverage-journey`);
      journey.value = result;
      // A coverage refresh strands the previous generation; dropping stranded keys keeps a long
      // browsing session from retaining a series per deck per refresh.
      const isCurrent = (k: string) => k === `${k.split(':')[0]}:${versionFor(k.split(':')[0]!)}`;
      if (Object.keys(cache.value).some((k) => !isCurrent(k))) cache.value = Object.fromEntries(Object.entries(cache.value).filter(([k]) => isCurrent(k)));
      cache.value[key] = result;
    } catch (err) {
      journey.value = null;
      failed.value = true;
      rateLimited.value = isRateLimited(err);
    } finally {
      loading.value = false;
    }
  }

  if (import.meta.client) {
    // cacheKey folds in both the deck and the coverage version, so a coverage refresh reloads
    // the series even though no review happened.
    watch([granted, cacheKey], () => load(), { immediate: true });
  }

  return {
    journey,
    loading,
    failed,
    rateLimited,
    granted,
    statusReady: fetched,
    retry: () => load(true),
  };
}
