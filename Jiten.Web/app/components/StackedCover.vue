<script setup lang="ts">
  const props = defineProps<{ covers: string[]; alt: string }>();

  const LAYER_COUNT = 3;

  const layers = computed(() =>
    Array.from({ length: LAYER_COUNT }, (_, depth) => ({
      depth,
      cover: props.covers[depth] ?? null,
      style: {
        left: `calc(var(--stack-offset) * ${depth})`,
        bottom: `calc(var(--stack-offset) * ${depth})`,
        width: `calc(100% - var(--stack-offset) * ${LAYER_COUNT - 1})`,
        height: `calc(100% - var(--stack-offset) * ${LAYER_COUNT - 1})`,
        zIndex: LAYER_COUNT - depth,
      },
    }))
  );
</script>

<template>
  <div class="relative isolate w-full h-full [--stack-offset:4px] sm:[--stack-offset:6px]">
    <div
      v-for="layer in layers"
      :key="layer.depth"
      class="absolute rounded-sm overflow-hidden ring-1 ring-surface-0 dark:ring-surface-900"
      :class="layer.cover ? 'bg-surface-100 dark:bg-surface-700' : 'bg-surface-200 dark:bg-surface-600'"
      :style="layer.style"
    >
      <img
        v-if="layer.cover"
        :src="layer.cover"
        :alt="layer.depth === 0 ? alt : ''"
        class="w-full h-full object-cover"
        :class="{ 'brightness-85': layer.depth > 0 }"
      />
    </div>
  </div>
</template>
