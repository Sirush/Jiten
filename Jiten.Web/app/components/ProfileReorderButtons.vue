<script setup lang="ts">
  defineProps<{
    label: string;
    index: number;
    count: number;
    disabled: boolean;
    removeVerb: string;
  }>();

  /** A delta of -1 or 1 moves the item; null removes it. */
  const emit = defineEmits<{ move: [delta: number | null] }>();
</script>

<template>
  <Button icon="pi pi-arrow-up" text rounded severity="secondary" :disabled="index === 0 || disabled" :aria-label="`Move ${label} up`" @click="emit('move', -1)" />
  <Button
    icon="pi pi-arrow-down"
    text
    rounded
    severity="secondary"
    :disabled="index === count - 1 || disabled"
    :aria-label="`Move ${label} down`"
    @click="emit('move', 1)"
  />
  <Button icon="pi pi-times" text rounded severity="secondary" :disabled="disabled" :aria-label="`${removeVerb} ${label}`" @click="emit('move', null)" />
</template>
