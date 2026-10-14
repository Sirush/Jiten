<script setup lang="ts">
  import type { SiteStatsTotals } from '~/types';
  import { formatCompact } from '~/utils/siteStats';

  const props = defineProps<{
    totals: SiteStatsTotals;
  }>();

  const tiles = computed(() =>
    [
      { value: props.totals.learners, label: 'Active learners', hint: '' },
      { value: props.totals.reviews, label: 'Reviews total', hint: '' },
      { value: props.totals.wordsKnown, label: 'Words known', hint: '' },
      { value: props.totals.hoursReviewing, label: 'Hours spent reviewing', hint: '' },
      { value: props.totals.titlesFinished, label: 'Titles finished', hint: '' },
      { value: props.totals.charactersRead, label: 'Characters read', hint: '' },
    ].filter((t) => t.value > 0)
  );
</script>

<template>
  <section aria-label="All-time totals" class="grid grid-cols-2 md:grid-cols-3 gap-3">
    <div
      v-for="tile in tiles"
      :key="tile.label"
      class="min-w-0 rounded-lg border border-surface-200 bg-surface-0 px-4 py-3 dark:border-surface-700 dark:bg-surface-900"
    >
      <Tooltip :content="tile.value.toLocaleString('en-US')">
        <div class="text-2xl sm:text-3xl font-bold tabular-nums leading-tight w-fit" tabindex="0">{{ formatCompact(tile.value) }}</div>
      </Tooltip>
      <div class="text-sm text-gray-700 dark:text-gray-300 mt-0.5">{{ tile.label }}</div>
      <div class="text-xs text-gray-500 dark:text-gray-400 mt-1">{{ tile.hint }}</div>
    </div>
  </section>
</template>
