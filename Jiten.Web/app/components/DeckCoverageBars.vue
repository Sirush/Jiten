<script setup lang="ts">
  import type { Deck } from '~/types';

  const props = defineProps<{ deck: Deck }>();

  const combinedCoverage = computed(() => Math.min(props.deck.coverage + props.deck.youngCoverage, 100));
  const combinedUniqueCoverage = computed(() => Math.min(props.deck.uniqueCoverage + props.deck.youngUniqueCoverage, 100));

  const coverageTooltip = computed(() =>
    `Mature: ${((props.deck.wordCount * props.deck.coverage) / 100).toFixed(0)} / ${props.deck.wordCount} (${props.deck.coverage.toFixed(1)}%)` +
    `\nYoung: ${((props.deck.wordCount * props.deck.youngCoverage) / 100).toFixed(0)} / ${props.deck.wordCount} (${props.deck.youngCoverage.toFixed(1)}%)` +
    `\nTotal: ${combinedCoverage.value.toFixed(1)}%`);
  const uniqueCoverageTooltip = computed(() =>
    `Mature: ${((props.deck.uniqueWordCount * props.deck.uniqueCoverage) / 100).toFixed(0)} / ${props.deck.uniqueWordCount} (${props.deck.uniqueCoverage.toFixed(1)}%)` +
    `\nYoung: ${((props.deck.uniqueWordCount * props.deck.youngUniqueCoverage) / 100).toFixed(0)} / ${props.deck.uniqueWordCount} (${props.deck.youngUniqueCoverage.toFixed(1)}%)` +
    `\nTotal: ${combinedUniqueCoverage.value.toFixed(1)}%`);
</script>

<template>
  <div class="flex flex-col gap-2 text-sm">
    <Tooltip :content="coverageTooltip" block>
      <div class="flex items-baseline justify-between gap-2">
        <span class="min-w-0 truncate text-gray-600 dark:text-gray-400 font-normal">Coverage</span>
        <span class="shrink-0 tabular-nums font-bold text-gray-900 dark:text-gray-50">{{ deck.coverage.toFixed(1) }}%</span>
      </div>
      <div class="relative w-full bg-gray-300 dark:bg-gray-700 rounded h-2.5 overflow-hidden mt-1">
        <div class="absolute inset-y-0 bg-purple-500/40 rounded-l transition-all duration-700" :style="{ width: combinedCoverage.toFixed(1) + '%' }" />
        <div class="absolute inset-y-0 bg-purple-500 rounded-l transition-all duration-700" :style="{ width: deck.coverage.toFixed(1) + '%' }" />
      </div>
    </Tooltip>
    <Tooltip :content="uniqueCoverageTooltip" block>
      <div class="flex items-baseline justify-between gap-2">
        <span class="min-w-0 truncate text-gray-600 dark:text-gray-400 font-normal">Unique</span>
        <span class="shrink-0 tabular-nums font-bold text-gray-900 dark:text-gray-50">{{ deck.uniqueCoverage.toFixed(1) }}%</span>
      </div>
      <div class="relative w-full bg-gray-300 dark:bg-gray-700 rounded h-2.5 overflow-hidden mt-1">
        <div class="absolute inset-y-0 bg-purple-500/40 rounded-l transition-all duration-700" :style="{ width: combinedUniqueCoverage.toFixed(1) + '%' }" />
        <div class="absolute inset-y-0 bg-purple-500 rounded-l transition-all duration-700" :style="{ width: deck.uniqueCoverage.toFixed(1) + '%' }" />
      </div>
    </Tooltip>
  </div>
</template>
