<script setup lang="ts">
  import type { Deck, SimilarDeck, MediaType } from '~/types';
  import { SelectButton, Select, Button } from 'primevue';
  import Skeleton from 'primevue/skeleton';
  import { useApiFetch } from '~/composables/useApiFetch';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { useJitenStore } from '~/stores/jitenStore';

  const props = defineProps<{
    deck: Deck;
  }>();

  const localiseTitle = useLocaliseTitle();

  // Gaussian width (in difficulty points) controlling how sharply difficulty mode favours
  // decks close to the source deck's difficulty. See plan: balanced weighting.
  const SIGMA = 0.75;
  const INITIAL_COUNT = 14;
  const STEP = 7;
  const HARD_MAX = 60;
  // "View more" stops once content similarity drops below this. Tune against real data
  // (CLI: --similar-to <id>); the baseline (INITIAL_COUNT) and HARD_MAX bound it either way.
  const SIMILARITY_THRESHOLD = 50;
  // Mirrors the backend gate (DeckVectorService.ShortRegimeUniqueWords): below this a deck is in the
  // short regime, so an empty result means "too short to compare" rather than a genuine no-match.
  const SHORT_REGIME_UNIQUE_WORDS = 1500;

  const sortMode = ref<'content' | 'difficulty'>('content');
  const modeOptions = [
    { label: 'Content', value: 'content' },
    { label: 'Difficulty', value: 'difficulty' },
  ];

  // Sentinel for "no filter". PrimeVue's Select treats a null model value as "no selection"
  // (renders empty), so we use a real value the MediaType enum never uses (it starts at 1).
  const ALL_TYPES = 0;
  // A non-zero pinned type is a saved default that follows the user across media pages
  // (persisted in a cookie via jitenStore). 0 = no saved default, so the filter starts at All types.
  const jitenStore = useJitenStore();
  const mediaTypeFilter = ref<number>(jitenStore.similarMediaPinnedType);
  // "Locked" means the current selection IS the saved default. Changing the type is a temporary
  // per-page override that leaves the saved default untouched, so the lock visually opens but the
  // cookie keeps the original type — the next page re-seeds from it.
  const isLocked = computed(() => jitenStore.similarMediaPinnedType !== ALL_TYPES && mediaTypeFilter.value === jitenStore.similarMediaPinnedType);

  function toggleLock() {
    jitenStore.similarMediaPinnedType = isLocked.value ? ALL_TYPES : mediaTypeFilter.value;
  }
  const visibleCount = ref(INITIAL_COUNT);

  // Reactive request: refetch from the backend (which over-fetches per type) when the filter changes.
  const { data, status } = await useApiFetch<SimilarDeck[]>(
    () => {
      const base = `media-deck/get-similar-decks/${props.deck.deckId}?limit=${HARD_MAX}`;
      return mediaTypeFilter.value !== ALL_TYPES ? `${base}&mediaType=${mediaTypeFilter.value}` : base;
    },
    { revalidateOnClient: true, lazy: true, watch: [mediaTypeFilter] }
  );

  // Available types come from a dedicated endpoint computed over the full candidate pool, not the
  // top unfiltered page — otherwise types that only appear deeper in the ranking (but are still
  // reachable via the per-type over-fetch) would be missing from the dropdown.
  const { data: typesData, status: typesStatus } = await useApiFetch<MediaType[]>(() => `media-deck/get-similar-deck-types/${props.deck.deckId}`, {
    revalidateOnClient: true,
    lazy: true,
  });
  const availableTypes = computed<MediaType[]>(() => [...(typesData.value ?? [])].sort((a, b) => a - b));

  const mediaTypeOptions = computed(() => {
    const opts = [{ label: 'All types', value: ALL_TYPES }, ...availableTypes.value.map((t) => ({ label: getMediaTypeText(t), value: t as number }))];
    // A locked type may not exist in this deck's candidate pool; include it anyway so the Select
    // shows its label (the empty-state message explains there are no matches) instead of a blank.
    if (mediaTypeFilter.value !== ALL_TYPES && !opts.some((o) => o.value === mediaTypeFilter.value)) {
      opts.push({ label: getMediaTypeText(mediaTypeFilter.value as MediaType), value: mediaTypeFilter.value });
    }
    return opts;
  });

  // Backend returns results content-similarity-desc. The revealable pool is every deck at or
  // above the threshold, but never fewer than INITIAL_COUNT and never more than HARD_MAX — so
  // "View more" stops at a relevance floor instead of a fixed count.
  const eligible = computed<SimilarDeck[]>(() => {
    const items = data.value ?? [];
    const aboveThreshold = items.filter((i) => i.similarityPercent >= SIMILARITY_THRESHOLD).length;
    const count = Math.min(Math.max(aboveThreshold, INITIAL_COUNT), items.length, HARD_MAX);
    return items.slice(0, count);
  });

  const ordered = computed<SimilarDeck[]>(() => {
    const items = eligible.value;
    if (sortMode.value === 'content') return items;

    const d0 = props.deck.difficultyRaw;
    return [...items]
      .map((item) => {
        const gap = item.deck.difficultyRaw - d0;
        const prox = Math.exp(-(gap * gap) / (2 * SIGMA * SIGMA));
        return { item, score: item.similarity * prox };
      })
      .sort((a, b) => b.score - a.score)
      .map((x) => x.item);
  });

  const displayed = computed<SimilarDeck[]>(() => ordered.value.slice(0, visibleCount.value));
  const remaining = computed(() => ordered.value.length - visibleCount.value);
  const canShowMore = computed(() => remaining.value > 0);

  function showMore() {
    visibleCount.value = Math.min(visibleCount.value + STEP, ordered.value.length);
  }

  watch(mediaTypeFilter, () => {
    visibleCount.value = INITIAL_COUNT;
  });
