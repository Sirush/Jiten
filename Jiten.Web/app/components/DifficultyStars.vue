<script setup lang="ts">
  defineProps<{
    difficulty: number;
  }>();

  const STARS = ['★☆☆☆☆', '★★☆☆☆', '★★★☆☆', '★★★★☆', '★★★★★', '★★★★★'];
  const COLORS = [
    'text-green-700 dark:text-green-300',
    'text-green-500',
    'text-cyan-500',
    'text-amber-600',
    'text-orange-600',
    'text-red-600',
  ];
</script>

<template>
  <span role="img" :aria-label="`Difficulty: ${difficulty} out of 5`">
    <span v-if="STARS[difficulty]" class="tabular-nums" :class="COLORS[difficulty]" aria-hidden="true">{{ STARS[difficulty] }}</span>
  </span>
</template>
