<script setup lang="ts">
  import type { SiteStatsForgottenWord } from '~/types';

  const props = defineProps<{
    words: SiteStatsForgottenWord[];
  }>();

  const convertToRuby = useConvertToRuby();

  const barMax = computed(() => Math.max(1, ...props.words.map((w) => w.missedPercent)));
</script>

<template>
  <StatsSection
    v-if="words.length"
    title="Words learners keep forgetting"
    subtitle="How often learners answered Again on a word they had already learned, last 90 days"
    note="Only words reviewed by at least 20 learners."
  >
    <div class="overflow-x-auto">
      <table class="w-full min-w-[20rem] border-collapse">
        <thead>
          <tr class="text-xs text-gray-600 dark:text-gray-400">
            <th scope="col" class="text-left font-medium py-1.5 pr-3">Word</th>
            <th scope="col" class="text-left font-medium py-1.5 pr-3 hidden sm:table-cell">Meaning</th>
            <th scope="col" class="text-left font-medium py-1.5">Missed on review</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="word in words" :key="`${word.wordId}-${word.readingIndex}`" class="border-t border-surface-200 dark:border-surface-700 align-middle">
            <td class="py-2 pr-3">
              <NuxtLink :to="`/vocabulary/${word.wordId}/${word.readingIndex}`" target="_blank" class="text-xl font-medium" lang="ja" v-html="convertToRuby(word.readingFurigana)" />
              <span v-if="word.gloss" class="block text-xs text-gray-600 dark:text-gray-400 sm:hidden">{{ word.gloss }}</span>
            </td>
            <td class="py-2 pr-3 text-sm text-gray-700 dark:text-gray-300 hidden sm:table-cell">{{ word.gloss }}</td>
            <td class="py-2 w-[40%] sm:w-[30%]">
              <span class="grid items-center gap-2" style="grid-template-columns: 2.5rem minmax(3rem, 1fr)">
                <span class="text-sm tabular-nums">{{ Math.round(word.missedPercent) }}%</span>
                <span class="h-2 rounded-sm bg-primary-100 dark:bg-primary-950 overflow-hidden" aria-hidden="true">
                  <span class="block h-full rounded-sm bg-primary-500 dark:bg-primary-400" :style="{ width: `${(word.missedPercent / barMax) * 100}%` }" />
                </span>
              </span>
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </StatsSection>
</template>
