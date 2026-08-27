<script setup lang="ts">
  import type { StudySettingsDto } from '~/types';
  import { resolveCardLayout } from '~/utils/cardLayout';
  import { cardBlockRegistry } from '~/components/srs/card-blocks/cardBlockRegistry';
  import { provideCardContext } from '~/components/srs/card-blocks/useCardContext';
  import { SAMPLE_CARD, createSampleCardContext } from '~/components/srs/card-blocks/sampleCard';

  const props = defineProps<{ settings: StudySettingsDto }>();

  const SAMPLE = SAMPLE_CARD;

  const isFlipped = ref(false);
  const showMobilePreview = ref(false);

  const layout = computed(() => resolveCardLayout(props.settings));
  const frontBlocks = computed(() => layout.value.front.filter((b) => b.type !== 'frequencyRank'));
  const backBlocks = computed(() => layout.value.back.filter((b) => b.type !== 'frequencyRank'));
  const frequencyRankBlock = computed(
    () => layout.value.front.find((b) => b.type === 'frequencyRank') ?? layout.value.back.find((b) => b.type === 'frequencyRank')
  );
  const showFrequencyRankChrome = computed(() => {
    const blk = frequencyRankBlock.value;
    if (!blk) return false;
    return (blk.options?.onlyAfterFlip ?? true) ? isFlipped.value : true;
  });

  const context = createSampleCardContext(
    computed(() => props.settings),
    isFlipped
  );
  // Blur reveal — reset whenever the blur toggle changes so the effect is demonstrable both ways.
  watch(
    () => props.settings.blurExampleSentence,
    () => (context.exampleRevealed.value = false)
  );
  provideCardContext(context);
</script>

<template>
  <div>
    <!-- On mobile the preview is gated behind a button; on desktop it is always shown.
         The wrapper carries md:hidden — PrimeVue's .p-button display rule would override it on the Button itself. -->
    <div class="md:hidden mb-3">
      <Button
        type="button"
        severity="secondary"
        class="w-full"
        :icon="showMobilePreview ? 'pi pi-eye-slash' : 'pi pi-eye'"
        :label="showMobilePreview ? 'Hide preview' : 'Show card preview'"
        @click="showMobilePreview = !showMobilePreview"
      />
    </div>
    <div :class="showMobilePreview ? 'block' : 'hidden md:block'">
      <div class="flex items-center justify-between mb-2">
        <span class="text-xs text-surface-500 dark:text-surface-400">Preview — sample card</span>
        <Button
          type="button"
          severity="secondary"
          size="small"
          :icon="isFlipped ? 'pi pi-arrow-up' : 'pi pi-arrow-down'"
          :label="isFlipped ? 'Show front' : 'Flip'"
          @click="isFlipped = !isFlipped"
        />
      </div>

      <div
        class="relative bg-surface-0 dark:bg-transparent rounded-2xl shadow-lg dark:shadow-none border border-surface-200 dark:border-surface-700 p-5 lg:p-7"
      >
        <!-- Top bar: frequency rank (back only) -->
        <div class="flex justify-end items-center min-h-[1.25rem]">
          <div v-if="showFrequencyRankChrome" class="text-xs text-gray-400">#{{ SAMPLE.frequencyRank.toLocaleString() }}</div>
        </div>

        <!-- Front (always visible) -->
        <div
          class="flex flex-col items-center"
          :class="{ 'cursor-pointer': !isFlipped }"
          :role="!isFlipped ? 'button' : undefined"
          :tabindex="!isFlipped ? 0 : undefined"
          :aria-label="!isFlipped ? 'Reveal answer' : undefined"
          @click="!isFlipped && (isFlipped = true)"
          @keydown.enter="!isFlipped && (isFlipped = true)"
          @keydown.space.prevent="!isFlipped && (isFlipped = true)"
        >
          <template v-for="block in frontBlocks" :key="block.id">
            <component :is="cardBlockRegistry[block.type].component" :block="block" side="front" />
          </template>

          <div v-if="!isFlipped" class="text-sm text-surface-500 dark:text-surface-300 mt-6">Click to reveal</div>
        </div>

        <!-- Back (shown when flipped) -->
        <div v-if="isFlipped" class="mt-6 pt-6 border-t border-surface-200 dark:border-surface-700">
          <template v-for="block in backBlocks" :key="block.id">
            <component :is="cardBlockRegistry[block.type].component" :block="block" side="back" />
          </template>
        </div>
      </div>
    </div>
  </div>
</template>
