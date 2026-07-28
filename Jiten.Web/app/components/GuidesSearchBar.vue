<script setup lang="ts">
  // Visible search field shown on every guides page; opens the GuidesSearch command palette.
  const { open } = useGuidesSearch();
</script>

<template>
  <button
    type="button"
    aria-label="Search guides"
    class="flex w-full items-center gap-2 rounded-lg border border-surface-200 bg-surface-0 px-3 py-2.5 text-left text-surface-400 transition hover:border-primary-400 dark:border-surface-700 dark:bg-surface-900"
    @click="open()"
  >
    <Icon name="material-symbols-light:search" class="text-xl" />
    <span class="flex-1">Search guides…</span>
    <kbd class="hidden sm:inline rounded border border-surface-300 px-1.5 py-0.5 text-[10px] dark:border-surface-600">Ctrl K</kbd>
  </button>
</template>
