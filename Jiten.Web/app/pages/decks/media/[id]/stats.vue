<script setup lang="ts">
  import type { DeckCoverageStats, CurveDatum, DeckDetail, DeckDifficultyDto } from '~/types';
  import { DifficultyValueDisplayStyle } from '~/types';
  import { useApiFetch, useApiFetchPaginated } from '~/composables/useApiFetch';
  import { useJitenStore } from '~/stores/jitenStore';
  import { formatDifficultyValue, getMaxDifficultyLabel } from '~/utils/difficultyColours';

  const route = useRoute();
  const store = useJitenStore();
  const localiseTitle = useLocaliseTitle();

  const usePercentage = computed(() => store.difficultyValueDisplayStyle === DifficultyValueDisplayStyle.Percentage);
  const deckId = computed(() => route.params.id as string);

  const { data: deckResponse, status: deckStatus, error: deckError } = await useApiFetchPaginated<DeckDetail>(`media-deck/${deckId.value}/detail`);

  const { data: stats, status: statsStatus, error: statsError } = await useApiFetch<DeckCoverageStats>(`media-deck/${deckId.value}/stats`);

  const { data: curveData, status: curveStatus, error: curveError } = await useApiFetch<CurveDatum[]>(`media-deck/${deckId.value}/coverage-curve?points=100`);

  const { data: difficultyData, status: difficultyStatus, error: difficultyError } = await useApiFetch<DeckDifficultyDto>(`media-deck/${deckId.value}/difficulty`);

  const isDifficultyLoading = computed(() => difficultyStatus.value === 'pending');

  const sortedDeciles = computed(() => {
    if (!difficultyData.value?.deciles) return [];

    return Object.entries(difficultyData.value.deciles)
      .map(([percentage, difficulty]) => ({
        percentage,
        difficulty,
        sortKey: parseInt(percentage.replace('%', '')),
      }))
      .sort((a, b) => a.sortKey - b.sortKey);
  });

  const title = computed(() => {
    if (!deckResponse.value?.data) {
      return '';
    }

    let title = '';
    if (deckResponse.value.data.parentDeck != null) {
      title += localiseTitle(deckResponse.value.data.parentDeck) + ' - ';
    }

    title += localiseTitle(deckResponse.value.data.mainDeck);

    return title;
  });

  useRobotsRule({ noindex: true, follow: true });

  useHead(() => ({
    title: `${title.value} - Coverage Statistics`,
    meta: [
      {
        name: 'description',
        content: `Advanced statistics for ${title.value}`,
      },
    ],
  }));

  const sortedMilestones = computed(() => {
    if (!stats.value?.milestones) return [];

    return Object.entries(stats.value.milestones)
      .map(([percentage, words]) => ({
        percentage,
        words,
        sortKey: parseInt(percentage.replace('%', '')),
      }))
      .sort((a, b) => a.sortKey - b.sortKey);
  });

  const progressionDescription = computed(() => {
    const mediaType = deckResponse.value?.data?.mainDeck?.mediaType;
    const isParentDeck = (deckResponse.value?.data?.mainDeck?.childrenDeckCount ?? 0) > 0;

    let progressionUnit: string;
    switch (mediaType) {
      case 3: // Movie
        progressionUnit = 'movie';
        break;
      case 1: // Anime
      case 2: // Drama
      case 10: // Audio
        progressionUnit = isParentDeck ? 'episodes' : (mediaType === 1 ? 'anime' : 'drama');
        break;
      case 4: // Novel
      case 5: // NonFiction
      case 8: // WebNovel
        progressionUnit = isParentDeck ? 'volumes' : 'book';
        break;
      case 6: // VideoGame
        progressionUnit = 'game';
        break;
      default: // VisualNovel, Manga, etc.
        progressionUnit = 'text';
    }

    return `Shows how the difficulty changes as you progress through the ${progressionUnit}.`;
  });
</script>

