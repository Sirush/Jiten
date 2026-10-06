<script setup lang="ts">
  import type { ComparisonSuggestionDto } from '~/types/types';
  import { DeckStatus } from '~/types/enums';

  const request = useRatingRequest();
  const { fetchSuggestions, fetchRating } = useDifficultyVotes();

  const visible = ref(false);
  const deckId = ref<number | null>(null);
  const title = ref<string | null>(null);
  const existingRating = ref<number | null>(null);
  const suggestions = ref<ComparisonSuggestionDto[]>([]);
  const comparisonIndex = ref(0);
  const voteTimestamps = ref<number[]>([]);
  const showCalibrationBanner = ref(false);
  let calibrationTimer: ReturnType<typeof setTimeout> | undefined;

  watch(request, async (value) => {
    if (!value) return;
    request.value = null;
    deckId.value = value.deckId;
    title.value = value.title;
    existingRating.value = null;
    suggestions.value = [];
    comparisonIndex.value = 0;
    visible.value = true;

    const [rating, pairs] = await Promise.all([fetchRating(value.deckId), fetchSuggestions(value.deckId)]);
    if (deckId.value !== value.deckId) return;
    existingRating.value = rating;
    suggestions.value = pairs.slice(0, 2).map((pair) => (pair.deckA.id === value.deckId ? pair : { deckA: pair.deckB, deckB: pair.deckA }));
  }, { immediate: true });

  let closedByUndo = false;
  onMediaListChange((change) => {
    if (!visible.value || change.deckId !== deckId.value || change.status === undefined || change.status === DeckStatus.Completed) return;
    closedByUndo = true;
    visible.value = false;
  });

  watch(visible, (now, before) => {
    if (closedByUndo) {
      closedByUndo = false;
      return;
    }
    if (before && !now && Math.random() < 0.25) {
      showCalibrationBanner.value = true;
      clearTimeout(calibrationTimer);
      calibrationTimer = setTimeout(() => (showCalibrationBanner.value = false), 8000);
    }
  });

  onBeforeUnmount(() => clearTimeout(calibrationTimer));

  const currentPair = computed(() => (comparisonIndex.value < suggestions.value.length ? suggestions.value[comparisonIndex.value] : null));
</script>

<template>
  <Dialog v-if="visible && deckId != null" v-model:visible="visible" modal header="Rate Difficulty" class="w-full" style="max-width: 40rem">
    <div class="flex flex-col gap-6">
      <div>
        <p class="text-sm text-muted-color mb-2">
          How difficult did you find
          <strong v-if="title" v-bind="japaneseTextAttrs(title)">{{ title }}</strong>
          <strong v-else>this series</strong>?
        </p>
        <LazyDifficultyRating :deck-id="deckId" :current-rating="existingRating" />
      </div>

      <template v-if="suggestions.length > 0">
        <Divider />
        <div v-if="currentPair">
          <p class="text-sm text-muted-color mb-2">Compare with other media you've completed:</p>
          <LazyDifficultyComparison
            :deck-a="currentPair.deckA"
            :deck-b="currentPair.deckB"
            :vote-timestamps="voteTimestamps"
            @voted="comparisonIndex++"
            @skipped="comparisonIndex++"
          />
        </div>
        <div v-else class="flex flex-col items-center gap-3 py-6">
          <i class="pi pi-check-circle text-green-500 text-4xl" />
          <p class="text-sm text-muted-color text-center">
            Thanks for helping refine the difficulties! <br />
            <NuxtLink to="/ratings" target="_blank" class="text-primary-500 hover:underline font-semibold"> Compare more media → </NuxtLink>
          </p>
        </div>
      </template>

      <div class="flex justify-end items-center pt-2">
        <Button label="Done" severity="secondary" @click="visible = false" />
      </div>
    </div>
  </Dialog>

  <div v-if="showCalibrationBanner" class="fixed bottom-4 left-1/2 z-50 w-[min(28rem,calc(100vw-2rem))] -translate-x-1/2">
    <Message severity="info" :closable="true" @close="showCalibrationBanner = false">
      Help refine the difficulty ratings:
      <NuxtLink to="/ratings" class="font-semibold underline" target="_blank">compare more media</NuxtLink>
    </Message>
  </div>
</template>
