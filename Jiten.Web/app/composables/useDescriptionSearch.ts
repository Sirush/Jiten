import { debounce } from 'perfect-debounce';
import type { DescriptionSearchResponse } from '~/types/types';

/**
 * On-demand description search for typeahead surfaces. Debounced, and a stale reply never
 * overwrites a newer one, since the network can answer out of order.
 */
export function useDescriptionSearch(limit = 3) {
  const { $api } = useNuxtApp();

  const response = ref<DescriptionSearchResponse | null>(null);
  const isLoading = ref(false);
  let latest = 0;

  const searchInternal = async (query: string, mediaType?: number | null) => {
    const request = ++latest;
    if (!query || query.trim().length < 2) {
      response.value = null;
      isLoading.value = false;
      return;
    }

    isLoading.value = true;
    try {
      const result = await $api<DescriptionSearchResponse>('media-deck/search-by-description', {
        query: { query: query.trim(), limit, mediaType: mediaType || undefined },
      });
      if (request !== latest) return;
      response.value = result;
    } catch {
      if (request !== latest) return;
      response.value = null;
    } finally {
      if (request === latest) isLoading.value = false;
    }
  };

  const search = debounce(searchInternal, 350);

  const clear = () => {
    latest++;
    response.value = null;
    isLoading.value = false;
  };

  return { response, isLoading, search, clear };
}
