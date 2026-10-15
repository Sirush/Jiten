<script setup lang="ts">
  import type { SiteStatsRecent } from '~/types';
  import { formatCompact } from '~/utils/siteStats';

  const props = defineProps<{
    stats: SiteStatsRecent;
  }>();

  const figures = computed(() =>
    [
      { value: props.stats.peopleReviewing, label: 'Active learners' },
      { value: props.stats.reviews, label: 'Reviews' },
      { value: props.stats.newWords, label: 'New words learned' },
      { value: Math.round(props.stats.medianReviewsPerStudyDay), label: 'Typical reviews per study day' },
    ].filter((f) => f.value > 0)
  );
</script>

<template>
  <StatsSection v-if="figures.length" title="Last 90 days">
    <dl class="grid grid-cols-2 md:grid-cols-4 gap-x-4 gap-y-3 m-0">
      <div v-for="figure in figures" :key="figure.label" class="min-w-0 flex flex-col-reverse justify-end">
        <dt class="text-sm text-gray-600 dark:text-gray-400">{{ figure.label }}</dt>
        <dd class="m-0 text-xl font-bold tabular-nums">
          {{ formatCompact(figure.value) }}
        </dd>
      </div>
    </dl>
  </StatsSection>
</template>
