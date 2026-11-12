<script setup lang="ts">
  const props = defineProps<{ position: number; total: number; ready: boolean; prefix?: string }>();

  const percent = computed(() => (props.total > 0 ? Math.min(100, Math.round(((props.position - 1) / props.total) * 100)) : 0));
</script>

<template>
  <div class="flex items-center gap-4">
    <span class="text-sm text-gray-600 dark:text-gray-300 whitespace-nowrap tabular-nums">
      <template v-if="ready">{{ prefix ?? 'Word' }} {{ position }} of about {{ total }}</template>
      <template v-else>Loading words</template>
    </span>
    <ProgressBar :value="percent" :show-value="false" class="flex-1 !h-1.5" aria-label="Progress" />
  </div>
</template>
