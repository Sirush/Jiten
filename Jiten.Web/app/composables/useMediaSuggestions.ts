import { debounce } from 'perfect-debounce';
import type { MediaSuggestion, MediaSuggestionsResponse } from '~/types/types';

export function useMediaSuggestions({ limit = 5 }: { limit?: number } = {}) {
  const { $api } = useNuxtApp();

  const suggestions = ref<MediaSuggestion[]>([]);
  const totalCount = ref(0);
  const isLoading = ref(false);
  const error = ref<Error | null>(null);
  /** Only the latest query's response is applied; an earlier one can land after it. */
  let latestQuery = '';

  const fetchSuggestionsInternal = async (query: string) => {
    if (query !== latestQuery) return;
    if (!query || query.length < 2) {
      suggestions.value = [];
      totalCount.value = 0;
      isLoading.value = false;
      return;
    }

    isLoading.value = true;
    error.value = null;

    try {
      const result = await $api<MediaSuggestionsResponse>('media-deck/search-suggestions', {
        query: { query, limit },
      });
      if (query !== latestQuery) return;
      suggestions.value = result?.suggestions || [];
      totalCount.value = result?.totalCount || 0;
    } catch (e) {
      if (query !== latestQuery) return;
      error.value = e as Error;
      suggestions.value = [];
      totalCount.value = 0;
    } finally {
      if (query === latestQuery) isLoading.value = false;
    }
  };

  const debouncedFetch = debounce(fetchSuggestionsInternal, 300);
  const fetchSuggestions = (query: string) => {
    latestQuery = query;
    return debouncedFetch(query);
  };

  const clearSuggestions = () => {
    latestQuery = '';
    suggestions.value = [];
    totalCount.value = 0;
    isLoading.value = false;
    error.value = null;
  };

  return {
    suggestions,
    totalCount,
    isLoading,
    error,
    fetchSuggestions,
    clearSuggestions,
  };
}
