<script setup lang="ts">
  import type { CardLayoutBlock, PitchAccentBlockOptions } from '~/types';
  import { pitchAccentDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<PitchAccentBlockOptions>(pitchAccentDefaults, props.block.options));

  const { card, wordData, isPreview, sample, writeInInputPhase } = useCardContext();

  const pitchReadingText = computed(() => {
    if (isPreview) return sample!.reading;
    if (wordData.value) return wordData.value.mainReading.text;
    const kanaReading = card.value?.readings.find((r) => r.formType === 1);
    return kanaReading?.text || card.value?.wordTextPlain || '';
  });

  const pitchAccents = computed<number[] | null>(() => {
    if (isPreview) return [sample!.pitchAccent];
    const accents = wordData.value?.pitchAccents || card.value?.pitchAccents;
    return accents && accents.length > 0 ? accents : null;
  });

  // Reveals the reading: hidden while a write-in reading answer is being typed (front placements only;
  // back placements never render during the input phase).
  const masked = computed(() => writeInInputPhase.value);
</script>

<template>
  <ClientOnly v-if="!masked">
    <div v-if="pitchAccents" class="mb-3">
      <h3 v-if="!opts.hideHeading" class="text-gray-500 dark:text-gray-300 text-sm mb-2">Pitch accent</h3>
      <CardBlockSpoiler :enabled="opts.spoiler">
        <div class="flex flex-wrap gap-2">
          <LazyPitchDiagram v-for="pitch in pitchAccents" :key="pitch" :reading="pitchReadingText" :pitch-accent="pitch" />
        </div>
      </CardBlockSpoiler>
    </div>
  </ClientOnly>
</template>
