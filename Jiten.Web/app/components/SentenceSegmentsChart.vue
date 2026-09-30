<script setup lang="ts">
  import { Bar } from 'vue-chartjs';
  import { Chart as ChartJS, BarElement, CategoryScale, LinearScale, Tooltip, Legend, type ChartOptions, type ChartData } from 'chart.js';
  import type { SentenceSegmentBar } from '~/types';

  ChartJS.register(BarElement, CategoryScale, LinearScale, Tooltip, Legend);

  const props = defineProps<{
    segments: SentenceSegmentBar[];
    xTitle: string;
  }>();

  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';
  const READABLE = '#22c55e';
  const ONE_NEW = 'rgba(34, 197, 94, 0.6)';
  const TWO_NEW = 'rgba(34, 197, 94, 0.3)';

  const share = (count: number, total: number) => (total > 0 ? (count * 100) / total : 0);

  const chartData = computed<ChartData<'bar'>>(() => ({
    labels: props.segments.map((s) => s.label),
    datasets: [
      { label: 'i+0', data: props.segments.map((s) => share(s.readable, s.total)), backgroundColor: READABLE, stack: 'sentences' },
      { label: 'i+1', data: props.segments.map((s) => share(s.oneUnknown, s.total)), backgroundColor: ONE_NEW, stack: 'sentences' },
      { label: 'i+2', data: props.segments.map((s) => share(s.twoUnknown, s.total)), backgroundColor: TWO_NEW, stack: 'sentences' },
    ],
  }));

  const chartOptions = computed<ChartOptions<'bar'>>(() => ({
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
          title: (items) => props.segments[items[0]!.dataIndex]?.tooltipTitle ?? '',
          label: (item) => `${item.dataset.label}: ${Number(item.raw).toFixed(0)}%`,
          footer: (items) => `Out of ${(props.segments[items[0]!.dataIndex]?.total ?? 0).toLocaleString()} sentences`,
        },
      },
    },
    scales: {
      x: {
        stacked: true,
        grid: { display: false },
        ticks: { color: AXIS, autoSkip: true, maxRotation: 0 },
        title: { display: true, text: props.xTitle, color: AXIS },
      },
      y: { stacked: true, min: 0, max: 100, grid: { color: GRID }, ticks: { color: AXIS, callback: (v) => `${v}%` } },
    },
  }));
</script>

<template>
  <div class="relative h-64 sm:h-72">
    <Bar :data="chartData" :options="chartOptions" />
  </div>
</template>
