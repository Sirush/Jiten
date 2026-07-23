<script setup lang="ts">
  import type { CardLayoutBlock, WordUsedInBlockOptions } from '~/types';
  import { wordUsedInDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<WordUsedInBlockOptions>(wordUsedInDefaults, props.block.options));

  const { card, wordData, isPreview, sample } = useCardContext();
  const convertToRuby = useConvertToRuby();
</script>

<template>
  <div v-if="isPreview" class="mt-4">
    <h3 v-if="!opts.hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Used in {{ sample!.usedInTotal }} words</h3>
    <CardBlockSpoiler :enabled="opts.spoiler">
      <div class="flex flex-col gap-y-3">
        <div v-for="(comp, i) in sample!.usedIn" :key="i" class="flex items-start gap-3 py-1 border-b border-surface-200/60 dark:border-surface-700/60">
          <span class="text-lg font-medium self-end" lang="ja" v-html="convertToRuby(comp.ruby)" />
          <div class="flex-1 min-w-0 flex flex-col">
            <span class="text-[10px] text-surface-500 dark:text-surface-400 leading-none self-end">#{{ comp.rank.toLocaleString() }}</span>
            <span class="text-surface-600 dark:text-surface-400 text-xs line-clamp-2 mt-0.5">{{ comp.def }}</span>
          </div>
        </div>
      </div>
    </CardBlockSpoiler>
  </div>
  <template v-else-if="card && wordData?.usedInTotal">
    <h3 v-if="opts.spoiler && !opts.hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mt-4">
      Used in {{ wordData.usedInTotal }} word{{ wordData.usedInTotal === 1 ? '' : 's' }}
    </h3>
    <CardBlockSpoiler :enabled="opts.spoiler">
      <WordUsedIn
        :key="`usedin-${card.wordId}-${card.readingIndex}`"
        :word-id="card.wordId"
        :reading-index="card.readingIndex"
        :initial-items="wordData.usedIn ?? []"
        :total="wordData.usedInTotal"
        :highlight="wordData.mainReading.text"
        :collapsed-count="2"
        :hide-heading="opts.hideHeading || opts.spoiler"
      />
    </CardBlockSpoiler>
  </template>
</template>
