<script setup lang="ts">
import { computed } from 'vue';
import { Bar } from 'vue-chartjs';
import {
  Chart as ChartJS,
  CategoryScale,
  LinearScale,
  BarElement,
  LineElement,
  PointElement,
  Tooltip,
  Legend,
  type ChartOptions,
  type ChartData,
} from 'chart.js';
import type { ProgressionSegmentDto } from '~/types';
import { MediaType } from '~/types';

ChartJS.register(CategoryScale, LinearScale, BarElement, LineElement, PointElement, Tooltip, Legend);

const props = defineProps<{
  progression: ProgressionSegmentDto[];
  overallDifficulty: number;
  mediaType?: MediaType;
  isParentDeck?: boolean;
  usePercentage?: boolean;
}>();

const getMediaTypePrefix = (mediaType?: MediaType): string => {
  switch (mediaType) {
    case MediaType.Anime:
    case MediaType.Drama:
    case MediaType.Movie:
    case MediaType.Audio:
      return 'Ep';
    case MediaType.Novel:
    case MediaType.NonFiction:
    case MediaType.WebNovel:
    case MediaType.Manga:
      return 'Vol';
    case MediaType.VisualNovel:
    case MediaType.VideoGame:
      return 'Part';
    default:
      return '#';
  }
};

const AXIS = '#6b7280';
const GRID = 'rgba(107, 114, 128, 0.15)';

// Colours matching DifficultyDisplay.vue (Tailwind color values)
const difficultyColours = [
  'rgba(21, 128, 61, 0.8)',   // green-700 - Beginner
  'rgba(34, 197, 94, 0.8)',   // green-500 - Easy
  'rgba(6, 182, 212, 0.8)',   // cyan-500 - Moderate
  'rgba(217, 119, 6, 0.8)',   // amber-600 - Hard
  'rgba(220, 38, 38, 0.8)',   // red-600 - Expert
  'rgba(220, 38, 38, 0.8)',   // red-600 - Insane
];

const getDifficultyColour = (difficulty: number): string => {
  const index = Math.min(Math.max(Math.floor(difficulty), 0), difficultyColours.length - 1);
  return difficultyColours[index];
};

const chartData = computed<ChartData<'bar'>>(() => {
  const sorted = [...props.progression].sort((a, b) => a.segment - b.segment);

  const labels = sorted.map((s) => {
    if (!props.isParentDeck || s.childStartOrder == null || s.childEndOrder == null) {
      return `${s.segment * 10}%`;
    }

    const prefix = getMediaTypePrefix(props.mediaType);
    if (s.childStartOrder === s.childEndOrder) {
      return `${prefix} ${s.childStartOrder}`;
    }
    return `${prefix} ${s.childStartOrder}-${s.childEndOrder}`;
  });

  const rawData = sorted.map((s) => s.difficulty);
  const rawPeakData = sorted.map((s) => s.peak);
  const data = props.usePercentage ? rawData.map((d) => d * 20) : rawData;
  const peakData = props.usePercentage ? rawPeakData.map((d) => d * 20) : rawPeakData;
  const colours = rawData.map((d) => getDifficultyColour(d));
  const averageValue = props.usePercentage ? props.overallDifficulty * 20 : props.overallDifficulty;
  const averageData = sorted.map(() => averageValue);

  return {
    labels,
    datasets: [
      {
        label: 'Difficulty',
        data,
        backgroundColor: colours,
        borderColor: colours.map((c) => c.replace('0.8', '1')),
        borderWidth: 1,
        borderRadius: 2,
        order: 3,
      },
      {
        type: 'line' as const,
        label: 'Peak Difficulty',
        data: peakData,
        borderColor: 'rgba(210, 12, 163, 1)',
        borderWidth: 2,
        borderDash: [6, 4],
        pointRadius: 0,
        pointHoverRadius: 0,
        fill: false,
        order: 1,
      },
      {
        type: 'line' as const,
        label: 'Overall Average',
        data: averageData,
        borderColor: 'rgba(6, 182, 212, 1)',
        borderWidth: 2,
        borderDash: [6, 4],
        pointRadius: 0,
        pointHoverRadius: 0,
        fill: false,
        order: 2,
      },
    ],
  };
});

const chartOptions = computed<ChartOptions<'bar'>>(() => ({
  responsive: true,
  maintainAspectRatio: true,
  aspectRatio: 2,
  interaction: {
    mode: 'index',
    intersect: false,
  },
  plugins: {
    legend: {
      display: true,
      position: 'top',
      labels: {
        usePointStyle: true,
        padding: 16,
        color: AXIS,
      },
    },
    datalabels: { display: false },
    tooltip: {
      enabled: true,
      backgroundColor: 'rgba(0, 0, 0, 0.8)',
      titleColor: '#fff',
      bodyColor: '#fff',
      borderColor: '#d20ca3',
      borderWidth: 1,
      padding: 12,
      displayColors: true,
      callbacks: {
        title: (context) => {
          return `Progress: ${context[0].label}`;
        },
        label: (context) => {
          const value = context.raw as number;
          const label = context.dataset.label;
          const formatted = props.usePercentage ? `${value.toFixed(0)}%` : `${value.toFixed(2)}/5`;
          if (label === 'Peak Difficulty') {
            return `Peak: ${formatted}`;
          }
          if (label === 'Overall Average') {
            return `Overall: ${formatted}`;
          }
          return `Difficulty: ${formatted}`;
        },
      },
    },
  },
  scales: {
    x: {
      type: 'category',
      title: {
        display: true,
        text: props.isParentDeck ? 'Progress Through Series' : 'Progress Through Text',
        font: { size: 14, weight: 'bold' },
        color: AXIS,
      },
      grid: { display: false },
      ticks: { color: AXIS },
    },
    y: {
      type: 'linear',
      title: {
        display: true,
        text: 'Difficulty',
        font: { size: 14, weight: 'bold' },
        color: AXIS,
      },
      min: 0,
      max: props.usePercentage ? 100 : 5,
      ticks: {
        color: AXIS,
        stepSize: props.usePercentage ? 10 : 0.5,
        callback: (value) => props.usePercentage ? `${value}%` : value,
      },
      grid: { color: GRID },
    },
  },
}));
</script>

<template>
  <div class="chart-container">
    <div class="difficulty-chart-wrapper">
      <Bar v-if="progression && progression.length > 0" :data="chartData" :options="chartOptions" />
      <div v-else class="text-center text-gray-500 dark:text-gray-400 py-8">No difficulty progression data available</div>
    </div>
  </div>
</template>

<style scoped>
.chart-container {
  width: 100%;
  border-radius: var(--radius-lg);
  padding-top: 1rem;
}

.difficulty-chart-wrapper {
  width: 100%;
  min-height: 400px;
  padding: 0 1rem 1rem 1rem;
}
</style>
