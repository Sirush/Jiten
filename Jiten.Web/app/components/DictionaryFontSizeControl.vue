<script setup lang="ts">
  withDefaults(defineProps<{ showLabel?: boolean }>(), { showLabel: false });

  const MIN_SIZE = 12;
  const MAX_SIZE = 24;

  const store = useJitenStore();

  const fontSize = computed(() => store.customDictionaryFontSize);

  function adjust(delta: number) {
    store.customDictionaryFontSize = Math.min(MAX_SIZE, Math.max(MIN_SIZE, store.customDictionaryFontSize + delta));
  }
</script>

<template>
  <div class="flex items-center gap-2">
    <span v-if="showLabel" class="text-sm text-gray-600 dark:text-gray-300">Definition text size</span>
    <span v-if="showLabel" class="text-sm tabular-nums w-10">{{ fontSize }}px</span>
    <div class="flex items-center shrink-0">
      <button
        type="button"
        class="size-btn w-7 h-7 flex items-center justify-center rounded text-surface-500 dark:text-surface-400 hover:bg-surface-100 dark:hover:bg-surface-800 hover:text-surface-700 dark:hover:text-surface-200 disabled:opacity-40 disabled:pointer-events-none"
        aria-label="Decrease dictionary text size"
        :disabled="fontSize <= MIN_SIZE"
        @click.stop="adjust(-1)"
      >
        <Icon name="material-symbols:text-decrease" size="18" />
      </button>
      <button
        type="button"
        class="size-btn w-7 h-7 flex items-center justify-center rounded text-surface-500 dark:text-surface-400 hover:bg-surface-100 dark:hover:bg-surface-800 hover:text-surface-700 dark:hover:text-surface-200 disabled:opacity-40 disabled:pointer-events-none"
        aria-label="Increase dictionary text size"
        :disabled="fontSize >= MAX_SIZE"
        @click.stop="adjust(1)"
      >
        <Icon name="material-symbols:text-increase" size="18" />
      </button>
    </div>
  </div>
</template>

<style scoped>
.size-btn {
  transition: background-color 0.15s, color 0.15s;
}
</style>
