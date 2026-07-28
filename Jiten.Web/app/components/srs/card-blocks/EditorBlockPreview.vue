<script setup lang="ts">
  import type { CardLayoutBlock, StudySettingsDto } from '~/types';
  import { cardBlockRegistry } from './cardBlockRegistry';
  import { createSampleCardContext } from './sampleCard';
  import { provideCardContext } from './useCardContext';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back'; settings: StudySettingsDto }>();

  // Each preview instance pins its own front/back context so both editor panels can render at once.
  const isFlipped = computed(() => props.side === 'back');
  provideCardContext(createSampleCardContext(computed(() => props.settings), isFlipped, { isolated: true }));
</script>

<template>
  <component :is="cardBlockRegistry[block.type].component" :block="block" :side="side" />
</template>
