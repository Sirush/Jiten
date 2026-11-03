<script setup lang="ts">
  import Breadcrumb from 'primevue/breadcrumb';
  import { useSrsStore } from '~/stores/srsStore';
  import { type Word, DeckOrder, KnownState, SortOrder, StudyDeckType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { debounce } from 'perfect-debounce';
  import { parseStringArray, toBooleanOrNull } from '~/utils/queryParams';
  import { studyDeckPresentation } from '~/utils/studyDeckPresentation';
  import { type BulkActionDef, type BulkCardAction, type BulkCardInfo, bulkStatusFromKnownStates } from '~/utils/srsBulkActions';

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
  const isSmartDeck = computed(() => deck.value?.deckType === StudyDeckType.Smart);

  srsStore.fetchSettings();

  const presentation = computed(() => (deck.value ? studyDeckPresentation(deck.value, localiseTitle) : null));
  const deckName = computed(() => presentation.value?.title ?? 'Vocabulary');

  useHead(() => ({ title: `${deckName.value} - Vocabulary` }));

  const breadcrumbHome = { icon: 'pi pi-home', route: '/' };
  const breadcrumbItems = computed(() => [
    { label: 'Decks', route: '/srs/decks' },
    { label: deckName.value, route: presentation.value?.link ?? undefined },
    { label: 'Vocabulary' },
  ]);

  const { fetched: planFetched, hasFeature } = useJitenPlus();
  const hasSentenceOrder = computed(() => deck.value?.deckType === StudyDeckType.MediaDeck && deck.value.order === DeckOrder.SentenceUnlock);

  const sentenceOrderLapsed = computed(() => hasSentenceOrder.value && planFetched.value && !hasFeature('sentence-order'));
  const usesSentenceOrder = computed(() => hasSentenceOrder.value && !sentenceOrderLapsed.value);

  const sortByOptions = computed(() => {
    const d = deck.value;
    if (!d) return [{ label: frequencySortLabel.value, value: 'globalFreq' }];

    switch (d.deckType) {
      case StudyDeckType.MediaDeck:
      case StudyDeckType.MediaGroup:
        return [
          ...(usesSentenceOrder.value ? [{ label: getDeckOrderText(DeckOrder.SentenceUnlock), value: 'sentenceUnlock' }] : []),
          { label: 'Chronological', value: 'chrono' },
          { label: 'Deck Frequency', value: 'deckFreq' },
          { label: frequencySortLabel.value, value: 'globalFreq' },
        ];
      case StudyDeckType.GlobalDynamic:
        return [{ label: frequencySortLabel.value, value: 'globalFreq' }];
      case StudyDeckType.StaticWordList:
        return [
          { label: 'Import Order', value: 'importOrder' },
          { label: frequencySortLabel.value, value: 'globalFreq' },
          { label: 'Occurrences', value: 'occurrences' },
        ];
      case StudyDeckType.Smart:
        return [
          { label: 'Smart order', value: 'importOrder' },
          { label: frequencySortLabel.value, value: 'globalFreq' },
        ];
      default:
        return [{ label: frequencySortLabel.value, value: 'globalFreq' }];
    }
  });

  const frequencySortLabel = computed(() => {
    const d = deck.value;
    if (d?.frequencyMediaType) return `${getMediaTypeText(d.frequencyMediaType)} Frequency`;
    if (d?.frequencyListId) return `${d.frequencySourceName ?? 'List'} Frequency`;
    if (d?.deckType === StudyDeckType.GlobalDynamic || d?.deckType === StudyDeckType.Smart) return 'Global Frequency';
    return frequencyOrderLabel(srsStore.studySettings);
  });

  const rankSourceLabel = computed(() => {
    const d = deck.value;
    if (d?.frequencyMediaType) return getMediaTypeText(d.frequencyMediaType);
    if (d?.frequencyListId) return d.frequencySourceName ?? 'your list';
    const settings = srsStore.studySettings;
    if (settings?.defaultFrequencyMediaType) return getMediaTypeText(settings.defaultFrequencyMediaType);
    if (settings?.defaultFrequencyListId) return 'your list';
    return undefined;
  });

  const defaultSort = computed(() => {
    const d = deck.value;
    if (!d) return 'globalFreq';
    switch (d.deckType) {
      case StudyDeckType.MediaDeck:
      case StudyDeckType.MediaGroup:
        if (usesSentenceOrder.value) return 'sentenceUnlock';
        return sentenceOrderLapsed.value ? 'deckFreq' : 'chrono';
      case StudyDeckType.GlobalDynamic:
        return 'globalFreq';
      case StudyDeckType.StaticWordList:
      case StudyDeckType.Smart:
        return 'importOrder';
      default:
        return 'globalFreq';
    }
  });

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const limit = computed(() => (route.query.limit ? Number(route.query.limit) : undefined));
  const sortBy = ref(route.query.sortBy?.toString() || defaultSort.value);
  const sortDescending = ref(route.query.sortOrder ? route.query.sortOrder === String(SortOrder.Descending) : sortBy.value === 'occurrences');
  const { tiers: displayTiers, suspended, redundant, query: displayQuery } = useVocabularyDisplayFilter();
  const search = ref(route.query.search?.toString() || '');
  const debouncedSearch = ref(search.value);

  const includePos = ref<string[]>(parseStringArray(route.query.pos));
  const excludePos = ref<string[]>(parseStringArray(route.query.excludePos));
  const hideKanaOnly = ref(toBooleanOrNull(route.query.hideKanaOnly) ?? false);

  const sortOrder = computed(() => (sortDescending.value ? SortOrder.Descending : SortOrder.Ascending));

  watch(
    sentenceOrderLapsed,
    (lapsed) => {
      if (lapsed && sortBy.value === 'sentenceUnlock') sortBy.value = 'deckFreq';
    },
    { immediate: true }
  );

  watch(sortDescending, () => {
    router.replace({ query: { ...route.query, sortOrder: sortOrder.value } });
  });

  watch(sortBy, (newValue) => {
    sortDescending.value = newValue === 'occurrences';
    router.replace({ query: { ...route.query, sortBy: newValue, sortOrder: sortOrder.value } });
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
      ...displayQuery,
      search: debouncedSearch,
      pos: computed(() => (debouncedIncludePos.value.length > 0 ? debouncedIncludePos.value.join(',') : undefined)),
      excludePos: computed(() => (debouncedExcludePos.value.length > 0 ? debouncedExcludePos.value.join(',') : undefined)),
      hideKanaOnly: debouncedHideKanaOnly,
      limit: limit,
    },
    watch: [offset, debouncedSearch, limit],
  });

  const { start, end, totalItems, previousLink, nextLink, currentPage, totalPages, pageLinkFor, pageSize } = usePagination(response);

  const listContext = computed(() => ({
    label: deckName.value,
    sortLabel: sortByOptions.value.find((o) => o.value === sortBy.value)?.label,
    sortDescending: sortDescending.value,
    offset: offset.value,
    totalItems: totalItems.value,
    pageSize: pageSize.value,
  }));

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
        const filtered = response.value.data.filter((w) => !(w.wordId === word.wordId && w.mainReading.readingIndex === word.mainReading.readingIndex));
        response.value = {
          data: filtered,
          totalItems: response.value.totalItems - 1,
          pageSize: response.value.pageSize,
          currentOffset: response.value.currentOffset,
        };
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

  const selectedKeys = ref(new Set<string>());
  const bulkLoading = ref(false);

  const wordKey = (w: Word) => `${w.wordId}-${w.mainReading.readingIndex}`;
  const pageWords = computed(() => response.value?.data ?? []);
  const selectedWords = computed(() => pageWords.value.filter((w) => selectedKeys.value.has(wordKey(w))));
  const allOnPageSelected = computed(() => pageWords.value.length > 0 && pageWords.value.every((w) => selectedKeys.value.has(wordKey(w))));

  watch(
    () => response.value?.data,
    (data) => {
      const onPage = new Set((data ?? []).map(wordKey));
      for (const key of [...selectedKeys.value]) if (!onPage.has(key)) selectedKeys.value.delete(key);
    }
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

  const knownStatesAfter: Partial<Record<BulkCardAction, KnownState[]>> = {
    'neverForget-add': [KnownState.Mastered],
    'suspend-add': [KnownState.Suspended],
    'blacklist-add': [KnownState.Blacklisted],
    'neverForget-remove': [KnownState.Young],
    'suspend-remove': [KnownState.Young],
    'blacklist-remove': [KnownState.Young],
    'reset-schedule': [KnownState.Young],
    'forget-add': [KnownState.New],
  };

  function describeWord(word: Word): BulkCardInfo {
    return { wordId: word.wordId, readingIndex: word.mainReading.readingIndex, status: bulkStatusFromKnownStates(word.knownStates) };
  }

  function replacePageWords(statesByKey: Map<string, KnownState[] | undefined>) {
    if (!response.value || statesByKey.size === 0) return;
    response.value = {
      ...response.value,
      data: response.value.data.map((w) => (statesByKey.has(wordKey(w)) ? { ...w, knownStates: statesByKey.get(wordKey(w)) } : w)),
    };
  }

  function applyBulkOptimistic(action: BulkCardAction, words: Word[]) {
    const next = knownStatesAfter[action];
    if (!next) return () => {};
    const previous = new Map(words.map((w) => [wordKey(w), w.knownStates]));
    replacePageWords(new Map(words.map((w) => [wordKey(w), [...next]])));
    return () => replacePageWords(previous);
  }

  function onStatusChanged(word: Word, states: KnownState[]) {
    replacePageWords(new Map([[wordKey(word), states]]));
  }

  const extraBulkActions = computed<BulkActionDef[]>(() =>
    isStaticDeck.value ? [{ label: 'Remove from deck', icon: 'pi pi-minus-circle', severity: 'danger', run: confirmBulkRemove }] : []
  );

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
            words.map((w) => ({ wordId: w.wordId, readingIndex: w.mainReading.readingIndex }))
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
    <div class="flex flex-col gap-2">
      <Breadcrumb :home="breadcrumbHome" :model="breadcrumbItems" class="!bg-transparent !p-0 !text-xs !gap-1 overflow-x-auto">
        <template #item="{ item }">
          <NuxtLink v-if="item.route" :to="item.route" class="flex items-center gap-1 text-primary hover:underline">
            <span v-if="item.icon" :class="item.icon" />
            <span v-if="item.label" class="truncate max-w-[45vw] md:max-w-xs">{{ item.label }}</span>
          </NuxtLink>
          <span v-else class="flex items-center gap-1 text-muted-color">
            <span v-if="item.label" class="truncate max-w-[45vw] md:max-w-xs">{{ item.label }}</span>
          </span>
        </template>
      </Breadcrumb>
      <div class="flex items-center justify-between gap-2 min-h-[2.5rem]">
        <h1 class="text-lg font-bold md:text-2xl truncate">
          <span v-bind="japaneseTextAttrs(deckName)">{{ deckName }}</span>
          <span class="hidden md:inline"> - Vocabulary List</span>
        </h1>
        <div v-if="isStaticDeck" class="flex gap-2 shrink-0">
          <Button icon="pi pi-plus" label="Add Words" class="!hidden sm:!inline-flex" @click="showAddDialog = true" />
          <Button icon="pi pi-plus" class="sm:!hidden" @click="showAddDialog = true" />
        </div>
      </div>

      <VocabularyFilters
        v-model:sort-by="sortBy"
        v-model:sort-descending="sortDescending"
        v-model:display-tiers="displayTiers"
        v-model:suspended="suspended"
        v-model:redundant="redundant"
        v-model:search="search"
        v-model:include-pos="includePos"
        v-model:exclude-pos="excludePos"
        v-model:hide-kana-only="hideKanaOnly"
        :sort-by-options="sortByOptions"
        :show-display-filter="auth.isAuthenticated"
      />

      <PaginationControls
        v-if="response?.data?.length"
        :previous-link="previousLink"
        :next-link="nextLink"
        :current-page="currentPage"
        :total-pages="totalPages"
        :page-link-for="pageLinkFor"
        :start="start"
        :end="end"
        :total-items="totalItems"
        item-label="words"
        :page-size="pageSize"
        :page-size-options="[50, 100, 200]"
        mobile-compact
      />

      <SrsSelectionRow
        v-if="pageWords.length > 0"
        :all-on-page-selected="allOnPageSelected"
        :selected-count="selectedWords.length"
        :page-count="pageWords.length"
        @toggle-page="toggleSelectAll"
      />

      <VocabularyList
        :words="response?.data ?? []"
        :list-context="listContext"
        :rank-source-label="rankSourceLabel"
        :hide-occurrences="isSmartDeck"
        :status="status"
        :error="error"
        :removable="isStaticDeck"
        :removing-key="removingKey"
        :selectable="true"
        :selected-keys="selectedKeys"
        empty-message="Try adjusting your search or filters"
        @remove="confirmRemoveWord"
        @select="toggleSelect"
        @status-changed="onStatusChanged"
      >
        <template #error="{ error: err }">
          <div>Error: {{ err }}</div>
        </template>
      </VocabularyList>

      <PaginationControls
        v-if="response?.data?.length"
        :previous-link="previousLink"
        :next-link="nextLink"
        :current-page="currentPage"
        :total-pages="totalPages"
        :page-link-for="pageLinkFor"
        :start="start"
        :end="end"
        :total-items="totalItems"
        :show-summary="false"
        :scroll-to-top-on-navigate="true"
      />
    </div>

    <SrsAddWordsDialog v-if="isStaticDeck" v-model:visible="showAddDialog" :deck-id="deckId" @words-added="onWordsAdded" />

    <SrsBulkActionBar
      :items="selectedWords"
      :describe="describeWord"
      :apply-optimistic="applyBulkOptimistic"
      :extra-actions="extraBulkActions"
      :busy="bulkLoading"
      noun="word"
      @clear="selectedKeys.clear()"
    />
  </div>
</template>
