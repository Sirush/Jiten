<script setup lang="ts">
  import type { CardLayoutBlock, CustomMeaningBlockOptions } from '~/types';
  import CustomMeaning from '~/components/CustomMeaning.vue';
  import { customMeaningDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<CustomMeaningBlockOptions>(customMeaningDefaults, props.block.options));

  const { card, isPreview, sample } = useCardContext();

  const sizeClass = computed(() => (opts.value.size === 'small' ? 'text-sm' : opts.value.size === 'large' ? 'text-lg' : ''));
</script>

<template>
  <CardBlockSpoiler :enabled="opts.spoiler">
    <CustomMeaning v-if="!isPreview && card" :word-id="card.wordId" editable class="mt-3 mb-4" :class="sizeClass" />
    <div v-else-if="isPreview && sample?.customMeaning" class="mt-3 mb-4 flex items-start gap-1.5 text-surface-700 dark:text-surface-300" :class="sizeClass">
      <i class="pi pi-pencil text-xs mt-1 text-surface-400 shrink-0" />
      <span>{{ sample.customMeaning }}</span>
    </div>
  </CardBlockSpoiler>
</template>
