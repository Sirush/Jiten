<script setup lang="ts">
  import type { CardLayoutBlock, ConfusableReadingsBlockOptions } from '~/types';
  import { confusableReadingsDefaults, resolveOptions } from './cardBlockOptions';
  import { useCardContext } from './useCardContext';
  import CardBlockSpoiler from './CardBlockSpoiler.vue';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<ConfusableReadingsBlockOptions>(confusableReadingsDefaults, props.block.options));

  const { card, isPreview, sample } = useCardContext();

  const readings = computed<string[]>(() => (isPreview ? sample!.confusable : (card.value?.confusableReadings ?? [])));
</script>

<template>
  <CardBlockSpoiler v-if="readings.length" :enabled="opts.spoiler">
    <div class="mt-4 flex items-center gap-2 text-sm text-amber-700 dark:text-amber-400" @click.stop>
      <i class="pi pi-exclamation-triangle text-xs shrink-0" />
      <span>
        Do not confuse with:
        <template v-for="(cr, i) in readings" :key="i">
          <strong>{{ cr }}</strong
          ><span v-if="i < readings.length - 1">,&ensp;</span>
        </template>
      </span>
    </div>
  </CardBlockSpoiler>
</template>
