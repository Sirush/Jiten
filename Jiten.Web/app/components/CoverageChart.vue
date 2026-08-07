<script setup lang="ts">
import { ref, computed } from 'vue';
import { Line } from 'vue-chartjs';
import {
  Chart as ChartJS,
  LogarithmicScale,
  LinearScale,
  PointElement,
  LineElement,
  Tooltip,
  Legend,
  type ChartOptions,
  type ChartData,
} from 'chart.js';
import type { CurveDatum } from '~/types';

ChartJS.register(LogarithmicScale, LinearScale, PointElement, LineElement, Tooltip, Legend);

const props = defineProps<{
  curveData: CurveDatum[];
}>();

const isLogScale = ref(true);

const AXIS = '#6b7280';
const GRID = 'rgba(107, 114, 128, 0.15)';

const toggleScale = () => {
  isLogScale.value = !isLogScale.value;
};

const chartData = computed<ChartData<'line'>>(() => {
  const data = props.curveData.map((d) => ({
    x: d.rank,
    y: d.coverage,
  }));

  return {
    datasets: [
      {
        label: 'Coverage',
        data,
        borderColor: '#d20ca3',
        backgroundColor: 'rgba(210, 12, 163, 0.1)',
        pointBackgroundColor: '#d20ca3',
        pointBorderColor: '#d20ca3',
        pointRadius: 4,
        pointHoverRadius: 6,
        borderWidth: 2,
        cubicInterpolationMode: 'monotone' as const,
        fill: true,
        clip: false,
      },
    ],
  };
});

const chartOptions = computed<ChartOptions<'line'>>(() => ({
  responsive: true,
  maintainAspectRatio: true,
  aspectRatio: 2,
  interaction: {
    mode: 'nearest',
    intersect: false,
    axis: 'x',
  },
  plugins: {
    legend: { display: false },
    datalabels: {display:false},
    tooltip: {
      enabled: true,
      backgroundColor: 'rgba(0, 0, 0, 0.8)',
      titleColor: '#fff',
      bodyColor: '#fff',
      borderColor: '#d20ca3',
      borderWidth: 1,
      padding: 12,
      displayColors: false,
      callbacks: {
        title: (context) => {
          const item = context[0].raw as { x: number; y: number };
          return `Words learned: ${item.x.toLocaleString()}`;
        },
        label: (context) => {
          const item = context.raw as { x: number; y: number };
          const coverage = item.y;
          const formatted = coverage < 99.0 ? coverage.toFixed(0) : coverage.toFixed(1);
          return `Coverage: ${formatted}%`;
        },
      },
    },
  },
  scales: {
    x: {
      // Dynamically switch between 'logarithmic' and 'linear'
      type: isLogScale.value ? 'logarithmic' : 'linear',
      title: {
        display: true,
        text: isLogScale.value
          ? 'Number of Most Frequent Words Learned (Log Scale)'
          : 'Number of Most Frequent Words Learned (Linear Scale)',
        font: { size: 14, weight: 'bold' },
        color: AXIS,
      },
      grid: { color: GRID },
      ticks: {
        color: AXIS,
        callback: (value) => {
          const num = Number(value);

          if (isLogScale.value) {
            // LOG SCALE: Only show powers of 10 to prevent clutter
            if (
              num === 1 ||
              num === 10 ||
              num === 100 ||
              num === 1000 ||
              num === 10000 ||
              num === 100000
            ) {
              return num.toLocaleString();
            }
            return '';
          } else {
            // LINEAR SCALE: ChartJS handles frequency, we just format the string
            return num.toLocaleString();
          }
        }
      }
    },
    y: {
      type: 'linear',
      title: {
        display: true,
        text: 'Coverage (%)',
        font: { size: 14, weight: 'bold' },
        color: AXIS,
      },
      min: 0,
      max: 100,
      grid: { color: GRID },
      ticks: { color: AXIS },
    },
  },
}));
</script>

<template>
  <div class="chart-container">
    <div class="flex justify-between items-center mb-4 px-4">
      <h3 class="text-lg font-semibold text-gray-700 dark:text-gray-200">Vocabulary Coverage</h3>
      <Button
        :label="isLogScale ? 'Switch to Linear Scale' : 'Switch to Log Scale'"
        icon="pi pi-sort-alt"
        severity="secondary"
        size="small"
        outlined
        @click="toggleScale"
      />
    </div>

    <!-- Chart -->
    <div class="coverage-chart-wrapper">
      <Line v-if="curveData && curveData.length > 0" :data="chartData" :options="chartOptions" />
      <div v-else class="text-center text-gray-500 py-8">No coverage data available</div>
    </div>
  </div>
</template>

<style scoped>
.chart-container {
  width: 100%;
  border-radius: 8px;
  padding-top: 1rem;
}

.coverage-chart-wrapper {
  width: 100%;
  min-height: 400px;
  padding: 0 1rem 1rem 1rem;
}
</style>
