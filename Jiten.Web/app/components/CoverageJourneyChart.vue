<script setup lang="ts">
  import { Line } from 'vue-chartjs';
  import {
    Chart as ChartJS,
    CategoryScale,
    LinearScale,
    PointElement,
    LineElement,
    Filler,
    Tooltip,
    Legend,
    type ChartOptions,
    type ChartData,
  } from 'chart.js';
  import type { JourneyPoint, GrowthPoint, JourneyMilestone, JourneyGranularity } from '~/types';

  ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Filler, Tooltip, Legend);

  const props = withDefaults(
    defineProps<{
      points: (JourneyPoint | GrowthPoint)[];
      granularity?: JourneyGranularity;
      // 'coverage' plots percentages of the deck; 'count' plots the number of words known.
      mode?: 'coverage' | 'count';
      metric?: 'total' | 'unique';
      milestones?: JourneyMilestone[];
      compact?: boolean;
      height?: string;
      tooltip?: boolean;
    }>(),
    {
      granularity: 'monthly',
      mode: 'coverage',
      metric: 'total',
      milestones: () => [],
      compact: false,
      height: '260px',
      tooltip: true,
    }
  );

  const MATURE = '#d20ca3';
  const MATURE_FILL = 'rgba(210, 12, 163, 0.35)';
  const COMBINED_FILL = 'rgba(210, 12, 163, 0.12)';
  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';

  const isCount = computed(() => props.mode === 'count');
  const isUnique = computed(() => props.metric === 'unique');

  const labels = computed(() => props.points.map((p) => formatBucketAxis(p.date, props.granularity)));

  const matureValues = computed(() => {
    if (isCount.value) return props.points.map((p) => p.knownWords);
    return (props.points as JourneyPoint[]).map((p) => (isUnique.value ? p.uniqueCoverage : p.coverage));
  });

  const combinedValues = computed(() => {
    if (isCount.value) return props.points.map((p) => p.knownWordsCombined);
    return (props.points as JourneyPoint[]).map((p) => (isUnique.value ? p.combinedUniqueCoverage : p.combinedCoverage));
  });

  // Markers and the dated milestone list below the chart must not disagree about which bucket crossed.
  const milestoneIndexByPoint = computed(() => {
    const indices = new Set<number>();
    if (isCount.value) return indices;
    for (const milestone of props.milestones) {
      if (milestone.unique !== isUnique.value) continue;
      const index = props.points.findIndex((p) => p.date === milestone.reachedAt);
      if (index >= 0) indices.add(index);
    }
    return indices;
  });

  const chartData = computed<ChartData<'line'>>(() => ({
    labels: labels.value,
    datasets: [
      {
        label: 'Mature + young',
        data: combinedValues.value,
        borderColor: 'transparent',
        backgroundColor: COMBINED_FILL,
        pointRadius: 0,
        pointHitRadius: 0,
        borderWidth: 0,
        tension: 0.4,
        fill: 'origin',
        order: 2,
      },
      {
        label: 'Mature',
        data: matureValues.value,
        borderColor: MATURE,
        backgroundColor: MATURE_FILL,
        pointBackgroundColor: MATURE,
        pointBorderColor: MATURE,
        pointRadius: props.compact ? 0 : props.points.map((_, i) => (milestoneIndexByPoint.value.has(i) ? 5 : 0)),
        pointStyle: props.points.map((_, i) => (milestoneIndexByPoint.value.has(i) ? 'rectRot' : 'circle')),
        pointHoverRadius: props.compact ? 4 : 5,
        borderWidth: 2,
        tension: 0.4,
        fill: 'origin',
        order: 1,
      },
    ],
  }));

  function formatValue(value: number): string {
    return isCount.value ? value.toLocaleString() : `${value.toFixed(1)}%`;
  }

  const chartOptions = computed<ChartOptions<'line'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    animation: props.compact ? false : undefined,
    interaction: { mode: 'index', intersect: false },
    // Decorative copies of the chart take no pointer events at all, so no hover dot appears either.
    events: props.tooltip ? undefined : [],
    // The compact sparkline draws to its own edges; without this the hover dot is clipped in half.
    layout: { padding: props.compact ? 4 : 0 },
    plugins: {
      datalabels: { display: false },
      legend: {
        display: !props.compact,
        position: 'bottom',
        labels: { usePointStyle: true, padding: 12, boxWidth: 8, font: { size: 11 }, color: AXIS },
      },
      tooltip: {
        enabled: props.tooltip,
        callbacks: {
          title: (items) => {
            const point = props.points[items[0]?.dataIndex ?? 0];
            return point ? formatBucketLong(point.date, props.granularity) : '';
          },
          label: (ctx) => `${ctx.dataset.label}: ${formatValue(ctx.raw as number)}`,
        },
      },
    },
    scales: {
      x: {
        display: !props.compact,
        grid: { display: false },
        ticks: { color: AXIS, font: { size: 10 }, autoSkip: true, maxTicksLimit: 10, maxRotation: 45 },
      },
      y: {
        display: !props.compact,
        beginAtZero: true,
        max: isCount.value ? undefined : 100,
        grace: isCount.value ? '10%' : undefined,
        grid: { color: GRID },
        ticks: {
          color: AXIS,
          font: { size: 10 },
          precision: 0,
          callback: (v) => (isCount.value ? Number(v).toLocaleString() : `${v}%`),
        },
      },
    },
  }));
</script>

<template>
  <div :style="{ height }">
    <Line :data="chartData" :options="chartOptions" />
  </div>
</template>
