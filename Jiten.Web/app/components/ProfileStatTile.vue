<script setup lang="ts">
  defineProps<{ label: string; value: string }>();
</script>

<template>
  <div>
    <dt class="text-sm text-muted-color">{{ label }}</dt>
    <dd class="text-2xl font-bold tabular-nums text-primary-600 dark:text-primary-400">{{ value }}</dd>
  </div>
</template>
