import { debounce } from 'perfect-debounce';
import type { SeriesKind, SeriesSummary } from '~/types/series';
import type { PaginatedResponse } from '~/types/types';

/** Admin series or setting search for pickers; `excludeId` drops the series being merged. */
export function useSeriesSuggestions(kind: MaybeRefOrGetter<SeriesKind>) {
  const { $api } = useNuxtApp();
  const suggestions = ref<SeriesSummary[]>([]);
  let latest = 0;

  const fetchSuggestions = debounce(async (query: string, excludeId: number | null = null) => {
    const request = ++latest;
    try {
      const res = await $api<PaginatedResponse<SeriesSummary[]>>('admin/series', { query: { query, kind: toValue(kind), limit: 20 } });
      if (request === latest) suggestions.value = res.data.filter((s) => s.seriesId !== excludeId);
    } catch {
      if (request === latest) suggestions.value = [];
    }
  }, 250);

  return { suggestions, fetchSuggestions };
}
