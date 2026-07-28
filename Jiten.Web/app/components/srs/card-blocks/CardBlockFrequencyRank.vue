<script setup lang="ts">
  import type { CardLayoutBlock, FrequencyRankBlockOptions } from '~/types';
  import { frequencyRankDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<FrequencyRankBlockOptions>(frequencyRankDefaults, props.block.options));

  const { card, isFlipped, isPreview, sample } = useCardContext();

  const rank = computed(() => (isPreview ? sample!.frequencyRank : (card.value?.frequencyRank ?? 0)));
  const visible = computed(() => rank.value > 0 && (!opts.value.onlyAfterFlip || isFlipped.value));
</script>

<template>
  <div v-if="visible" class="text-xs text-gray-400 text-right">#{{ rank.toLocaleString() }}</div>
</template>
