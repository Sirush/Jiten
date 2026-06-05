<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { type Word, SortOrder, StudyDeckType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { debounce } from 'perfect-debounce';
  import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';

  definePageMeta({ middleware: ['auth'] });

  const route = useRoute();
  const router = useRouter();
  const srsStore = useSrsStore();
  const auth = useAuthStore();
  const toast = useToast();
  const confirm = useConfirm();
  const localiseTitle = useLocaliseTitle();

  const deckId = Number(route.params.id);

  if (srsStore.studyDecks.length === 0) {
    await srsStore.fetchStudyDecks();
  }

  const deck = computed(() => srsStore.studyDecks.find((d) => d.userStudyDeckId === deckId));
  const isStaticDeck = computed(() => deck.value?.deckType === StudyDeckType.StaticWordList);

  const deckName = computed(() => {
    const d = deck.value;
    if (!d) return 'Vocabulary';
    if (d.deckType === StudyDeckType.MediaDeck) {
      return localiseTitle({ originalTitle: d.title, romajiTitle: d.romajiTitle, englishTitle: d.englishTitle });
    }
    return d.name;
  });

  useHead(() => ({ title: `${deckName.value} - Vocabulary` }));

  const sortByOptions = computed(() => {
    const d = deck.value;
    if (!d) return [{ label: 'Global Frequency', value: 'globalFreq' }];

    switch (d.deckType) {
      case StudyDeckType.MediaDeck:
        return [
          { label: 'Chronological', value: 'chrono' },
          { label: 'Deck Frequency', value: 'deckFreq' },
          { label: 'Global Frequency', value: 'globalFreq' },
        ];
      case StudyDeckType.GlobalDynamic:
        return [{ label: 'Global Frequency', value: 'globalFreq' }];
      case StudyDeckType.StaticWordList:
        return [
          { label: 'Import Order', value: 'importOrder' },
          { label: 'Global Frequency', value: 'globalFreq' },
          { label: 'Occurrences', value: 'occurrences' },
        ];
      default:
        return [{ label: 'Global Frequency', value: 'globalFreq' }];
    }
  });

  const defaultSort = computed(() => {
    const d = deck.value;
    if (!d) return 'globalFreq';
    switch (d.deckType) {
      case StudyDeckType.MediaDeck:
        return 'chrono';
      case StudyDeckType.GlobalDynamic:
        return 'globalFreq';
      case StudyDeckType.StaticWordList:
        return 'importOrder';
      default:
        return 'globalFreq';
    }
  });

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const sortDescending = ref(route.query.sortOrder === String(SortOrder.Descending));
  const sortBy = ref(route.query.sortBy?.toString() || defaultSort.value);
  const display = ref(route.query.display?.toString() || 'all');
  const search = ref(route.query.search?.toString() || '');
  const debouncedSearch = ref(search.value);

  const includePos = ref<string[]>(parseStringArray(route.query.pos));
  const excludePos = ref<string[]>(parseStringArray(route.query.excludePos));
  const hideKanaOnly = ref(toBooleanOrNull(route.query.hideKanaOnly) ?? false);

  const sortOrder = computed(() => (sortDescending.value ? SortOrder.Descending : SortOrder.Ascending));

  watch(sortDescending, () => {
    router.replace({ query: { ...route.query, sortOrder: sortOrder.value } });
  });

  watch(sortBy, (newValue) => {
    router.replace({ query: { ...route.query, sortBy: newValue } });
  });

  watch(display, (newValue) => {
    router.replace({ query: { ...route.query, display: newValue } });
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
  } = await useApiFetchPaginated<Word[]>(`srs/study-decks/${deckId}/vocabulary`, {
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

  const showAddDialog = ref(false);
  const removingKey = ref<string | null>(null);

  function confirmRemoveWord(word: Word) {
    confirm.require({
      message: `Remove "${word.mainReading.text}" from this deck?`,
      header: 'Remove Word',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      accept: () => removeWord(word),
    });
  }

  async function removeWord(word: Word) {
    const key = `${word.wordId}-${word.mainReading.readingIndex}`;
    removingKey.value = key;
    try {
      await srsStore.removeDeckWord(deckId, word.wordId, word.mainReading.readingIndex);
      if (response.value) {
        const filtered = response.value.data.filter(
          (w) => !(w.wordId === word.wordId && w.mainReading.readingIndex === word.mainReading.readingIndex),
        );
        response.value = { data: filtered, totalItems: response.value.totalItems - 1, pageSize: response.value.pageSize, currentOffset: response.value.currentOffset };
      }
      toast.add({ severity: 'info', summary: 'Word removed', life: 2000 });
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to remove word', life: 3000 });
    } finally {
      removingKey.value = null;
    }
  }

  function onWordsAdded() {
    refresh();
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <SrsSubNav />
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4 min-h-[2.5rem]">
      <div class="flex items-center gap-2 min-w-0">
        <NuxtLink to="/srs/decks" class="text-sm text-surface-500 hover:text-surface-700 dark:hover:text-surface-300 whitespace-nowrap">‹ Decks</NuxtLink>
        <span class="text-surface-300 dark:text-surface-600">·</span>
        <h1 class="text-2xl font-bold truncate">{{ deckName }}</h1>
        <span v-if="totalItems > 0" class="text-sm text-gray-500 whitespace-nowrap">{{ totalItems }} words</span>
      </div>
      <div v-if="isStaticDeck" class="flex gap-2">
        <Button icon="pi pi-plus" label="Add Words" @click="showAddDialog = true" class="!hidden sm:!inline-flex" />
        <Button icon="pi pi-plus" @click="showAddDialog = true" class="sm:!hidden" />
      </div>
    </div>

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

    <PaginationControls v-if="response?.data?.length" :previous-link="previousLink" :next-link="nextLink" :start="start" :end="end" :total-items="totalItems" item-label="words" />

    <VocabularyList
      :words="response?.data ?? []"
      :status="status"
      :error="error"
      :removable="isStaticDeck"
      :removing-key="removingKey"
      empty-message="Try adjusting your search or filters"
      @remove="confirmRemoveWord"
    >
      <template #error="{ error: err }">
        <div>Error: {{ err }}</div>
      </template>
    </VocabularyList>

    <PaginationControls v-if="response?.data?.length"
      :previous-link="previousLink"
      :next-link="nextLink"
      :start="start"
      :end="end"
      :total-items="totalItems"
      :show-summary="false"
      :scroll-to-top-on-next="true"
    />

    <SrsAddWordsDialog v-if="isStaticDeck" v-model:visible="showAddDialog" :deck-id="deckId" @words-added="onWordsAdded" />
  </div>
</template>
