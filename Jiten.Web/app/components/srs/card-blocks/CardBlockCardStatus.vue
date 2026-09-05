<script setup lang="ts">
  import type { CardLayoutBlock } from '~/types';
  import { useSrsStore } from '~/stores/srsStore';
  import { useCardContext } from './useCardContext';

  defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();

  const { card, isPreview, sample } = useCardContext();
  const srsStore = useSrsStore();

  const cardKey = computed(() => (card.value ? `${card.value.wordId}-${card.value.readingIndex}` : ''));
  const isAgain = computed(() => !isPreview && srsStore.againCardKeys.has(cardKey.value));
  const isLearning = computed(() => !isPreview && srsStore.learningCardKeys.has(cardKey.value));
  const isNew = computed(() => (isPreview ? sample!.isNew : !!card.value?.isNewCard));
  const isLeech = computed(() => !isPreview && !!card.value?.isLeech);

  const statusLabel = computed(() => (isAgain.value ? 'Again' : isLearning.value ? 'Learning' : isNew.value ? 'New' : 'Review'));
</script>

<template>
  <div class="flex items-center gap-2 text-sm mb-4 uppercase tracking-wider">
    <span :class="isAgain ? 'text-red-400 dark:text-red-400' : 'text-surface-400 dark:text-surface-300'">
      {{ statusLabel }}
    </span>
    <span
      v-if="isLeech"
      class="flex items-center gap-1 text-xs font-medium px-1.5 py-0.5 rounded bg-amber-100 dark:bg-amber-900/40 text-amber-700 dark:text-amber-300"
    >
      <i class="pi pi-exclamation-triangle !text-[10px]" />
      Leech
    </span>
  </div>
</template>
