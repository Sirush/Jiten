<script setup lang="ts">
  import type { SiteStatsLibrary } from '~/types';
  import { formatCompact } from '~/utils/siteStats';

  const props = defineProps<{
    library: SiteStatsLibrary;
  }>();

  const figures = computed(() =>
    [
      { value: props.library.titles, label: 'Titles analysed' },
      { value: props.library.charactersParsed, label: 'Characters parsed' },
      { value: props.library.addedThisMonth, label: 'Added this month' },
      { value: props.library.requestsFulfilled, label: 'Requests fulfilled' },
      { value: props.library.difficultyVotes, label: 'Difficulty votes' },
    ].filter((f) => f.value > 0)
  );
</script>

<template>
  <StatsSection v-if="figures.length" title="Media library">
    <dl class="grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-x-4 gap-y-3 m-0">
      <div v-for="figure in figures" :key="figure.label" class="min-w-0 flex flex-col-reverse">
        <dt class="text-sm text-gray-600 dark:text-gray-400">{{ figure.label }}</dt>
        <dd class="m-0 text-xl font-bold tabular-nums">
          {{ formatCompact(figure.value) }}
        </dd>
      </div>
    </dl>
    <StatsRunningTotalChart v-if="library.byMonth.length > 1" :points="library.byMonth" label="Titles" />
  </StatsSection>
</template>
