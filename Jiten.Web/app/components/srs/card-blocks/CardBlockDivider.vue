<script setup lang="ts">
  import type { CardLayoutBlock, DividerBlockOptions } from '~/types';
  import { dividerDefaults, resolveOptions } from './cardBlockOptions';

  const props = defineProps<{ block: CardLayoutBlock; side: 'front' | 'back' }>();
  const opts = computed(() => resolveOptions<DividerBlockOptions>(dividerDefaults, props.block.options));
  const label = computed(() => (opts.value.label ?? '').trim());
</script>

<template>
  <template v-if="opts.style === 'line'">
    <div v-if="label" class="my-4 flex items-center gap-2">
      <hr class="flex-1 border-surface-200 dark:border-surface-700" >
      <span class="text-xs text-surface-400 dark:text-surface-400">{{ label }}</span>
      <hr class="flex-1 border-surface-200 dark:border-surface-700" >
    </div>
    <hr v-else class="my-4 border-surface-200 dark:border-surface-700" >
  </template>
  <template v-else>
    <div v-if="label" class="h-6 flex items-center justify-center">
      <span class="text-xs text-surface-400 dark:text-surface-400">{{ label }}</span>
    </div>
    <div v-else class="h-6" />
  </template>
</template>
