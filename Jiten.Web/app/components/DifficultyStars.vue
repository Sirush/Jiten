<script setup lang="ts">
  const props = defineProps<{
    difficulty: number;
  }>();

  const STARS = ['★☆☆☆☆', '★★☆☆☆', '★★★☆☆', '★★★★☆', '★★★★★', '★★★★★'];
  const COLORS = ['text-green-700 dark:text-green-300', 'text-green-500', 'text-cyan-500', 'text-amber-600', 'text-orange-600', 'text-red-600'];

  const { paletteId, textClass } = useDifficultyColours();
  const colourClass = computed(() => (paletteId.value === 'default' ? COLORS[props.difficulty] : textClass(props.difficulty)));
</script>

<template>
  <span role="img" :aria-label="`Difficulty: ${difficulty} out of 5`">
    <span v-if="STARS[difficulty]" class="tabular-nums" :class="colourClass" aria-hidden="true">{{ STARS[difficulty] }}</span>
  </span>
</template>
