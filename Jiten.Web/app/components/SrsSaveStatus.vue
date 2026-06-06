<script setup lang="ts">
  defineProps<{ state: 'idle' | 'saving' | 'saved' | 'error' }>();
  const emit = defineEmits<{ retry: [] }>();
</script>

<template>
  <span v-if="state !== 'idle'" class="inline-flex items-center gap-1.5 text-sm font-normal">
    <template v-if="state === 'saving'">
      <ProgressSpinner style="width: 14px; height: 14px" stroke-width="6" />
      <span class="text-surface-500">Saving…</span>
    </template>
    <template v-else-if="state === 'saved'">
      <i class="pi pi-check text-green-500 text-xs" />
      <span class="text-surface-500">All changes saved</span>
    </template>
    <template v-else-if="state === 'error'">
      <i class="pi pi-exclamation-circle text-red-500 text-xs" />
      <button type="button" class="text-red-500 hover:underline cursor-pointer" @click="emit('retry')">Save failed - retry</button>
    </template>
  </span>
</template>
