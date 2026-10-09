import { debounce } from 'perfect-debounce';
import type { MediaSuggestion, MediaSuggestionsResponse } from '~/types/types';

export function useMediaSuggestions({ limit = 5 }: { limit?: number } = {}) {
  const { $api } = useNuxtApp();

  const suggestions = ref<MediaSuggestion[]>([]);
  const totalCount = ref(0);
  const isLoading = ref(false);
  const error = ref<Error | null>(null);

  const fetchSuggestionsInternal = async (query: string) => {
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
      suggestions.value = result?.suggestions || [];
      totalCount.value = result?.totalCount || 0;
    } catch (e) {
      error.value = e as Error;
      suggestions.value = [];
      totalCount.value = 0;
    } finally {
      isLoading.value = false;
    }
  };

  const fetchSuggestions = debounce(fetchSuggestionsInternal, 300);

  const clearSuggestions = () => {
    suggestions.value = [];
    totalCount.value = 0;
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
