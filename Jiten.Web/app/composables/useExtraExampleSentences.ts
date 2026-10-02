import type { MaybeRefOrGetter } from 'vue';
import type { ExampleSentence, ExampleSentencesByDifficultyResponse } from '~/types';

export interface ExtraSentenceTarget {
  wordId: number;
  readingIndex: number;
  exampleSentence?: { sourceDeck?: { deckId: number }; sourceParent?: { deckId: number }; isCustom?: boolean };
}

const BAND_SIZE = 0.5;
const PAGE_SIZE = 3;

/// Paging state for the "See more sentences" list under a study card, shared by the legacy card and
/// the block card. Difficulty modes walk outward one band per call; Random pages by exclusion.
export function useExtraExampleSentences(target: MaybeRefOrGetter<ExtraSentenceTarget | null | undefined>) {
  const { $api } = useNuxtApp();
  const srsStore = useSrsStore();
  const authStore = useAuthStore();

  const sentences = ref<ExampleSentence[]>([]);
  const expanded = ref(false);
  const canLoadMore = ref(true);
  const isLoading = ref(false);
  const nextBandMin = ref(0);
  const nextBandMax = ref(BAND_SIZE);
  const bandStarted = ref(false);
  // Sentences fetched past the page; kept rather than dropped because the difficulty cursor has already moved beyond them
  let lookahead: ExampleSentence[] = [];
  let sourceExhausted = false;

  function reset() {
    sentences.value = [];
    expanded.value = false;
    canLoadMore.value = true;
    bandStarted.value = false;
    lookahead = [];
    sourceExhausted = false;
  }

  function startBand(sorting: string) {
    if (sorting === 'HardestFirst') {
      nextBandMin.value = 999;
      nextBandMax.value = 999 + BAND_SIZE;
    } else {
      nextBandMin.value = 0;
      nextBandMax.value = BAND_SIZE;
    }
    bandStarted.value = true;
  }

  // Signed-in users get the study-deck-aware endpoint; the anonymous vocabulary routes are the fallback
  // for surfaces without a user (and for a logged-out session), and answer the same shape.
  async function fetchPage(card: ExtraSentenceTarget, sorting: string, alreadyLoaded: number[], take: number) {
    const descending = sorting === 'HardestFirst';

    if (authStore.isAuthenticated) {
      return await $api<ExampleSentencesByDifficultyResponse>('srs/word-example-sentences', {
        method: 'POST',
        body: {
          wordId: card.wordId,
          readingIndex: card.readingIndex,
          excludedDeckIds: alreadyLoaded,
          sorting,
          minDifficulty: nextBandMin.value,
          maxDifficulty: nextBandMax.value,
          descending,
          take,
          readableFirst: sentences.value.length === 0,
        },
      });
    }

    if (sorting === 'Random') {
      const results = await $api<ExampleSentence[]>(`vocabulary/${card.wordId}/${card.readingIndex}/random-example-sentences?take=${take}`, {
        method: 'POST',
        body: alreadyLoaded,
      });
      return { sentences: results, minDifficulty: 0, maxDifficulty: 0, searchedBandMin: 0, searchedBandMax: 0 };
    }

    return await $api<ExampleSentencesByDifficultyResponse>(
      `vocabulary/${card.wordId}/${card.readingIndex}/example-sentences-by-difficulty?minDifficulty=${nextBandMin.value}&maxDifficulty=${nextBandMax.value}&descending=${descending}&take=${take}`,
      { method: 'POST', body: alreadyLoaded }
    );
  }

  function advanceBand(sorting: string, results: ExampleSentencesByDifficultyResponse) {
    if (sorting === 'HardestFirst') {
      nextBandMax.value = results.searchedBandMin;
      nextBandMin.value = nextBandMax.value - BAND_SIZE;
      if (nextBandMax.value <= results.minDifficulty) sourceExhausted = true;
    } else {
      nextBandMin.value = results.searchedBandMax;
      nextBandMax.value = nextBandMin.value + BAND_SIZE;
      if (nextBandMin.value > results.maxDifficulty) sourceExhausted = true;
    }
  }

  async function loadMore() {
    const card = toValue(target);
    if (!card || isLoading.value) return;
    isLoading.value = true;
    const sorting = srsStore.studySettings.exampleSentenceSorting;
    if (!bandStarted.value) startBand(sorting);

    const page = [...lookahead];
    lookahead = [];

    try {
      if (!sourceExhausted) {
        const alreadyLoaded = [...sentences.value, ...page].map((s) => s.sourceDeckParent?.deckId ?? s.sourceDeck.deckId);
        // The card's own sentence must not come back as the first extra
        const shown = card.exampleSentence?.isCustom ? undefined : card.exampleSentence;
        const shownDeckId = shown?.sourceParent?.deckId ?? shown?.sourceDeck?.deckId;
        if (shownDeckId !== undefined && !alreadyLoaded.includes(shownDeckId)) alreadyLoaded.push(shownDeckId);

        const take = PAGE_SIZE - page.length + 1;
        const results = await fetchPage(card, sorting, alreadyLoaded, take);
        page.push(...results.sentences);
        if (results.sentences.length < take) sourceExhausted = true;
        if (sorting !== 'Random') advanceBand(sorting, results);
      }

      sentences.value.push(...page.slice(0, PAGE_SIZE));
      lookahead = page.slice(PAGE_SIZE);
      canLoadMore.value = lookahead.length > 0 || !sourceExhausted;
      expanded.value = true;
    } catch (e) {
      // A 429 is transient: keep the button so the user can retry once the window frees up.
      const status = (e as { status?: number; statusCode?: number } | null)?.status ?? (e as { statusCode?: number } | null)?.statusCode;
      if (status === 429) {
        lookahead = page;
      } else {
        sentences.value.push(...page);
        canLoadMore.value = false;
      }
    } finally {
      isLoading.value = false;
    }
  }

  function toggle() {
    if (expanded.value) {
      expanded.value = false;
    } else if (sentences.value.length === 0 && canLoadMore.value) {
      loadMore();
    } else {
      expanded.value = true;
    }
  }

  watch(() => {
    const card = toValue(target);
    return card ? `${card.wordId}-${card.readingIndex}` : '';
  }, reset);

  return { sentences, expanded, canLoadMore, isLoading, loadMore, toggle, reset };
}
