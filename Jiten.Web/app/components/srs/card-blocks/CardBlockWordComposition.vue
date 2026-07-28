<script setup lang="ts">
  import type { CardLayoutBlock, WordCompositionBlockOptions } from '~/types';
  import { wordCompositionDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<WordCompositionBlockOptions>(wordCompositionDefaults, props.block.options));

  const { wordData, isPreview, sample } = useCardContext();
  const convertToRuby = useConvertToRuby();
</script>

<template>
  <div v-if="isPreview" class="mt-3">
    <h3 v-if="!opts.hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Composed of</h3>
    <CardBlockSpoiler :enabled="opts.spoiler">
      <div class="flex flex-wrap gap-2">
        <div
          v-for="(comp, i) in sample!.composedOf"
          :key="i"
          class="inline-flex items-center gap-3 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700"
        >
          <span class="text-xl font-medium" lang="ja" v-html="convertToRuby(comp.ruby)" />
          <span class="text-surface-600 dark:text-surface-400 text-xs max-w-[14rem] line-clamp-2">{{ comp.def }}</span>
        </div>
      </div>
    </CardBlockSpoiler>
  </div>
  <template v-else-if="wordData?.composedOf?.length">
    <h3 v-if="opts.spoiler && !opts.hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mt-2">Composed of</h3>
    <CardBlockSpoiler :enabled="opts.spoiler">
      <WordComposition :components="wordData.composedOf" :hide-heading="opts.hideHeading || opts.spoiler" />
    </CardBlockSpoiler>
  </template>
</template>
