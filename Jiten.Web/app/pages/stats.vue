<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { GlobalStats, SiteStats } from '~/types';
  import { formatCompact, formatSnapshotDate } from '~/utils/siteStats';

  const description =
    'How people use Jiten to learn Japanese: learners, reviews, the words they know, how many words each media type needs, and what they read and watch. Updated every day.';

  useSeoMeta({
    title: 'Jiten statistics',
    description,
    ogTitle: 'Jiten statistics',
    ogDescription: description,
    twitterTitle: 'Jiten statistics',
    twitterDescription: description,
  });

  defineOgImage('PageOgImage', {
    title: 'Jiten statistics',
    category: 'Updated daily',
    description: 'Learners, reviews, known words and how many words each media type needs.',
  });

  const { data: stats } = await useApiFetch<SiteStats | null>('stats/site');
  const snapshot = computed(() => (stats.value && typeof stats.value === 'object' && stats.value.totals ? stats.value : null));

  const { data: globalStats } = await useApiFetch<GlobalStats>('stats/get-global-stats');
</script>

<template>
  <div class="flex flex-col gap-5">
    <header class="pt-2">
      <h1 class="text-3xl font-bold mb-1">Jiten statistics</h1>
      <p class="text-gray-600 dark:text-gray-400 mb-0 max-w-3xl">Global statistics updated every day.</p>
    </header>

    <template v-if="snapshot">
      <StatsHeadlineTiles :totals="snapshot.totals" />
      <StatsRecent v-if="snapshot.recent" :stats="snapshot.recent" />
      <StatsLearnersOverTime :points="snapshot.learnersByMonth" />

      <div class="grid grid-cols-1 lg:grid-cols-2 gap-5 [&>*:only-child]:lg:col-span-2">
        <StatsReviewHeatmap v-if="snapshot.heatmap" :heatmap="snapshot.heatmap" />
        <StatsKnownWords v-if="snapshot.knownWords" :known-words="snapshot.knownWords" />
      </div>

      <StatsFrequencyBands :bands="snapshot.frequencyBands" />
      <StatsWordsForCoverage v-if="snapshot.wordsForCoverage" :data="snapshot.wordsForCoverage" />
      <StatsReadWatch :rows="snapshot.reading" />
      <StatsStudying :decks="snapshot.studying" />
      <StatsForgottenWords :words="snapshot.forgotten" />
      <StatsLibrary :library="snapshot.library" />

      <p class="text-sm text-gray-500 dark:text-gray-400 text-center mb-0">
        As of {{ formatSnapshotDate(snapshot.date) }} UTC.
      </p>
    </template>

    <template v-else>
      <p class="mb-0">Statistics will appear here after the next daily update.</p>
      <dl v-if="globalStats" class="grid grid-cols-2 gap-3 m-0 max-w-md">
        <div class="rounded-lg border border-surface-200 bg-surface-0 px-4 py-3 dark:border-surface-700 dark:bg-surface-900 flex flex-col-reverse">
          <dt class="text-sm text-gray-600 dark:text-gray-400">Titles analysed</dt>
          <dd class="m-0 text-2xl font-bold tabular-nums">{{ globalStats.totalMedia.toLocaleString('en-US') }}</dd>
        </div>
        <div class="rounded-lg border border-surface-200 bg-surface-0 px-4 py-3 dark:border-surface-700 dark:bg-surface-900 flex flex-col-reverse">
          <dt class="text-sm text-gray-600 dark:text-gray-400">Characters parsed</dt>
          <dd class="m-0 text-2xl font-bold tabular-nums">{{ formatCompact(globalStats.totalMojis) }}</dd>
        </div>
      </dl>
    </template>
  </div>
</template>
