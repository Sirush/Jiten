<script setup lang="ts">
  import { Line } from 'vue-chartjs';
  import { Chart as ChartJS, LinearScale, PointElement, LineElement, Tooltip, Legend, type ChartOptions, type ChartData } from 'chart.js';
  import type { SentenceProjectionPoint } from '~/types';

  ChartJS.register(LinearScale, PointElement, LineElement, Tooltip, Legend);

  const props = defineProps<{
    points: SentenceProjectionPoint[];
    total: number;
  }>();

  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';

  const share = (count: number) => (props.total > 0 ? (count * 100) / props.total : 0);

  // Starts just below today's share, so a title that is already 70% readable doesn't flatten the curve into the top of the chart.
  const yMin = computed(() => Math.max(0, Math.floor((share(props.points[0]?.greedy ?? 0) - 5) / 10) * 10));

  const chartData = computed<ChartData<'line'>>(() => ({
    datasets: [
      {
        label: 'Best order',
        data: props.points.map((p) => ({ x: p.words, y: share(p.greedy) })),
        borderColor: '#22c55e',
        backgroundColor: '#22c55e',
        pointRadius: 3,
        borderWidth: 2,
      },
      {
        label: 'By frequency in this title',
        data: props.points.map((p) => ({ x: p.words, y: share(p.byFrequency) })),
        borderColor: '#9ca3af',
        backgroundColor: '#9ca3af',
        borderDash: [6, 4],
        pointRadius: 3,
        borderWidth: 2,
      },
    ],
  }));

  const chartOptions = computed<ChartOptions<'line'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      legend: { display: true, position: 'bottom', labels: { color: AXIS, boxWidth: 12 } },
      datalabels: { display: false },
      tooltip: {
        backgroundColor: 'rgba(0, 0, 0, 0.8)',
        padding: 10,
        callbacks: {
          title: (items) => `After ${Number((items[0]!.raw as { x: number }).x).toLocaleString()} new words`,
          label: (item) => `${item.dataset.label}: ${(item.raw as { y: number }).y.toFixed(0)}% readable`,
        },
      },
    },
    scales: {
      x: { type: 'linear', min: 0, grid: { color: GRID }, ticks: { color: AXIS }, title: { display: true, text: 'New words learned', color: AXIS } },
      y: {
        min: yMin.value,
        max: 100,
        grid: { color: GRID },
        ticks: { color: AXIS, callback: (v) => `${v}%` },
        title: { display: true, text: 'Readable sentences', color: AXIS },
      },
    },
  }));
</script>

<template>
  <div class="relative h-64 sm:h-72">
    <Line :data="chartData" :options="chartOptions" />
  </div>
</template>
