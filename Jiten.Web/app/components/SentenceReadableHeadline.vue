<script setup lang="ts">
  const props = defineProps<{
    readable: number;
    oneUnknown: number;
    total: number;
  }>();

  const percent = (count: number) => (props.total > 0 ? Math.floor((count * 100) / props.total) : 0);
</script>

<template>
  <div class="flex flex-wrap gap-x-10 gap-y-3">
    <div>
      <div class="text-3xl font-bold leading-none tabular-nums">{{ percent(readable) }}%</div>
      <div class="mt-1 text-sm text-gray-600 dark:text-gray-300">you can understand now (i+0)</div>
    </div>
    <div>
      <div class="text-3xl font-bold leading-none tabular-nums text-green-700 dark:text-green-400">{{ percent(readable + oneUnknown) }}%</div>
      <div class="mt-1 text-sm text-gray-600 dark:text-gray-300">with at most one new word (i+1)</div>
    </div>
  </div>
</template>
