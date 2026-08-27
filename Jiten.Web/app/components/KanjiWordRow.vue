<script setup lang="ts">
  import type { WordSummary } from '~/types';

  defineProps<{
    word: WordSummary;
  }>();

  const convertToRuby = useConvertToRuby();
</script>

<template>
  <NuxtLink
    :to="`/vocabulary/${word.wordId}/${word.readingIndex}`"
    class="grid grid-cols-[minmax(0,1fr)_auto] sm:grid-cols-[minmax(8rem,auto)_1fr_auto] items-baseline gap-x-4 gap-y-0.5 p-2 rounded-lg hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors"
  >
    <span class="text-xl font-medium" lang="ja" v-html="convertToRuby(word.readingFurigana)" />
    <span
      v-if="word.mainDefinition"
      class="order-last sm:order-none col-span-2 sm:col-span-1 text-surface-600 dark:text-surface-400 text-sm max-sm:line-clamp-2 sm:truncate"
    >
      {{ word.mainDefinition }}
    </span>
    <span v-else class="hidden sm:block" />
    <Tag v-if="word.frequencyRank" severity="secondary" class="text-xs shrink-0">#{{ word.frequencyRank }}</Tag>
  </NuxtLink>
</template>
