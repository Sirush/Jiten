<script setup lang="ts">
  defineProps<{ message: string; loading?: boolean }>();

  const emit = defineEmits<{ dismiss: []; complete: [] }>();
</script>

<template>
  <div
    role="status"
    class="flex flex-wrap items-center justify-between gap-2 rounded-md bg-green-50 dark:bg-green-900/30 px-3 py-2 text-sm text-green-800 dark:text-green-200"
  >
    <span>{{ message }}</span>
    <span class="flex justify-end gap-1 ml-auto">
      <Button label="Not yet" text size="small" severity="secondary" @click="emit('dismiss')" />
      <Button label="Completed" icon="pi pi-check" size="small" severity="success" :loading="loading" @click="emit('complete')" />
    </span>
  </div>
</template>
