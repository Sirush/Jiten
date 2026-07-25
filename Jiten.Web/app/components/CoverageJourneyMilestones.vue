<script setup lang="ts">
  import type { JourneyMilestone, JourneyGranularity } from '~/types';

  const props = withDefaults(
    defineProps<{
      milestones: JourneyMilestone[];
      metric?: 'total' | 'unique';
      granularity?: JourneyGranularity;
    }>(),
    { metric: 'total', granularity: 'monthly' }
  );

  const rows = computed(() =>
    props.milestones
      .filter((m) => m.unique === (props.metric === 'unique'))
      .sort((a, b) => a.threshold - b.threshold)
      .map((m) => ({ threshold: m.threshold, label: formatBucketDated(m.reachedAt, props.granularity) }))
  );
</script>

<template>
  <div v-if="rows.length" class="flex flex-wrap gap-2">
    <div
      v-for="row in rows"
      :key="row.threshold"
      class="flex items-baseline gap-2 rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-800/40 px-3 py-2"
    >
      <span class="font-bold tabular-nums text-primary-600 dark:text-primary-400">{{ row.threshold }}%</span>
      <span class="text-sm text-gray-500 dark:text-gray-400">{{ row.label }}</span>
    </div>
  </div>
  <p v-else class="text-sm text-gray-500 dark:text-gray-400">No milestones reached yet.</p>
</template>
