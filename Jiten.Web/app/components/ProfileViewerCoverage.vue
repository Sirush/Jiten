<script setup lang="ts">
  import type { DeckCard } from '~/types';

  const props = withDefaults(defineProps<{ deck: DeckCard; stacked?: boolean }>(), { stacked: false });

  const combined = computed(() => Math.min(props.deck.coverage + props.deck.youngCoverage, 100));
  const tooltip = computed(() => `Words in this title you know\nMature: ${props.deck.coverage.toFixed(1)}%\nWith young cards: ${combined.value.toFixed(1)}%`);
</script>

<template>
  <Tooltip :content="tooltip" block>
    <div class="text-xs" :class="stacked ? 'flex flex-col gap-1' : 'flex items-center gap-2'">
      <div v-if="stacked" class="flex items-baseline justify-between gap-2">
        <span class="min-w-0 truncate text-muted-color">Your coverage</span>
        <span class="font-bold tabular-nums text-surface-900 dark:text-surface-0">{{ Math.round(deck.coverage) }}%</span>
      </div>
      <span v-else class="whitespace-nowrap text-muted-color">Your coverage</span>
      <div class="relative h-1.5 overflow-hidden rounded bg-surface-200 dark:bg-surface-700" :class="{ 'min-w-8 flex-1': !stacked }" aria-hidden="true">
        <div class="absolute inset-y-0 left-0 rounded-l bg-purple-500/40" :style="{ width: `${combined}%` }" />
        <div class="absolute inset-y-0 left-0 rounded-l bg-purple-500" :style="{ width: `${deck.coverage}%` }" />
      </div>
      <span v-if="!stacked" class="font-bold tabular-nums text-surface-900 dark:text-surface-0">{{ Math.round(deck.coverage) }}%</span>
    </div>
  </Tooltip>
</template>