</script>

<template>
  <!-- Initial load only: a refetch on filter change keeps the section + dropdown in place. -->
  <div v-if="availableTypes.length === 0 && typesStatus !== 'success'" class="pt-4">
    <h2 class="font-bold">Similar Media</h2>
    <div class="flex flex-row flex-wrap gap-4 justify-center pt-4">
      <Skeleton v-for="i in 7" :key="i" width="136px" height="192px" />
    </div>
  </div>
  <div v-else-if="availableTypes.length > 0" class="pt-4">
    <div class="flex flex-col md:flex-row md:items-center md:justify-between gap-2">
      <div class="flex flex-wrap items-baseline gap-x-2">
        <h2 class="font-bold">Similar Media</h2>
        <span class="text-xs text-gray-500 dark:text-gray-400">
          <span class="rounded bg-primary px-1 py-0.5 font-bold text-primary-contrast">xx%</span>
          = content similarity
        </span>
      </div>
      <div class="flex flex-wrap items-center gap-2">
        <Select
          v-if="mediaTypeOptions.length > 2"
          v-model="mediaTypeFilter"
          :options="mediaTypeOptions"
          option-label="label"
          option-value="value"
          size="small"
          class="text-sm"
        />
        <Button
          v-if="mediaTypeOptions.length > 2"
          :icon="isLocked ? 'pi pi-lock' : 'pi pi-lock-open'"
          text
          rounded
          size="small"
          :severity="isLocked ? 'primary' : 'secondary'"
          :title="isLocked ? 'Default media type locked — click to unlock' : 'Lock this media type as the default'"
          :aria-label="isLocked ? 'Unlock default media type' : 'Lock default media type'"
          @click="toggleLock"
        />
        <SelectButton
          v-model="sortMode"
          :options="modeOptions"
          option-value="value"
          option-label="label"
          :allow-empty="false"
          :pt="{ button: { class: 'text-sm py-1 px-3' } }"
        />
      </div>
    </div>

    <!-- Keep the existing grid mounted during a filter refetch (useFetch retains old data while
         pending) so the section height stays put — just dim it. Skeletons are only for the first
         load, when there is nothing to show yet. -->
    <div
      v-if="displayed.length > 0"
      class="flex flex-row flex-wrap gap-4 justify-center pt-4 transition-opacity"
      :class="{ 'pointer-events-none opacity-50': status === 'pending' }"
    >
      <div v-for="item in displayed" :key="item.deck.deckId" class="flex w-34 flex-col items-center gap-1">
        <div class="relative">
          <span
            class="absolute top-1 left-1 z-10 rounded bg-primary px-1.5 py-0.5 text-xs font-bold text-primary-contrast shadow"
            :title="`${item.similarityPercent}% content match`"
          >
            {{ item.similarityPercent }}%
          </span>
          <MediaDeckCompactView :deck="item.deck" lazy-cover />
        </div>
        <NuxtLink
          :to="`/decks/media/${item.deck.deckId}/detail`"
          class="line-clamp-2 w-full text-center text-sm hover:underline"
          :title="localiseTitle(item.deck)"
        >
          {{ localiseTitle(item.deck) }}
        </NuxtLink>
      </div>
    </div>
    <div v-else-if="status === 'pending'" class="flex flex-row flex-wrap gap-4 justify-center pt-4">
      <Skeleton v-for="i in 7" :key="i" width="136px" height="192px" />
    </div>
    <div v-else class="pt-4 text-center text-sm text-gray-500 dark:text-gray-400">No similar media of this type.</div>

    <div v-if="status !== 'pending' && canShowMore" class="flex justify-center pt-3 pb-6">
      <Button :label="`View more (${remaining})`" icon="pi pi-chevron-down" class="px-6" @click="showMore" />
    </div>
  </div>
  <!-- Loaded with no results: explain rather than silently hide. -->
  <div v-else-if="typesStatus === 'success'" class="pt-4">
    <span class="font-bold">Similar Media</span>
    <p class="pt-2 text-sm text-gray-500 dark:text-gray-400">
      {{ deck.uniqueWordCount < SHORT_REGIME_UNIQUE_WORDS ? 'This title is too short to reliably find similar media.' : 'No similar media found.' }}
    </p>
  </div>
</template>

<style scoped></style>
