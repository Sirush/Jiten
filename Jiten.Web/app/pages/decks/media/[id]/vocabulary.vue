<script setup lang="ts">
  import { type DeckVocabularyList, SortOrder } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { debounce } from 'perfect-debounce';
  import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';

  const route = useRoute();
  const router = useRouter();

  const auth = useAuthStore();
  const localiseTitle = useLocaliseTitle();

  const id = route.params.id;

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const url = computed(() => `media-deck/${id}/vocabulary`);

  const sortByOptions = ref([
    { label: 'Chronological', value: 'chrono' },
    { label: 'Deck Frequency', value: 'deckFreq' },
    { label: 'Global Frequency', value: 'globalFreq' },
  ]);

  const sortDescending = ref(route.query.sortOrder === String(SortOrder.Descending));
  const sortBy = ref(route.query.sortBy?.toString() || sortByOptions.value[0].value);
  const display = ref(route.query.display?.toString() || 'all');
  const search = ref(route.query.search?.toString() || '');
  const debouncedSearch = ref(search.value);

  const includePos = ref<string[]>(parseStringArray(route.query.pos));
  const excludePos = ref<string[]>(parseStringArray(route.query.excludePos));
  const hideKanaOnly = ref(toBooleanOrNull(route.query.hideKanaOnly) ?? false);

  const sortOrder = computed(() => sortDescending.value ? SortOrder.Descending : SortOrder.Ascending);

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

  watch(display, (newValue) => {
    router.replace({
      query: { ...route.query, display: newValue },
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
  } = await useApiFetchPaginated<DeckVocabularyList>(url.value, {
    query: {
      offset: offset,
      sortBy: sortBy,
      sortOrder: sortOrder,
      displayFilter: display,
      search: debouncedSearch,
      pos: computed(() => debouncedIncludePos.value.length > 0 ? debouncedIncludePos.value.join(',') : undefined),
      excludePos: computed(() => debouncedExcludePos.value.length > 0 ? debouncedExcludePos.value.join(',') : undefined),
      hideKanaOnly: debouncedHideKanaOnly,
    },
    watch: [offset, debouncedSearch],
  });

  const { start, end, totalItems, previousLink, nextLink } = usePagination(response);

  // Stream entries in over a few frames instead of mounting all ~100 at once.
  const { visibleItems: visibleWords } = useProgressiveList(
    computed(() => response.value?.data?.words ?? []),
    { initial: 20, batch: 12, keyOf: (w) => `${w.wordId}-${w.mainReading.readingIndex}` },
  );

  const title = computed(() => {
    if (!response.value?.data) {
      return '';
    }

    let title = '';
    if (response.value?.data.parentDeck != null) title += localiseTitle(response.value?.data.parentDeck) + ' - ';

    title += localiseTitle(response.value?.data.deck);

    return title;
  });

  useHead(() => {
    return {
      title: `${title.value} - Vocabulary`,
      meta: [
        {
          name: 'description',
          content: `Vocabulary list for ${title.value}`,
        },
      ],
    };
  });

</script>

<template>
  <div class="flex flex-col gap-2">
    <DeckBreadcrumb
      v-if="response?.data?.deck"
      :deck="response.data.deck"
      :parent-deck="response.data.parentDeck"
      current="Vocabulary"
    />
    <VocabularyFilters
      v-model:sort-by="sortBy"
      v-model:sort-descending="sortDescending"
      v-model:display-filter="display"
      v-model:search="search"
      v-model:include-pos="includePos"
      v-model:exclude-pos="excludePos"
      v-model:hide-kana-only="hideKanaOnly"
      :sort-by-options="sortByOptions"
      :show-display-filter="auth.isAuthenticated"
    />
    <PaginationControls v-if="response?.data?.words?.length" :previous-link="previousLink" :next-link="nextLink" :start="start" :end="end" :total-items="totalItems" item-label="words" />
    <VocabularyList :words="visibleWords" :status="status" :error="error" empty-message="Try adjusting your search or filters">
      <template #error="{ error: err }">
        <div>Error: {{ err }}</div>
      </template>
    </VocabularyList>
    <PaginationControls v-if="response?.data?.words?.length" :previous-link="previousLink" :next-link="nextLink" :start="start" :end="end" :total-items="totalItems" :show-summary="false" :scroll-to-top-on-next="true" />
  </div>
</template>

<style scoped></style>
