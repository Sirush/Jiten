<script setup lang="ts">
  import { Line } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, PointElement, LineElement, Filler, Tooltip, type ChartData, type ChartOptions } from 'chart.js';
  import type { SiteStatsMonthPoint } from '~/types';
  import { formatCompact, formatMonth } from '~/utils/siteStats';

  ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Filler, Tooltip);

  const props = defineProps<{
    points: SiteStatsMonthPoint[];
    label: string;
  }>();

  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';
  const LINE = '#a855f7';

  const chartData = computed<ChartData<'line'>>(() => ({
    labels: props.points.map((p, i) => formatMonth(p.month, i === 0 || p.month.slice(5, 7) === '01')),
    datasets: [
      {
        label: props.label,
        data: props.points.map((p) => p.total),
        borderColor: LINE,
        backgroundColor: 'rgba(168, 85, 247, 0.12)',
        pointBackgroundColor: LINE,
        pointRadius: props.points.map((_, i) => (i === props.points.length - 1 ? 4 : 0)),
        pointHoverRadius: 5,
        borderWidth: 2,
        fill: 'origin',
        tension: 0.25,
      },
    ],
  }));

  const chartOptions = computed<ChartOptions<'line'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      legend: { display: false },
      datalabels: { display: false },
      tooltip: {
        displayColors: false,
        callbacks: {
          title: (items) => formatMonth(props.points[items[0]!.dataIndex]!.month, true),
          label: (item) => `${props.label}: ${(item.raw as number).toLocaleString('en-US')}`,
        },
      },
    },
    scales: {
      x: { grid: { display: false }, ticks: { color: AXIS, autoSkip: true, maxRotation: 0, autoSkipPadding: 12 } },
      y: { beginAtZero: true, grid: { color: GRID }, ticks: { color: AXIS, maxTicksLimit: 5, callback: (v) => formatCompact(Number(v)) } },
    },
  }));
</script>

<template>
  <div class="h-48 sm:h-56">
    <ClientOnly>
      <Line :data="chartData" :options="chartOptions" :aria-label="label" role="img" />
    </ClientOnly>
  </div>
</template>
