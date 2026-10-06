<script setup lang="ts">
  const revealed = defineModel<boolean>({ required: true });
</script>

<template>
  <button
    type="button"
    class="inline-flex items-center justify-center p-1.5 -m-1.5 transition-colors cursor-pointer"
    :class="revealed ? 'text-primary-500' : 'text-surface-400 hover:text-primary-500'"
    :title="revealed ? 'Hide details' : 'Show details'"
    :aria-label="revealed ? 'Hide details' : 'Show details'"
    :aria-pressed="revealed"
    @pointerdown.stop
    @click.stop="revealed = !revealed"
  >
    <i class="pi pi-ellipsis-v text-[0.65rem]" />
  </button>
</template>
