<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { DeckWord, DictionaryEntry, DictionarySearchResult, ParseNormalisedResult } from '~/types';
  import OmniSearch from '~/components/OmniSearch.vue';

  const route = useRoute();
  const { $api } = useNuxtApp();

  const searchContent = ref(route.query.text || '');

  useHead(() => {
    return {
      title: 'Search ' + searchContent.value,
    };
  });

  const {
    data: response,
    status,
  } = await useApiFetch<ParseNormalisedResult>('vocabulary/parse-normalised', {
    query: { text: searchContent },
    watch: [searchContent],
  });

  const words = computed<DeckWord[]>(() => response.value?.words || []);
  const hasMeaningfulParseResults = computed(() => words.value.some(w => w.wordId !== 0));

  const isLikelyEnglish = computed(() => {
    const text = String(searchContent.value).trim();
    if (text.includes('*')) return true;
    const hasJapanese = /[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]/.test(text);
    if (hasJapanese) return false;
    if (text.includes(' ')) return true;

    const matched = words.value.filter(w => w.wordId !== 0);
    const unmatched = words.value.filter(w => w.wordId === 0);
    if (matched.length > 0 && matched.every(w => w.originalText.length === 1) && unmatched.length > 0) return true;

    return false;
  });

  const canParse = computed(() => {
    if (!hasMeaningfulParseResults.value) return false;
    if (isLikelyEnglish.value) return false;
    return true;
  });

  const selectedWord = ref<DeckWord | undefined>();

  const activeSearchQuery = computed(() => String(searchContent.value));

  // Mirrors the cap in VocabularyController.SearchDictionary; parsing allows far longer input,
  // so a long query must skip the dictionary call rather than trade it for a 400.
  const DICTIONARY_QUERY_MAX_LENGTH = 200;

  const isSearchable = computed(() => {
    const trimmed = activeSearchQuery.value.trim();
    return trimmed.length > 0 && trimmed.length <= DICTIONARY_QUERY_MAX_LENGTH;
  });

  const searchTooLong = computed(() => activeSearchQuery.value.trim().length > DICTIONARY_QUERY_MAX_LENGTH);

  const {
    data: searchResponse,
    status: searchStatus,
    execute: executeSearch,
  } = await useApiFetch<DictionarySearchResult>('vocabulary/search', {
    query: { query: activeSearchQuery },
    watch: false,
    immediate: isSearchable.value,
  });

  watch(activeSearchQuery, () => {
    if (isSearchable.value) executeSearch();
    else searchResponse.value = null;
  });

  watch(
    () => route.query.text,
    (newText) => {
      if (newText) {
        selectedWord.value = undefined;
        directMatchesShowAll.value = false;
        showParseResultsManually.value = false;
        response.value = null;
        searchResponse.value = null;
        searchContent.value = newText;
      }
    }
  );

  const hasAnyDictionaryResults = computed(() => {
    return (searchResponse.value?.results?.length ?? 0) > 0
      || (searchResponse.value?.dictionaryResults?.length ?? 0) > 0;
  });

  const showParseResultsManually = ref(route.query.parsed === 'true');

  // Only a request still in flight withholds parse results. A failed or skipped dictionary
  // search must fall through to them, otherwise every render gate is false and the page is blank.
  const showParseResults = computed(() => {
    if (!canParse.value) return false;
    if (showParseResultsManually.value) return true;
    if (searchStatus.value === 'pending') return false;
    if (hasAnyDictionaryResults.value) return false;
    return true;
  });

  const showParseLink = computed(() => {
    return canParse.value && hasAnyDictionaryResults.value && !showParseResultsManually.value;
  });

  // Word-specific direct matches (separate from main search)
  const wordDirectMatches = ref<DictionaryEntry[]>([]);
  const canLoadMoreWordResults = ref(false);
  const isLoadingWordResults = ref(false);

  watch(selectedWord, async (word) => {
    directMatchesShowAll.value = false;
    if (word && !isLikelyEnglish.value) {
      isLoadingWordResults.value = true;
      try {
        const data = await $api<DictionarySearchResult>('vocabulary/search', {
          query: { query: word.originalText },
        });
        wordDirectMatches.value = (data.results || []).filter(
          r => !(r.wordId === word.wordId && r.readingIndex === word.readingIndex)
        );
        canLoadMoreWordResults.value = data.hasMore;
      } catch {
        wordDirectMatches.value = [];
        canLoadMoreWordResults.value = false;
      } finally {
        isLoadingWordResults.value = false;
      }
    } else {
      wordDirectMatches.value = [];
      canLoadMoreWordResults.value = false;
    }
  });

  // Pagination state for main search
  const extraResults = ref<DictionaryEntry[]>([]);
  const extraDictResults = ref<DictionaryEntry[]>([]);
  const canLoadMoreResults = ref(false);
  const canLoadMoreDictResults = ref(false);
  const isLoadingMoreResults = ref(false);
  const isLoadingMoreDictResults = ref(false);

  watch(searchResponse, (resp) => {
    extraResults.value = [];
    extraDictResults.value = [];
    canLoadMoreResults.value = resp?.hasMore ?? false;
    canLoadMoreDictResults.value = (resp?.dictionaryResults?.length ?? 0) >= 50;
  });

  const allResults = computed(() => [
    ...(searchResponse.value?.results || []),
    ...extraResults.value,
  ]);

  const allDictResults = computed(() => [
    ...(searchResponse.value?.dictionaryResults || []),
    ...extraDictResults.value,
  ]);

  // Direct matches: word-specific results when a word is selected, otherwise main search results
  const directMatches = computed(() => {
    if (showParseResults.value && wordDirectMatches.value.length > 0) {
      return wordDirectMatches.value;
    }
    if (!selectedWord.value) return allResults.value;
    return allResults.value.filter(
      r => !(r.wordId === selectedWord.value!.wordId && r.readingIndex === selectedWord.value!.readingIndex)
    );
  });

  const wholeStringMatches = computed(() => {
    if (!showParseResults.value || !selectedWord.value) return [];
    if (selectedWord.value.originalText === String(searchContent.value).trim()) return [];
    return allResults.value;
  });

  const dictionaryMatches = computed(() => {
    if (!showParseResults.value) return allDictResults.value;
    // Only include English gloss results: dictionaryResults from backend,
    // plus main results when the backend treated the query as English
    const englishMainResults = searchQueryType.value === 'english' ? allResults.value : [];
    const combined = [...englishMainResults, ...allDictResults.value];
    if (!selectedWord.value) return combined;
    return combined.filter(
      r => !(r.wordId === selectedWord.value!.wordId && r.readingIndex === selectedWord.value!.readingIndex)
    );
  });

  const searchQueryType = computed(() => searchResponse.value?.queryType || '');
  const isSearchLoading = computed(() => searchStatus.value === 'pending');

  const directMatchesLabel = computed(() => {
    const type = searchQueryType.value;
    if (type === 'english') return { prefix: 'Dictionary results', subject: '' };
    if (type === 'wildcard') return { prefix: 'Wildcard results', subject: '' };
    if (showParseResults.value && selectedWord.value) return { prefix: 'Direct matches for ', subject: selectedWord.value.originalText };
    return { prefix: 'Direct matches for ', subject: String(searchContent.value).trim() };
  });

  const resultsTotalLabel = computed(() => {
    const count = directMatches.value.length;
    const more = (showParseResults.value ? canLoadMoreWordResults.value : canLoadMoreResults.value) ? '+' : '';
    return `${count}${more}`;
  });

  const dictResultsTotalLabel = computed(() => {
    const count = dictionaryMatches.value.length;
    const more = canLoadMoreDictResults.value ? '+' : '';
    return `${count}${more}`;
  });

  async function loadMoreResults() {
    if (showParseResults.value && selectedWord.value) {
      if (isLoadingMoreResults.value || !canLoadMoreWordResults.value) return;
      isLoadingMoreResults.value = true;
      try {
        const offset = wordDirectMatches.value.length;
        const data = await $api<DictionarySearchResult>('vocabulary/search', {
          query: { query: selectedWord.value.originalText, offset, limit: 50 },
        });
        if (data.results.length > 0) {
          const word = selectedWord.value;
          const filtered = data.results.filter(
            r => !(r.wordId === word.wordId && r.readingIndex === word.readingIndex)
          );
          wordDirectMatches.value.push(...filtered);
        }
        canLoadMoreWordResults.value = data.hasMore;
      } catch { canLoadMoreWordResults.value = false; }
      finally { isLoadingMoreResults.value = false; }
    } else {
      if (isLoadingMoreResults.value || !canLoadMoreResults.value) return;
      isLoadingMoreResults.value = true;
      try {
        const offset = allResults.value.length;
        const data = await $api<DictionarySearchResult>('vocabulary/search', {
          query: { query: activeSearchQuery.value, offset, limit: 50 },
        });
        if (data.results.length > 0) {
          extraResults.value.push(...data.results);
        }
        canLoadMoreResults.value = data.hasMore;
      } catch { canLoadMoreResults.value = false; }
      finally { isLoadingMoreResults.value = false; }
    }
  }

  async function loadMoreDictResults() {
    if (isLoadingMoreDictResults.value || !canLoadMoreDictResults.value) return;
    isLoadingMoreDictResults.value = true;
    try {
      const offset = allDictResults.value.length;
      const data = await $api<DictionarySearchResult>('vocabulary/search', {
        query: { query: activeSearchQuery.value, offset, limit: 50 },
      });
      if (data.dictionaryResults.length > 0) {
        extraDictResults.value.push(...data.dictionaryResults);
      }
      canLoadMoreDictResults.value = data.dictionaryResults.length >= 50;
    } catch { canLoadMoreDictResults.value = false; }
    finally { isLoadingMoreDictResults.value = false; }
  }

  // IntersectionObserver for infinite scroll
  const resultsSentinel = ref<HTMLElement | null>(null);
  const dictSentinel = ref<HTMLElement | null>(null);
  let observer: IntersectionObserver | undefined;

  onMounted(() => {
    observer = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        if (entry.target === resultsSentinel.value) loadMoreResults();
        if (entry.target === dictSentinel.value) loadMoreDictResults();
      }
    }, { rootMargin: '300px' });

    if (resultsSentinel.value) observer.observe(resultsSentinel.value);
    if (dictSentinel.value) observer.observe(dictSentinel.value);
  });

  watch(resultsSentinel, (newEl, oldEl) => {
    if (oldEl && observer) observer.unobserve(oldEl);
    if (newEl && observer) observer.observe(newEl);
  });

  watch(dictSentinel, (newEl, oldEl) => {
    if (oldEl && observer) observer.unobserve(oldEl);
    if (newEl && observer) observer.observe(newEl);
  });

  onUnmounted(() => observer?.disconnect());

  watch(
    () => [status.value, showParseResults.value] as const,
    ([newStatus]) => {
      if (showParseResults.value && !selectedWord.value) {
        selectedWord.value = words.value.find((word) => word.wordId != 0);
      } else if (newStatus === 'error' || newStatus === 'idle') {
        selectedWord.value = undefined;
      }
    },
    { immediate: true }
  );

  const handleWordClick = (word: DeckWord) => {
    if (word.wordId !== 0) {
      selectedWord.value = word;
    }
  };

  const autoOpenEntry = computed(() => {
    if (searchStatus.value !== 'success') return null;
    const isRomaji = !/[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF]/.test(String(searchContent.value).trim());
    if (isRomaji) {
      // allResults is already ordered by frequencyRank by the backend
      return allResults.value[0] ?? null;
    }
    if (dictionaryMatches.value.length > 0) return null;
    if (directMatches.value.length === 0) return null;
    return directMatches.value[0];
  });

  const directMatchesExpanded = ref(true);
  const directMatchesShowAll = ref(false);
  const dictionaryMatchesExpanded = ref(true);

  const directMatchesLimit = 10;
  const directMatchesTruncated = computed(() =>
    dictionaryMatches.value.length > 0 && !directMatchesShowAll.value && directMatches.value.length > directMatchesLimit
  );
  const visibleDirectMatches = computed(() =>
    directMatchesTruncated.value ? directMatches.value.slice(0, directMatchesLimit) : directMatches.value
  );
