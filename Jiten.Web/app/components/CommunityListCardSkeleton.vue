<script setup lang="ts">
  withDefaults(defineProps<{ compact?: boolean }>(), { compact: false });
</script>

<template>
  <div
    class="flex flex-col overflow-hidden rounded-lg border border-surface-200 bg-surface-0 dark:border-surface-700 dark:bg-surface-900"
    :class="compact ? 'w-64 shrink-0 lg:w-[calc((100%-0.75rem)/2)]' : 'w-full'"
    aria-hidden="true"
  >
    <Skeleton class="aspect-[2/1]" height="auto" border-radius="0" />
    <div class="flex flex-1 flex-col gap-1.5 p-3">
      <Skeleton width="70%" height="1.375rem" />
      <Skeleton width="40%" height="1.25rem" />
      <Skeleton width="4.5rem" height="1.125rem" />
      <div class="mt-auto flex items-center gap-3 pt-1">
        <Skeleton width="2.5rem" height="1.25rem" />
        <Skeleton width="2.5rem" height="1.25rem" />
        <Skeleton width="5rem" height="1.25rem" class="ml-auto" />
      </div>
    </div>
  </div>
</template>
