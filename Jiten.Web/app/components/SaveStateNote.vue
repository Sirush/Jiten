<script setup lang="ts">
  import type { SaveState } from '~/composables/useSaveQueue';

  defineProps<{ state: SaveState }>();
</script>

<template>
  <span role="status" aria-live="polite" class="inline-flex items-center gap-1.5 text-xs text-gray-600 dark:text-gray-300 min-h-4">
    <template v-if="state === 'saving'"><i class="pi pi-spin pi-spinner text-[10px]" aria-hidden="true" />Saving…</template>
    <template v-else-if="state === 'saved'"><i class="pi pi-check text-[10px] text-green-600 dark:text-green-400" aria-hidden="true" />Saved</template>
  </span>
</template>
