<script setup lang="ts">
  import { useAuthStore } from '~/stores/authStore';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const route = useRoute();

  const auth = useAuthStore();
  const localiseTitle = useLocaliseTitle();

  const id = route.params.id;

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
  } = useVocabularyListQuery(`media-deck/${id}/vocabulary`);

  const listContext = computed(() => ({
    label: `${title.value} - Vocabulary`,
    sortLabel: sortByOptions.value.find((o) => o.value === sortBy.value)?.label,
    sortDescending: sortDescending.value,
    offset: offset.value,
    totalItems: totalItems.value,
    pageSize: pageSize.value,
  }));

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
    <DeckBreadcrumb :deck="response?.data?.deck" :parent-deck="response?.data?.parentDeck" current="Vocabulary" />
    <h1 v-if="title" class="text-lg font-bold md:text-2xl">
      {{ title }}
      <span class="hidden md:inline">- Vocabulary List</span>
    </h1>
    <GuestAccountStrip v-if="response?.data?.deck?.uniqueWordCount" surface="deck_vocabulary">
      {{ response.data.deck.uniqueWordCount.toLocaleString() }} words in this title. You can find the ones you don't know with an account.
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
      <FrequencySourceSelect v-if="sortBy === 'globalFreq'" v-model="frequencySource" input-id="deckVocabRankSource" />
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
      empty-message="Try adjusting your search or filters"
    >
      <template #error="{ error: err }">
        <div>Error: {{ err }}</div>
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

<style scoped></style>
