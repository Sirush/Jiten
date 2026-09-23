<script setup lang="ts">
  import type { KanjiStrokes } from '~/types';

  const props = defineProps<{
    strokes: KanjiStrokes;
    character: string;
    showNumbers: boolean;
    speed: number;
    playKey: number;
  }>();

  const STROKE_SECONDS = 0.55;
  const DRAW_SECONDS = 0.5;

  const playing = ref(false);

  const numbers = computed(() =>
    props.strokes.paths
      .map((_, i) => ({ n: i + 1, x: props.strokes.numberPositions[i * 2], y: props.strokes.numberPositions[i * 2 + 1] }))
      .filter((p): p is { n: number; x: number; y: number } => p.x != null && p.y != null)
  );

  watch(
    () => props.playKey,
    () => {
      playing.value = true;
    },
    { immediate: props.playKey > 0 }
  );

  watch(
    () => props.strokes,
    () => {
      playing.value = false;
    }
  );

  const onStrokeDrawn = (index: number) => {
    if (index === props.strokes.paths.length - 1) playing.value = false;
  };
</script>

<template>
  <svg viewBox="0 0 109 109" role="img" :aria-label="`Stroke order for ${character}, ${strokes.paths.length} strokes`">
    <g class="text-surface-200 dark:text-surface-700" stroke="currentColor" stroke-width="0.5" stroke-dasharray="2 2">
      <line x1="54.5" y1="0" x2="54.5" y2="109" />
      <line x1="0" y1="54.5" x2="109" y2="54.5" />
    </g>

    <g v-if="playing" class="text-surface-200 dark:text-surface-700" fill="none" stroke="currentColor" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round">
      <path v-for="(d, i) in strokes.paths" :key="i" :d="d" />
    </g>

    <g :key="playKey" class="text-surface-900 dark:text-surface-0" fill="none" stroke="currentColor" stroke-width="3.5" stroke-linecap="round" stroke-linejoin="round">
      <path
        v-for="(d, i) in strokes.paths"
        :key="i"
        :d="d"
        pathLength="1"
        :class="{ 'stroke-draw': playing }"
        :style="playing ? { animationDelay: `${(i * STROKE_SECONDS) / speed}s`, animationDuration: `${DRAW_SECONDS / speed}s` } : undefined"
        @animationend="onStrokeDrawn(i)"
      />
    </g>

    <g v-if="showNumbers && !playing" class="text-primary-600 dark:text-primary-400" fill="currentColor" font-size="8" font-weight="600">
      <text v-for="p in numbers" :key="p.n" :x="p.x" :y="p.y">{{ p.n }}</text>
    </g>
  </svg>
</template>

<style scoped>
  .stroke-draw {
    stroke-dasharray: 1;
    animation: stroke-draw 0.5s linear both;
  }

  @keyframes stroke-draw {
    0% {
      stroke-dashoffset: 1;
      opacity: 0;
    }
    1% {
      opacity: 1;
    }
    100% {
      stroke-dashoffset: 0;
      opacity: 1;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .stroke-draw {
      animation-duration: 1ms !important;
    }
  }
</style>
