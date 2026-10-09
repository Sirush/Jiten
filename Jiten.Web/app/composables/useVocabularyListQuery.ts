import { debounce } from 'perfect-debounce';
import { type DeckVocabularyList, SortOrder } from '~/types';
import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';

interface VocabularyListQueryOptions {
  extraQuery?: Record<string, Ref<unknown>>;
  occurrenceSortLabel?: string;
}

export function useVocabularyListQuery(url: string, { extraQuery = {}, occurrenceSortLabel = 'Deck Frequency' }: VocabularyListQueryOptions = {}) {
  const route = useRoute();
  const router = useRouter();

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const limit = computed(() => (route.query.limit ? Number(route.query.limit) : undefined));

  const sortDescending = ref(route.query.sortOrder === String(SortOrder.Descending));
  const sortBy = ref(route.query.sortBy?.toString() || 'chrono');
  const { tiers: displayTiers, suspended, redundant, query: displayQuery } = useVocabularyDisplayFilter();
  const search = ref(route.query.search?.toString() || '');
  const debouncedSearch = ref(search.value);

  const includePos = ref<string[]>(parseStringArray(route.query.pos));
  const excludePos = ref<string[]>(parseStringArray(route.query.excludePos));
  const hideKanaOnly = ref(toBooleanOrNull(route.query.hideKanaOnly) ?? false);
  const frequencySource = ref(Number(route.query.frequencySource ?? 0) || 0);

  watch(frequencySource, (value) => {
    router.replace({ query: { ...route.query, frequencySource: value || undefined, offset: undefined } });
  });

  const sortOrder = computed(() => (sortDescending.value ? SortOrder.Descending : SortOrder.Ascending));

  watch(sortDescending, () => {
    router.replace({
      query: { ...route.query, sortOrder: sortOrder.value },
    });
  });

  watch(sortBy, (newValue) => {
    router.replace({
      query: { ...route.query, sortBy: newValue },
    });
  });

  const updateSearch = debounce((val: string) => {
    debouncedSearch.value = val;
    router.replace({ query: { ...route.query, search: val || undefined, offset: undefined } });
  }, 300);
  watch(search, updateSearch);

  const debouncedIncludePos = ref([...includePos.value]);
  const debouncedExcludePos = ref([...excludePos.value]);
  const debouncedHideKanaOnly = ref(hideKanaOnly.value);

  const updateAdvancedFilters = debounce(() => {
    debouncedIncludePos.value = [...includePos.value];
    debouncedExcludePos.value = [...excludePos.value];
    debouncedHideKanaOnly.value = hideKanaOnly.value;
    router.replace({
      query: {
        ...route.query,
        pos: includePos.value.length > 0 ? includePos.value.join(',') : undefined,
        excludePos: excludePos.value.length > 0 ? excludePos.value.join(',') : undefined,
        hideKanaOnly: hideKanaOnly.value ? 'true' : undefined,
        offset: 0,
      },
    });
  }, 500);

  watch([includePos, excludePos, hideKanaOnly], updateAdvancedFilters, { deep: true });

  const {
    data: response,
    status,
    error,
    refresh,
  } = useApiFetchPaginated<DeckVocabularyList>(url, {
    query: {
      offset: offset,
      sortBy: sortBy,
      sortOrder: sortOrder,
      ...displayQuery,
      search: debouncedSearch,
      pos: computed(() => (debouncedIncludePos.value.length > 0 ? debouncedIncludePos.value.join(',') : undefined)),
      excludePos: computed(() => (debouncedExcludePos.value.length > 0 ? debouncedExcludePos.value.join(',') : undefined)),
      hideKanaOnly: debouncedHideKanaOnly,
      frequencySource: computed(() => frequencySource.value || undefined),
      limit: limit,
      ...extraQuery,
    },
    watch: [offset, debouncedSearch, limit],
  });

  const pagination = usePagination(response);

  // The server fills the rank source from the account default when none is picked, so the label follows the response.
  const rankSourceName = computed(() => {
    const source = frequencySource.value || response.value?.data?.appliedFrequencySource || 0;
    return source ? getMediaTypeText(source) : null;
  });
  const rankSourceLabel = computed(() => rankSourceName.value ?? undefined);

  const sortByOptions = computed(() => [
    { label: 'Chronological', value: 'chrono' },
    { label: occurrenceSortLabel, value: 'deckFreq' },
    { label: `${rankSourceName.value ?? 'Global'} Frequency`, value: 'globalFreq' },
  ]);

  const { visibleItems: visibleWords } = useProgressiveList(
    computed(() => response.value?.data?.words ?? []),
    { initial: 20, batch: 12, keyOf: (w) => `${w.wordId}-${w.mainReading.readingIndex}` }
  );

  return {
    offset,
    sortBy,
    sortDescending,
    sortByOptions,
    displayTiers,
    suspended,
    redundant,
    search,
    includePos,
    excludePos,
    hideKanaOnly,
    frequencySource,
    response,
    status,
    error,
    refresh,
    rankSourceLabel,
    visibleWords,
    ...pagination,
  };
}
