<script setup lang="ts">
  import type { FranchiseNode, MediaGroupRef, MediaType } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { parseNumberArray } from '~/utils/queryParams';
  import { mediaGroupKindWord } from '~/utils/mediaGroup';

  const props = defineProps<{
    group: MediaGroupRef;
    label: string;
    /** Already narrowed by the scope and the media filter. */
    members: FranchiseNode[];
    /** Sent to the API only. */
    mediaTypes: MediaType[];
    uniqueWordCount: number | null;
  }>();

  const route = useRoute();
  const router = useRouter();
  const auth = useAuthStore();
  const localiseTitle = useLocaliseTitle();

  const excludedDeckIds = ref<number[]>(parseNumberArray(route.query.excludeDeckIds));

  const kindWord = mediaGroupKindWord(props.group.kind);

  const excludeOptions = computed(() => props.members.map((m) => ({ label: localiseTitle(m), value: m.deckId })));

  // Exclusions hidden by the media filter stay in the URL and come back with their media type, but are not sent.
  const activeExcludedIds = computed(() => {
    const visible = new Set(props.members.map((m) => m.deckId));
    return excludedDeckIds.value.filter((id) => visible.has(id));
  });
  const excludeModel = computed<number[]>({
    get: () => activeExcludedIds.value,
    set: (selected) => {
      const visible = new Set(props.members.map((m) => m.deckId));
      excludedDeckIds.value = [...excludedDeckIds.value.filter((id) => !visible.has(id)), ...selected];
    },
  });

  watch(
    excludedDeckIds,
    (ids) => {
      router.replace({ query: { ...route.query, excludeDeckIds: ids.length > 0 ? ids.join(',') : undefined, offset: undefined } });
    },
    { deep: true }
  );

  const {
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
    start,
    end,
    totalItems,
    previousLink,
    nextLink,
    currentPage,
    totalPages,
    pageLinkFor,
    pageSize,
  } = useVocabularyListQuery('media-group/vocabulary', {
    occurrenceSortLabel: `${kindWord} Frequency`,
    extraQuery: {
      kind: computed(() => props.group.kind),
      id: computed(() => props.group.id),
      mediaTypes: computed(() => (props.mediaTypes.length > 0 ? props.mediaTypes.join(',') : undefined)),
      excludeDeckIds: computed(() => (activeExcludedIds.value.length > 0 ? activeExcludedIds.value.join(',') : undefined)),
    },
  });

  const listContext = computed(() => ({
    label: `${props.label} - Vocabulary`,
    sortLabel: sortByOptions.value.find((o) => o.value === sortBy.value)?.label,
    sortDescending: sortDescending.value,
    offset: offset.value,
    totalItems: totalItems.value,
    pageSize: pageSize.value,
  }));

  const everyTitleExcluded = computed(() => props.members.length > 0 && activeExcludedIds.value.length >= props.members.length);
  const emptyMessage = computed(() =>
    everyTitleExcluded.value
      ? 'Every title in this selection is excluded. Remove a title from "Exclude titles" to see its words.'
      : 'Try adjusting your search or filters'
  );
</script>

<template>
  <div class="flex flex-col gap-2">
    <GuestAccountStrip v-if="uniqueWordCount" surface="franchise_vocabulary">
      {{ uniqueWordCount.toLocaleString() }} words in this {{ kindWord.toLowerCase() }}. You can find the ones you don't know with an account.
    </GuestAccountStrip>
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
    >
      <div class="flex flex-wrap gap-2 max-md:w-full">
        <MultiSelect
          v-if="excludeOptions.length > 1"
          v-model="excludeModel"
          :options="excludeOptions"
          option-label="label"
          option-value="value"
          placeholder="Exclude titles"
          filter
          filter-placeholder="Find a title"
          :show-toggle-all="false"
          :max-selected-labels="1"
          selected-items-label="{0} titles excluded"
          aria-label="Exclude titles from the vocabulary list"
          class="w-full md:w-56"
        />
        <FrequencySourceSelect v-if="sortBy === 'globalFreq'" v-model="frequencySource" input-id="franchiseVocabRankSource" />
      </div>
    </VocabularyFilters>
    <PaginationControls
      v-if="response?.data?.words?.length"
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
    <VocabularyList
      :words="visibleWords"
      :status="status"
      :error="error"
      :list-context="listContext"
      :rank-source-label="rankSourceLabel"
      :empty-message="emptyMessage"
    >
      <template #error>
        <div class="flex flex-col items-center gap-3 py-8 text-center">
          <p class="text-surface-600 dark:text-surface-300">The vocabulary list for this {{ kindWord.toLowerCase() }} failed to load.</p>
          <Button label="Retry" icon="pi pi-refresh" size="small" @click="refresh()" />
        </div>
      </template>
    </VocabularyList>
    <PaginationControls
      v-if="response?.data?.words?.length"
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
</template>
