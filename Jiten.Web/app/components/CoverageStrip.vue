<script setup lang="ts">
  const props = withDefaults(
    defineProps<{
      coverage: number;
      youngCoverage?: number;
      withTooltip?: boolean;
    }>(),
    { youngCoverage: 0, withTooltip: false }
  );

  const combinedCoverage = computed(() => Math.min(props.coverage + props.youngCoverage, 100));
  const fillColour = computed(() => getCoverageColour(combinedCoverage.value));
  const youngColour = computed(() => getCoverageColour(combinedCoverage.value, 0.45));
  const tooltipContent = computed(() => `You know or are learning ${combinedCoverage.value.toFixed(1)}% of the words in this media`);
</script>

<template>
  <div class="overflow-hidden" :class="withTooltip ? '' : 'pointer-events-none'" aria-hidden="true">
    <Tooltip v-if="withTooltip" :content="tooltipContent" block>
      <div class="relative h-1 bg-black/10 dark:bg-white/15">
        <div class="absolute inset-y-0 left-0" :style="{ width: combinedCoverage.toFixed(1) + '%', backgroundColor: youngColour }" />
        <div class="absolute inset-y-0 left-0" :style="{ width: coverage.toFixed(1) + '%', backgroundColor: fillColour }" />
      </div>
    </Tooltip>
    <div v-else class="relative h-1 bg-black/10 dark:bg-white/15">
      <div class="absolute inset-y-0 left-0" :style="{ width: combinedCoverage.toFixed(1) + '%', backgroundColor: youngColour }" />
      <div class="absolute inset-y-0 left-0" :style="{ width: coverage.toFixed(1) + '%', backgroundColor: fillColour }" />
    </div>
  </div>
</template>
