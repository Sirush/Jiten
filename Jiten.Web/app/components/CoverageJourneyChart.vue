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
  import { coverageToTail, coverageWindow, coverageTickDecimals, formatCoverageTick, tailWindow, tailTicks, type CoverageScale } from '~/utils/coverageAxis';

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
      // Draws bulk-declared words as their own baseline band instead of folding them into the curve.
      separatePrior?: boolean;
      // 'fit' zooms the axis to the plotted range, 'log' positions coverage by its shrinking remainder, 'full' is a fixed 0-100%; ignored in count mode.
      scale?: CoverageScale;
    }>(),
    {
      granularity: 'monthly',
      mode: 'coverage',
      metric: 'total',
      milestones: () => [],
      compact: false,
      height: '260px',
      tooltip: true,
      separatePrior: true,
      scale: 'fit',
    }
  );

  const MATURE = '#d20ca3';
  const MATURE_FILL = 'rgba(210, 12, 163, 0.35)';
  const COMBINED_FILL = 'rgba(210, 12, 163, 0.12)';
  // A solid block on an axis that does not start at 0 reads as a quantity; a tint keeps it reading as a trend.
  const MATURE_FILL_ZOOMED = 'rgba(210, 12, 163, 0.18)';
  const COMBINED_FILL_ZOOMED = 'rgba(210, 12, 163, 0.12)';
  const AXIS = '#6b7280';
  const PRIOR_LABEL = 'Already knew';
  const GRID = 'rgba(107, 114, 128, 0.15)';

  const isCount = computed(() => props.mode === 'count');
  const isTail = computed(() => props.scale === 'log' && !isCount.value);
  const isFull = computed(() => props.scale === 'full' && !isCount.value);
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

  const showPrior = computed(() => props.separatePrior && hasPriorKnowledge(props.points));

  const priorValues = computed(() => {
    if (isCount.value) return props.points.map((p) => p.priorKnownWords ?? 0);
    return (props.points as JourneyPoint[]).map((p) => (isUnique.value ? (p.priorUniqueCoverage ?? 0) : (p.priorCoverage ?? 0)));
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

  const plot = (values: number[]) => (isTail.value ? values.map(coverageToTail) : values);

  const yWindow = computed(() => {
    if (isCount.value) return null;
    if (isFull.value) return { min: 0, max: 100 };
    const values = [...matureValues.value, ...combinedValues.value];
    return isTail.value ? tailWindow(values) : coverageWindow(values);
  });

  const zoomed = computed(() => !isCount.value && !isFull.value && (yWindow.value?.min ?? 0) > (isTail.value ? coverageToTail(0) : 0));

  const yTicks = computed(() => (isTail.value && yWindow.value ? tailTicks(yWindow.value) : []));
  const linearDecimals = computed(() => (yWindow.value && !isTail.value ? coverageTickDecimals(yWindow.value) : 0));

  const priorDataset = computed(() => ({
    label: PRIOR_LABEL,
    data: plot(priorValues.value),
    borderColor: AXIS,
    backgroundColor: AXIS,
    borderDash: [4, 4],
    pointRadius: 0,
    pointHitRadius: 0,
    borderWidth: 1.5,
    cubicInterpolationMode: 'monotone' as const,
    fill: false as const,
    order: 0,
  }));

  const chartData = computed<ChartData<'line'>>(() => ({
    labels: labels.value,
    datasets: [
      ...(showPrior.value ? [priorDataset.value] : []),
      {
        label: 'Mature + young',
        data: plot(combinedValues.value),
        borderColor: 'transparent',
        backgroundColor: zoomed.value ? COMBINED_FILL_ZOOMED : COMBINED_FILL,
        pointRadius: 0,
        pointHitRadius: 0,
        borderWidth: 0,
        cubicInterpolationMode: 'monotone' as const,
        fill: 'start',
        order: 2,
      },
      {
        label: 'Mature',
        data: plot(matureValues.value),
        borderColor: MATURE,
        backgroundColor: zoomed.value ? MATURE_FILL_ZOOMED : MATURE_FILL,
        pointBackgroundColor: MATURE,
        pointBorderColor: MATURE,
        pointRadius: props.compact ? 0 : props.points.map((_, i) => (milestoneIndexByPoint.value.has(i) ? 5 : 0)),
        pointStyle: props.points.map((_, i) => (milestoneIndexByPoint.value.has(i) ? 'rectRot' : 'circle')),
        pointHoverRadius: props.compact ? 4 : 5,
        borderWidth: 2,
        cubicInterpolationMode: 'monotone' as const,
        fill: 'start',
        order: 1,
      },
    ],
  }));

  function formatValue(value: number): string {
    return isCount.value ? value.toLocaleString() : `${value.toFixed(1)}%`;
  }

  function rawValue(label: string | undefined, index: number): number {
    const series = label === PRIOR_LABEL ? priorValues.value : label === 'Mature' ? matureValues.value : combinedValues.value;
    return series[index] ?? 0;
  }

  function formatTick(value: number | string): string {
    if (isCount.value) return Number(value).toLocaleString();
    return formatCoverageTick(Number(value), linearDecimals.value);
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
        // Chart.js clips the tooltip to the canvas, and a sparkline is too short for a third row. The
        // baseline is flat across the whole series anyway, so it is the row a hover least needs.
        filter: (item) => !props.compact || item.dataset.label !== PRIOR_LABEL,
        callbacks: {
          title: (items) => {
            const point = props.points[items[0]?.dataIndex ?? 0];
            return point ? formatBucketLong(point.date, props.granularity) : '';
          },
          label: (ctx) => `${ctx.dataset.label}: ${formatValue(rawValue(ctx.dataset.label, ctx.dataIndex))}`,
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
        beginAtZero: isCount.value ? !showPrior.value : undefined,
        min: yWindow.value?.min,
        max: yWindow.value?.max,
        grace: isCount.value ? '10%' : undefined,
        grid: { color: GRID },
        afterBuildTicks: isTail.value ? (scale) => (scale.ticks = yTicks.value.map((t) => ({ value: t.value }))) : undefined,
        ticks: {
          color: AXIS,
          font: { size: 10 },
          precision: isCount.value || isFull.value ? 0 : undefined,
          includeBounds: isCount.value || isFull.value,
          callback: (v, i) => (isTail.value ? (yTicks.value[i]?.label ?? '') : formatTick(v)),
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
