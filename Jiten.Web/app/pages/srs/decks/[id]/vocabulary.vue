<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { type Word, KnownState, SortOrder, StudyDeckType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { debounce } from 'perfect-debounce';
  import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';

  definePageMeta({ middleware: ['auth'] });

  const route = useRoute();
  const router = useRouter();
  const { $api } = useNuxtApp();
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

  // Selection + bulk actions
  const selectedKeys = ref(new Set<string>());
  const bulkLoading = ref(false);

  const wordKey = (w: Word) => `${w.wordId}-${w.mainReading.readingIndex}`;
  const pageWords = computed(() => response.value?.data ?? []);
  const selectedWords = computed(() => pageWords.value.filter((w) => selectedKeys.value.has(wordKey(w))));
  const allOnPageSelected = computed(() => pageWords.value.length > 0 && pageWords.value.every((w) => selectedKeys.value.has(wordKey(w))));

  watch(
    () => response.value?.data,
    () => selectedKeys.value.clear(),
  );

  function toggleSelect(word: Word) {
    const key = wordKey(word);
    if (selectedKeys.value.has(key)) selectedKeys.value.delete(key);
    else selectedKeys.value.add(key);
  }

  function toggleSelectAll() {
    if (allOnPageSelected.value) {
      for (const w of pageWords.value) selectedKeys.value.delete(wordKey(w));
    } else {
      for (const w of pageWords.value) selectedKeys.value.add(wordKey(w));
    }
  }

  interface BulkActionDef {
    label: string;
    icon: string;
    severity: string;
    run: () => void;
  }

  const bulkActions = computed<BulkActionDef[]>(() => {
    const actions: BulkActionDef[] = [
      { label: 'Master', icon: 'pi pi-check-circle', severity: 'success', run: () => confirmBulkState('Master', 'neverForget-add', [KnownState.Mastered]) },
      { label: 'Suspend', icon: 'pi pi-pause', severity: 'warn', run: () => confirmBulkState('Suspend', 'suspend-add', [KnownState.Suspended]) },
      { label: 'Blacklist', icon: 'pi pi-ban', severity: 'secondary', run: () => confirmBulkState('Blacklist', 'blacklist-add', [KnownState.Blacklisted]) },
    ];
    if (isStaticDeck.value) {
      actions.push({ label: 'Remove', icon: 'pi pi-trash', severity: 'danger', run: confirmBulkRemove });
    }
    return actions;
  });

  function confirmBulkState(label: string, state: string, optimistic: KnownState[]) {
    const words = selectedWords.value;
    if (words.length === 0) return;
    confirm.require({
      message: `${label} ${words.length} word${words.length !== 1 ? 's' : ''}?`,
      header: `Bulk ${label}`,
      acceptLabel: label,
      rejectLabel: 'Cancel',
      acceptClass: 'p-button-danger',
      accept: async () => {
        bulkLoading.value = true;
        try {
          await $api('srs/set-vocabulary-state-bulk', {
            method: 'POST',
            body: { state, items: words.map((w) => ({ wordId: w.wordId, readingIndex: w.mainReading.readingIndex })) },
          });
          for (const w of words) w.knownStates = optimistic;
          selectedKeys.value.clear();
          toast.add({ severity: 'success', summary: `${words.length} word${words.length !== 1 ? 's' : ''} updated`, life: 2500 });
        } catch {
          toast.add({ severity: 'error', summary: 'Failed to update words', life: 3000 });
        } finally {
          bulkLoading.value = false;
        }
      },
    });
  }

  function confirmBulkRemove() {
    const words = selectedWords.value;
    if (words.length === 0) return;
    confirm.require({
      message: `Remove ${words.length} word${words.length !== 1 ? 's' : ''} from this deck?`,
      header: 'Remove Words',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      acceptClass: 'p-button-danger',
      accept: async () => {
        bulkLoading.value = true;
        try {
          const result = await srsStore.removeDeckWordsBatch(
            deckId,
            words.map((w) => ({ wordId: w.wordId, readingIndex: w.mainReading.readingIndex })),
          );
          if (response.value) {
            const removedKeys = new Set(words.map(wordKey));
            response.value = {
              data: response.value.data.filter((w) => !removedKeys.has(wordKey(w))),
              totalItems: response.value.totalItems - result.removed,
              pageSize: response.value.pageSize,
              currentOffset: response.value.currentOffset,
            };
          }
          selectedKeys.value.clear();
          toast.add({ severity: 'info', summary: `${result.removed} word${result.removed !== 1 ? 's' : ''} removed`, life: 2500 });
        } catch {
          toast.add({ severity: 'error', summary: 'Failed to remove words', life: 3000 });
        } finally {
          bulkLoading.value = false;
        }
      },
    });
  }
</script>

<template>
  <div class="container mx-auto p-2 md:p-4 pb-24">
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

    <div v-if="pageWords.length > 0" class="flex items-center gap-3 px-3 py-2 text-sm text-surface-500">
      <Checkbox :model-value="allOnPageSelected" :binary="true" @change="toggleSelectAll" />
      <span class="text-xs cursor-pointer select-none" @click="toggleSelectAll">
        {{ selectedWords.length > 0 ? `${selectedWords.length} selected` : `Select page (${pageWords.length})` }}
      </span>
    </div>

    <VocabularyList
      :words="response?.data ?? []"
      :status="status"
      :error="error"
      :removable="isStaticDeck"
      :removing-key="removingKey"
      :selectable="true"
      :selected-keys="selectedKeys"
      empty-message="Try adjusting your search or filters"
      @remove="confirmRemoveWord"
      @select="toggleSelect"
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

    <Transition name="slide-up">
      <div
        v-if="selectedKeys.size > 0"
        class="fixed bottom-0 left-0 right-0 z-40 bg-surface-0 dark:bg-surface-900 border-t border-surface-200 dark:border-surface-700 shadow-[0_-4px_12px_rgba(0,0,0,0.1)] px-4 py-3"
      >
        <div class="container mx-auto flex items-center justify-between gap-3">
          <div class="flex items-center gap-2">
            <span class="text-sm font-medium">{{ selectedKeys.size }} selected</span>
            <Button label="Clear" text size="small" severity="secondary" @click="selectedKeys.clear()" />
          </div>
          <div class="flex gap-2 flex-wrap justify-end">
            <template v-for="action in bulkActions" :key="action.label">
              <Button
                :icon="action.icon"
                :label="action.label"
                size="small"
                :severity="action.severity"
                :loading="bulkLoading"
                class="!hidden sm:!inline-flex"
                @click="action.run()"
              />
              <Button :icon="action.icon" size="small" :severity="action.severity" :loading="bulkLoading" class="sm:!hidden" @click="action.run()" />
            </template>
          </div>
        </div>
      </div>
    </Transition>
  </div>
</template>

<style scoped>
  .slide-up-enter-active {
    transition:
      transform 0.2s ease-out,
      opacity 0.2s ease-out;
  }
  .slide-up-leave-active {
    transition:
      transform 0.15s ease-in,
      opacity 0.15s ease-in;
  }
  .slide-up-enter-from {
    transform: translateY(100%);
    opacity: 0;
  }
  .slide-up-leave-to {
    transform: translateY(100%);
    opacity: 0;
  }
</style>
