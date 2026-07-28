<script setup lang="ts">
  import type { CardLayoutBlock, KanjiBreakdownBlockOptions } from '~/types';
  import { kanjiBreakdownDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<KanjiBreakdownBlockOptions>(kanjiBreakdownDefaults, props.block.options));

  const { card, isPreview, sample } = useCardContext();

  // The breakdown child fetches its own data and self-hides for kanji-less words; the hoisted
  // (unblurred) heading approximates that gate with a script check on the word text.
  const wordHasKanji = computed(() => /[㐀-鿿豈-﫿]/.test(card.value?.wordText ?? ''));
</script>

<template>
  <div v-if="isPreview" class="mt-2">
    <h3 v-if="!opts.hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Kanji breakdown</h3>
    <CardBlockSpoiler :enabled="opts.spoiler">
      <div class="flex flex-wrap gap-2">
        <div
          v-for="kanji in sample!.kanji"
          :key="kanji.character"
          class="inline-flex items-center gap-2 px-3 py-2 rounded-lg border border-surface-200 dark:border-surface-700"
        >
          <span class="text-2xl font-medium" lang="ja">{{ kanji.character }}</span>
          <div class="flex flex-col text-xs">
            <span class="text-surface-600 dark:text-surface-400 text-[10px]">{{ kanji.strokeCount }} strokes</span>
            <span class="text-surface-700 dark:text-surface-300 text-sm max-w-[10rem] truncate">{{ kanji.meaning }}</span>
            <span class="text-primary-600 dark:text-primary-400 text-[10px]">JLPT N{{ kanji.jlpt }}</span>
          </div>
        </div>
      </div>
    </CardBlockSpoiler>
  </div>
  <template v-else-if="card">
    <h3 v-if="opts.spoiler && !opts.hideHeading && wordHasKanji" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mt-2">Kanji breakdown</h3>
    <CardBlockSpoiler :enabled="opts.spoiler">
      <KanjiBreakdown
        :key="`${card.wordId}-${card.readingIndex}`"
        :word-id="card.wordId"
        :reading-index="card.readingIndex"
        :hide-heading="opts.hideHeading || opts.spoiler"
      />
    </CardBlockSpoiler>
  </template>
</template>
