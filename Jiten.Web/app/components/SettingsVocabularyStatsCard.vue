<script setup lang="ts">
  const props = defineProps<{
    vocabStatsLoading: boolean;
    refreshing?: boolean;
    youngWordsAmount: number;
    matureWordsAmount: number;
    masteredWordsAmount: number;
    blacklistedWordsAmount: number;
    youngFormsAmount: number;
    matureFormsAmount: number;
    masteredFormsAmount: number;
    blacklistedFormsAmount: number;
    totalWordsAmount: number;
    totalFormsAmount: number;
    wordSetMasteredWords: number;
    wordSetMasteredForms: number;
    wordSetBlacklistedWords: number;
    wordSetBlacklistedForms: number;
    hasWordSetContributions: boolean;
    redundantForms: number;
    derivationCoveredWords: number;
    derivationCoveredForms: number;
  }>();

  interface StatTile {
    key: string;
    label: string;
    words: number;
    forms: number;
    dot: string;
  }

  // Kana redundancy only covers other forms of already-tracked words, so it adds forms but no words.
  const redundantWords = computed(() => props.derivationCoveredWords);
  const totalRedundantForms = computed(() => props.redundantForms + props.derivationCoveredForms);

  function counted(count: number, noun: string): string {
    return `${count.toLocaleString()} ${noun}${count === 1 ? '' : 's'}`;
  }

  const TILE_CLASS = 'flex flex-col rounded-lg border border-surface-100 dark:border-surface-800 bg-surface-50 dark:bg-surface-800/40 p-3';

  const cardTiles = computed<StatTile[]>(() => [
    { key: 'young', label: 'Young', words: props.youngWordsAmount, forms: props.youngFormsAmount, dot: 'bg-yellow-500' },
    { key: 'mature', label: 'Mature', words: props.matureWordsAmount, forms: props.matureFormsAmount, dot: 'bg-green-500' },
    { key: 'mastered', label: 'Mastered', words: props.masteredWordsAmount, forms: props.masteredFormsAmount, dot: 'bg-green-700' },
    {
      key: 'blacklisted',
      label: 'Blacklisted',
      words: props.blacklistedWordsAmount,
      forms: props.blacklistedFormsAmount,
      dot: 'bg-surface-400 dark:bg-surface-500',
    },
  ]);

  const cardlessTiles = computed<Omit<StatTile, 'dot'>[]>(() =>
    [
      { key: 'set-mastered', label: 'Word sets, mastered', words: props.wordSetMasteredWords, forms: props.wordSetMasteredForms },
      {
        key: 'set-blacklisted',
        label: 'Word sets, blacklisted',
        words: props.wordSetBlacklistedWords,
        forms: props.wordSetBlacklistedForms,
      },
      { key: 'redundancy', label: 'Redundancy', words: redundantWords.value, forms: totalRedundantForms.value },
    ].filter((tile) => tile.forms > 0)
  );

  const barSegments = computed(() => cardTiles.value.filter((tile) => tile.key !== 'blacklisted' && tile.words > 0));
</script>

<template>
  <Card>
    <template #title>
      <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-1">
        <h2 class="text-xl font-bold">Your Vocabulary</h2>
        <NuxtLink to="/settings/cards" class="text-sm font-medium">View All Words</NuxtLink>
      </div>
    </template>
    <template #content>
      <div v-if="vocabStatsLoading" class="flex flex-col gap-4" aria-busy="true" aria-label="Loading vocabulary stats">
        <Skeleton width="14rem" height="2.25rem" />
        <Skeleton height="0.75rem" border-radius="9999px" />
        <div class="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <Skeleton v-for="i in 4" :key="i" height="5rem" />
        </div>
      </div>
      <div v-else class="flex flex-col gap-5 transition-opacity duration-200" :class="{ 'opacity-60 delay-200': refreshing }" :aria-busy="refreshing">
        <section class="flex flex-col gap-3">
          <h3 class="text-sm font-semibold text-muted-color">Tracked words</h3>
          <p class="flex flex-wrap items-baseline gap-x-2">
            <span class="text-3xl font-bold tabular-nums">{{ totalWordsAmount.toLocaleString() }}</span>
            <span class="text-muted-color">{{ totalWordsAmount === 1 ? 'word' : 'words' }}</span>
          </p>
          <div v-if="barSegments.length > 0" class="flex gap-0.5 h-3 w-full overflow-hidden rounded-full" aria-hidden="true">
            <div v-for="segment in barSegments" :key="segment.key" class="h-full basis-0 min-w-3" :class="segment.dot" :style="{ flexGrow: segment.words }" />
          </div>
          <dl class="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <div v-for="tile in cardTiles" :key="tile.key" :class="TILE_CLASS">
              <dt class="order-2 flex items-center gap-1.5 text-sm mt-0.5">
                <span class="size-2 rounded-full shrink-0" :class="tile.dot" aria-hidden="true" />
                {{ tile.label }}
              </dt>
              <dd class="order-1 text-[clamp(1.1rem,5vw,1.4rem)] font-bold tabular-nums" :class="{ 'text-muted-color': tile.words === 0 }">
                {{ tile.words.toLocaleString() }}
              </dd>
              <dd class="order-3 text-xs text-muted-color tabular-nums mt-0.5">{{ counted(tile.forms, 'form') }}</dd>
            </div>
          </dl>
        </section>

        <section v-if="cardlessTiles.length > 0" class="flex flex-col gap-3 border-t border-surface-200 dark:border-surface-700 pt-4">
          <h3 class="text-sm font-semibold text-muted-color">Also covered</h3>
          <dl class="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <div v-for="tile in cardlessTiles" :key="tile.key" :class="TILE_CLASS">
              <dt class="order-2 text-sm mt-0.5">{{ tile.label }}</dt>
              <dd class="order-1 text-[clamp(1.1rem,5vw,1.4rem)] font-bold tabular-nums">{{ tile.words.toLocaleString() }}</dd>
              <dd class="order-3 text-xs text-muted-color tabular-nums mt-0.5">{{ counted(tile.forms, 'form') }}</dd>
            </div>
          </dl>
        </section>
      </div>
    </template>
  </Card>
</template>