<template>
  <div class="flex flex-col gap-4">
    <DeckBreadcrumb
      :deck="deckResponse?.data?.mainDeck"
      :parent-deck="deckResponse?.data?.parentDeck"
      current="Statistics"
    />

    <h1 v-if="title" class="text-2xl font-bold">{{ title }} - Statistics</h1>

    <!-- Coverage Section -->
    <Card v-if="statsStatus === 'pending' || curveStatus === 'pending'" class="p-2">
      <template #content>
        <Skeleton width="100%" height="150px" />
      </template>
    </Card>

    <Card v-else-if="statsError || curveError">
      <template #header>
        <h2 class="text-xl font-bold px-6 pt-6">Coverage</h2>
      </template>
      <template #content>
        <p class="text-gray-500 dark:text-gray-400">Coverage statistics are not available for this deck.</p>
      </template>
    </Card>

    <Card v-else-if="stats">
      <template #header>
        <h2 class="text-xl font-bold px-6 pt-6">Coverage</h2>
        <h3 class="italic text-sm px-6">This shows how much text coverage you'll have if you know the most X frequent words in that work.</h3>
      </template>
      <template #content>
        <LazyCoverageChart class="hidden sm:block" v-if="curveData" :curve-data="curveData" />
        <div v-else class="text-center text-gray-500 py-8">No curve data available</div>

        <h2 class="text-xl font-bold pt-6">Milestones</h2>
        <div class="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4 pt-2">
          <div v-for="milestone in sortedMilestones" :key="milestone.percentage">
            <div class="text-sm text-gray-500 dark:text-gray-400">{{ milestone.percentage }} Coverage</div>
            <div class="text-xl font-bold">{{ milestone.words.toLocaleString() }} words</div>
          </div>
          <div>
            <div class="text-sm text-gray-500 dark:text-gray-400">Total Words</div>
            <div class="text-2xl font-bold">{{ stats?.totalUniqueWords.toLocaleString() }}</div>
          </div>
        </div>
      </template>
    </Card>

    <!-- Difficulty Section (independent of coverage) -->
    <Card v-if="isDifficultyLoading" class="p-2">
        <template #content>
          <Skeleton width="100%" height="400px" />
        </template>
      </Card>

      <Card v-else-if="difficultyError">
        <template #header>
          <h2 class="text-xl font-bold px-6 pt-6">Difficulty Progression</h2>
        </template>
        <template #content>
          <p class="text-gray-500 dark:text-gray-400">Difficulty data is not available for this deck.</p>
        </template>
      </Card>

      <Card v-else-if="difficultyData">
        <template #header>
          <h2 class="text-xl font-bold px-6 pt-6">Difficulty Progression</h2>
          <h3 class="italic text-sm px-6">{{ progressionDescription }}</h3>
        </template>
        <template #content>
          <template v-if="difficultyData.progression && difficultyData.progression.length > 0">
            <!-- Desktop: Chart -->
            <LazyDifficultyProgressionChart
              class="hidden sm:block"
              :progression="difficultyData.progression"
              :overall-difficulty="difficultyData.difficulty"
              :media-type="deckResponse.data.mainDeck.mediaType"
              :is-parent-deck="(deckResponse.data.mainDeck.childrenDeckCount ?? 0) > 0"
              :use-percentage="usePercentage"
            />
            <!-- Mobile: Table -->
            <DifficultyProgressionTable
              class="sm:hidden"
              :progression="difficultyData.progression"
              :overall-difficulty="difficultyData.difficulty"
              :overall-peak="difficultyData.peak"
              :media-type="deckResponse.data.mainDeck.mediaType"
              :is-parent-deck="(deckResponse.data.mainDeck.childrenDeckCount ?? 0) > 0"
              :use-percentage="usePercentage"
            />
          </template>
          <div v-else class="text-center text-gray-500 py-8">No progression data available</div>

          <h2 class="text-xl font-bold pt-6">Deciles</h2>
          <h3 class="italic text-sm">Shows the difficulty level at or below which X% of the text falls.</h3>

          <!-- Desktop: Chart -->
          <LazyDecilesChart
            class="hidden sm:block"
            v-if="difficultyData.deciles && Object.keys(difficultyData.deciles).length > 0"
            :deciles="difficultyData.deciles"
            :overall-difficulty="difficultyData.difficulty"
            :use-percentage="usePercentage"
          />

          <!-- Mobile: Number grid -->
          <div class="grid grid-cols-2 gap-4 pt-2 sm:hidden">
            <div v-for="decile in sortedDeciles" :key="decile.percentage">
              <div class="text-sm text-gray-500 dark:text-gray-400">{{ decile.percentage }}%</div>
              <div class="text-xl font-bold">{{ formatDifficultyValue(decile.difficulty, usePercentage) }}</div>
            </div>
          </div>

          <div class="grid grid-cols-1 sm:grid-cols-2 gap-4 pt-6">
            <div class="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
              <div class="text-sm text-gray-500 dark:text-gray-400">Overall Difficulty</div>
              <div class="text-2xl font-bold">{{ formatDifficultyValue(difficultyData.difficulty, usePercentage) }}</div>
            </div>
            <div class="p-4 bg-gray-50 dark:bg-gray-800 rounded-lg">
              <div class="text-sm text-gray-500 dark:text-gray-400">Peak Difficulty</div>
              <div class="text-2xl font-bold">{{ formatDifficultyValue(difficultyData.peak, usePercentage) }}</div>
            </div>
          </div>
        </template>
      </Card>
  </div>
</template>

<style scoped></style>