</script>

<template>
  <div>
    <OmniSearch />

    <div v-if="searchTooLong" class="mt-3 text-sm text-surface-500 dark:text-surface-400">
      Dictionary search is limited to {{ DICTIONARY_QUERY_MAX_LENGTH }} characters, so only parse results are shown for this text.
    </div>
    <Message v-else-if="searchStatus === 'error'" severity="warn" :closable="false" class="mt-3">
      Dictionary search is unavailable right now.
      <template v-if="canParse">Parse results are shown below.</template>
      <template v-else>Please try again in a moment.</template>
    </Message>

    <!-- Parse link: shown when dictionary results exist but parse is also available -->
    <div v-if="showParseLink" class="mt-2 mb-2">
      <button
        class="inline-flex items-center gap-2 text-purple-600 dark:text-purple-400 hover:underline text-sm"
        @click="navigateTo({ query: { ...route.query, parsed: 'true' } }, { replace: true }); showParseResultsManually = true"
      >
        <Icon name="material-symbols:frame-inspect" class="text-lg" />
        View parse results for "{{ String(searchContent).trim() }}"
      </button>
    </div>

    <template v-if="showParseResults">
      <div class="flex items-center gap-0.5 flex-wrap">
        <span v-for="(word, index) in words" :key="index" class="pr-1.5 font-noto-sans" lang="ja">
          <span
            v-if="word.wordId != 0"
            class="text-purple-600 dark:text-purple-400 text-lg underline underline-offset-4 cursor-pointer hover:font-bold"
            :class="{ 'font-bold': selectedWord?.wordId === word.wordId && selectedWord?.readingIndex === word.readingIndex }"
            @click="handleWordClick(word)"
          >
            {{ word.originalText }}
          </span>
          <span v-else>{{ word.originalText }}</span>
        </span>
        <button
          v-if="selectedWord"
          class="text-xs text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 border border-gray-300 dark:border-gray-600 rounded px-1.5 py-0.5 ml-1"
          title="Deselect word"
          @click="selectedWord = undefined"
        >
          Clear selection
        </button>
      </div>

      <div v-if="selectedWord">
        <VocabularyDetail
          :word-id="selectedWord.wordId"
          :reading-index="selectedWord.readingIndex"
          :show-redirect="true"
          :conjugations="selectedWord.conjugations"
        />
      </div>
    </template>

    <div v-if="isSearchLoading && !showParseResults" class="flex justify-center py-8">
      <ProgressSpinner style="width: 40px; height: 40px" stroke-width="4" />
    </div>

    <div v-if="directMatches.length > 0" class="mt-4">
      <div
        class="flex items-center gap-2 cursor-pointer select-none mb-2"
        @click="directMatchesExpanded = !directMatchesExpanded"
      >
        <Icon
          :name="directMatchesExpanded ? 'material-symbols:expand-more' : 'material-symbols:chevron-right'"
          class="text-gray-500 dark:text-gray-400"
        />
        <h2 class="text-sm font-medium text-gray-500 dark:text-gray-400">
          {{ directMatchesLabel.prefix }}<template v-if="directMatchesLabel.subject"> <span class="text-purple-600 dark:text-purple-400 font-noto-sans">{{ directMatchesLabel.subject }}</span></template>
          <span class="text-xs"> ({{ resultsTotalLabel }})</span>
        </h2>
      </div>

      <div v-if="directMatchesExpanded" class="flex flex-col gap-2">
        <DictionaryResultEntry
          v-for="entry in visibleDirectMatches"
          :key="`${entry.wordId}-${entry.readingIndex}`"
          :entry="entry"
          :initial-expanded="autoOpenEntry?.wordId === entry.wordId && autoOpenEntry?.readingIndex === entry.readingIndex"
        />
        <button
          v-if="directMatchesTruncated"
          class="text-sm text-purple-600 dark:text-purple-400 hover:underline self-start"
          @click="directMatchesShowAll = true"
        >
          + View {{ directMatches.length - directMatchesLimit }} more
        </button>
        <div v-if="!directMatchesTruncated && (showParseResults ? canLoadMoreWordResults : canLoadMoreResults)" ref="resultsSentinel" class="flex justify-center py-4">
          <ProgressSpinner v-if="isLoadingMoreResults" style="width: 30px; height: 30px" stroke-width="4" />
        </div>
      </div>
    </div>

    <div v-if="wholeStringMatches.length > 0" class="mt-4">
      <div class="flex items-center gap-2 mb-2">
        <Icon name="material-symbols:expand-more" class="text-gray-500 dark:text-gray-400" />
        <h2 class="text-sm font-medium text-gray-500 dark:text-gray-400">
          Direct matches for <span class="text-purple-600 dark:text-purple-400 font-noto-sans">{{ String(searchContent).trim() }}</span>
        </h2>
      </div>
      <div class="flex flex-col gap-2">
        <DictionaryResultEntry
          v-for="entry in wholeStringMatches"
          :key="`whole-${entry.wordId}-${entry.readingIndex}`"
          :entry="entry"
        />
      </div>
    </div>

    <div v-if="dictionaryMatches.length > 0" class="mt-4">
      <div
        class="flex items-center gap-2 cursor-pointer select-none mb-2"
        @click="dictionaryMatchesExpanded = !dictionaryMatchesExpanded"
      >
        <Icon
          :name="dictionaryMatchesExpanded ? 'material-symbols:expand-more' : 'material-symbols:chevron-right'"
          class="text-gray-500 dark:text-gray-400"
        />
        <h2 class="text-sm font-medium text-gray-500 dark:text-gray-400">
          Dictionary results
          <span class="text-xs">({{ dictResultsTotalLabel }})</span>
        </h2>
      </div>

      <div v-if="dictionaryMatchesExpanded" class="flex flex-col gap-2">
        <DictionaryResultEntry
          v-for="entry in dictionaryMatches"
          :key="`dict-${entry.wordId}-${entry.readingIndex}`"
          :entry="entry"
        />
        <div v-if="canLoadMoreDictResults" ref="dictSentinel" class="flex justify-center py-4">
          <ProgressSpinner v-if="isLoadingMoreDictResults" style="width: 30px; height: 30px" stroke-width="4" />
        </div>
      </div>
    </div>

    <div
      v-if="searchStatus !== 'pending' && searchStatus !== 'error' && !searchTooLong && status !== 'pending' && !showParseResults && directMatches.length === 0 && dictionaryMatches.length === 0 && String(searchContent).trim().length > 0"
      class="text-center py-8 text-gray-500 dark:text-gray-400"
    >
      No results found for "{{ activeSearchQuery }}"
    </div>
  </div>
</template>
