<script setup lang="ts">
  import type { SiteStatsHeatmap } from '~/types';
  import { heatStep } from '~/utils/siteStats';

  const props = defineProps<{
    heatmap: SiteStatsHeatmap;
  }>();

  const DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
  const HOURS = Array.from({ length: 24 }, (_, h) => h);
  const STEP_CLASSES = [
    'bg-surface-100 dark:bg-surface-800',
    'bg-primary-100 dark:bg-primary-950',
    'bg-primary-200 dark:bg-primary-900',
    'bg-primary-300 dark:bg-primary-700',
    'bg-primary-500 dark:bg-primary-500',
    'bg-primary-700 dark:bg-primary-300',
  ];

  const max = computed(() => Math.max(0, ...props.heatmap.reviews.flat()));

  const busiest = computed(() => {
    let best = { day: 0, hour: 0, value: -1 };
    props.heatmap.reviews.forEach((row, day) =>
      row.forEach((value, hour) => {
        if (value > best.value) best = { day, hour, value };
      })
    );
    return best;
  });

  const summary = computed(() => `Reviews by weekday and hour. The busiest hour is ${DAYS[busiest.value.day]} at ${busiest.value.hour}:00.`);
</script>

<template>
  <StatsSection
    v-if="max > 0"
    title="When people review"
    subtitle="Last 90 days, in their own time zone"
    :note="`From the ${heatmap.learners.toLocaleString('en-US')} learners who have set a study time zone.`"
  >
    <div class="overflow-x-auto -mx-1 px-1">
      <div class="grid gap-[2px] min-w-[30rem] text-[11px] text-gray-500 dark:text-gray-400" style="grid-template-columns: 2.25rem repeat(24, minmax(0, 1fr))" role="img" :aria-label="summary">
        <span />
        <span v-for="hour in HOURS" :key="`h${hour}`" class="text-center" aria-hidden="true">{{ hour % 3 === 0 ? hour : '' }}</span>
        <template v-for="(row, day) in heatmap.reviews" :key="day">
          <span class="self-center" aria-hidden="true">{{ DAYS[day]!.slice(0, 3) }}</span>
          <span
            v-for="(value, hour) in row"
            :key="hour"
            class="aspect-square rounded-[2px]"
            :class="STEP_CLASSES[heatStep(value, max)]"
            :title="`${DAYS[day]} ${hour}:00, ${value.toLocaleString('en-US')} reviews`"
          />
        </template>
      </div>
    </div>
    <div class="flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400" aria-hidden="true">
      <span>Fewer</span>
      <span v-for="(cls, i) in STEP_CLASSES.slice(1)" :key="i" class="w-3.5 h-3.5 rounded-[2px]" :class="cls" />
      <span>More</span>
    </div>
  </StatsSection>
</template>
