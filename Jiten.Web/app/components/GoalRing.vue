<script setup lang="ts">
  const props = withDefaults(defineProps<{
    reviewsDone: number;
    reviewsTarget: number;
    newDone: number;
    newTarget: number;
    totalToStudy: number;
    size?: number;
  }>(), {
    size: 96,
  });

  const OUTER_R = 34;
  const INNER_R = 28;
  const STROKE = 6;
  const CENTER = 40;
  const OUTER_C = 2 * Math.PI * OUTER_R;
  const INNER_C = 2 * Math.PI * INNER_R;

  function frac(done: number, target: number) {
    if (target <= 0) return 0;
    return Math.min(1, done / target);
  }

  const reviewsFrac = computed(() => frac(props.reviewsDone, props.reviewsTarget));
  const newFrac = computed(() => frac(props.newDone, props.newTarget));

  const reviewsDash = computed(() => `${reviewsFrac.value * OUTER_C} ${OUTER_C}`);
  const newDash = computed(() => `${newFrac.value * INNER_C} ${INNER_C}`);

  const newMet = computed(() => props.newTarget > 0 && props.newDone >= props.newTarget);
  const reviewsMet = computed(() => props.reviewsTarget > 0 && props.reviewsDone >= props.reviewsTarget);
</script>

<template>
  <div class="flex items-center gap-3">
    <div class="relative flex-shrink-0" :style="{ width: `${size}px`, height: `${size}px` }">
      <svg viewBox="0 0 80 80" class="w-full h-full -rotate-90">
        <!-- Outer track (reviews) -->
        <circle :cx="CENTER" :cy="CENTER" :r="OUTER_R" fill="none" :stroke-width="STROKE"
          class="stroke-surface-200 dark:stroke-surface-700" />
        <!-- Outer arc (reviews) -->
        <circle :cx="CENTER" :cy="CENTER" :r="OUTER_R" fill="none" :stroke-width="STROKE" stroke-linecap="round"
          :stroke-dasharray="reviewsDash"
          class="transition-[stroke-dasharray] duration-700"
          :class="reviewsMet ? 'stroke-blue-500 dark:stroke-blue-400' : 'stroke-blue-400 dark:stroke-blue-500'" />
        <!-- Inner track (new) -->
        <circle :cx="CENTER" :cy="CENTER" :r="INNER_R" fill="none" :stroke-width="STROKE"
          class="stroke-surface-200 dark:stroke-surface-700" />
        <!-- Inner arc (new) -->
        <circle :cx="CENTER" :cy="CENTER" :r="INNER_R" fill="none" :stroke-width="STROKE" stroke-linecap="round"
          :stroke-dasharray="newDash"
          class="transition-[stroke-dasharray] duration-700"
          :class="newMet ? 'stroke-green-500 dark:stroke-green-400' : 'stroke-green-400 dark:stroke-green-500'" />
      </svg>
      <!-- Center label (total to study) -->
      <div class="absolute inset-0 flex items-center justify-center">
        <div class="flex flex-col items-center justify-center leading-none gap-0.5">
          <template v-if="totalToStudy > 0">
            <span class="text-xl font-bold tabular-nums text-surface-700 dark:text-surface-200">{{ totalToStudy }}</span>
            <span class="text-[10px] uppercase tracking-wide text-surface-400 dark:text-surface-500">to do</span>
          </template>
          <template v-else>
            <Icon name="material-symbols:check-circle" size="24" class="text-green-500 dark:text-green-400" />
            <span class="text-[10px] uppercase tracking-wide text-surface-400 dark:text-surface-500">done</span>
          </template>
        </div>
      </div>
    </div>
    <div class="flex flex-col gap-1 text-xs">
      <span class="text-[10px] font-semibold uppercase tracking-wide text-surface-400 dark:text-surface-500">Done today</span>
      <span class="flex items-center gap-1.5 text-surface-600 dark:text-surface-300">
        <span class="inline-block w-2 h-2 rounded-full bg-blue-400 dark:bg-blue-500"></span>
        <span class="tabular-nums font-medium">{{ reviewsDone }}</span> reviews
      </span>
      <span class="flex items-center gap-1.5 text-surface-600 dark:text-surface-300">
        <span class="inline-block w-2 h-2 rounded-full bg-green-400 dark:bg-green-500"></span>
        <span class="tabular-nums font-medium">{{ newDone }}</span> new
      </span>
    </div>
  </div>
</template>
