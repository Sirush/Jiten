import type { MaybeRefOrGetter } from 'vue';
import type { ExampleSentence, ExampleSentencesByDifficultyResponse } from '~/types';

export interface ExtraSentenceTarget {
  wordId: number;
  readingIndex: number;
}

const BAND_SIZE = 0.5;

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

  function reset() {
    sentences.value = [];
    expanded.value = false;
    canLoadMore.value = true;
    if (srsStore.studySettings.exampleSentenceSorting === 'HardestFirst') {
      nextBandMin.value = 999;
      nextBandMax.value = 999 + BAND_SIZE;
    } else {
      nextBandMin.value = 0;
      nextBandMax.value = BAND_SIZE;
    }
  }

  // Signed-in users get the study-deck-aware endpoint; the anonymous vocabulary routes are the fallback
  // for surfaces without a user (and for a logged-out session), and answer the same shape.
  async function fetchPage(card: ExtraSentenceTarget, sorting: string, alreadyLoaded: number[]) {
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
          take: 3,
        },
      });
    }

    if (sorting === 'Random') {
      const results = await $api<ExampleSentence[]>(`vocabulary/${card.wordId}/${card.readingIndex}/random-example-sentences`, {
        method: 'POST',
        body: alreadyLoaded,
      });
      return { sentences: results, minDifficulty: 0, maxDifficulty: 0, searchedBandMin: 0, searchedBandMax: 0 };
    }

    return await $api<ExampleSentencesByDifficultyResponse>(
      `vocabulary/${card.wordId}/${card.readingIndex}/example-sentences-by-difficulty?minDifficulty=${nextBandMin.value}&maxDifficulty=${nextBandMax.value}&descending=${descending}`,
      { method: 'POST', body: alreadyLoaded }
    );
  }

  async function loadMore() {
    const card = toValue(target);
    if (!card) return;
    isLoading.value = true;
    const sorting = srsStore.studySettings.exampleSentenceSorting;

    try {
      const alreadyLoaded = sentences.value.map((s) => s.sourceDeck.deckId);
      const results = await fetchPage(card, sorting, alreadyLoaded);

      if (sorting === 'Random') {
        if (results.sentences.length === 0) {
          canLoadMore.value = false;
          return;
        }

        sentences.value.push(...results.sentences);
      } else {
        const descending = sorting === 'HardestFirst';

        if (results.sentences.length > 0) {
          sentences.value.push(...results.sentences);
        }

        if (descending) {
          nextBandMax.value = results.searchedBandMin;
          nextBandMin.value = nextBandMax.value - BAND_SIZE;
          if (nextBandMax.value <= results.minDifficulty) {
            canLoadMore.value = false;
          }
        } else {
          nextBandMin.value = results.searchedBandMax;
          nextBandMax.value = nextBandMin.value + BAND_SIZE;
          if (nextBandMin.value > results.maxDifficulty) {
            canLoadMore.value = false;
          }
        }

        if (results.sentences.length === 0 && canLoadMore.value) {
          return;
        }
      }

      expanded.value = true;
    } catch (e) {
      // A 429 is transient: keep the button so the user can retry once the window frees up.
      const status = (e as { status?: number; statusCode?: number } | null)?.status ?? (e as { statusCode?: number } | null)?.statusCode;
      if (status !== 429) {
        canLoadMore.value = false;
      }
    } finally {
      isLoading.value = false;
    }
  }

  function toggle() {
    if (sentences.value.length === 0) {
      loadMore();
    } else {
      expanded.value = !expanded.value;
    }
  }

  watch(() => {
    const card = toValue(target);
    return card ? `${card.wordId}-${card.readingIndex}` : '';
  }, reset);

  return { sentences, expanded, canLoadMore, isLoading, loadMore, toggle, reset };
}
