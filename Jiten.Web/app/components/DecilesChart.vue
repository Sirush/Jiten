<script setup lang="ts">
  import { computed } from 'vue';
  import { Bar } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, BarElement, LineElement, PointElement, Tooltip, Legend } from 'chart.js';
  import { getDifficultyChartColour, averageColour, formatDifficultyValue } from '~/utils/difficultyColours';

  ChartJS.register(CategoryScale, LinearScale, BarElement, LineElement, PointElement, Tooltip, Legend);

  const props = defineProps<{
    deciles: Record<string, number>;
    overallDifficulty: number;
    usePercentage?: boolean;
  }>();

  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';

  const chartData = computed(() => {
    const sortedEntries = Object.entries(props.deciles)
      .map(([percentage, difficulty]) => ({
        percentage,
        difficulty,
        sortKey: parseInt(percentage.replace('%', '')),
      }))
      .sort((a, b) => a.sortKey - b.sortKey);

    const labels = sortedEntries.map((e) => (e.percentage.includes('%') ? e.percentage : `${e.percentage}%`));
    const rawData = sortedEntries.map((e) => e.difficulty);
    const data = props.usePercentage ? rawData.map((d) => d * 20) : rawData;
    const colours = rawData.map((d) => getDifficultyChartColour(d));
    const averageValue = props.usePercentage ? props.overallDifficulty * 20 : props.overallDifficulty;
    const averageData = sortedEntries.map(() => averageValue);

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
          order: 2,
        },
        {
          type: 'line' as const,
          label: 'Overall Average',
          data: averageData,
          borderColor: averageColour,
          borderWidth: 2,
          borderDash: [6, 4],
          pointRadius: 0,
          pointHoverRadius: 0,
          fill: false,
          order: 1,
        },
      ],
    };
  });

  const chartOptions = computed(() => ({
    responsive: true,
    maintainAspectRatio: true,
    aspectRatio: 2.5,
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
            return `${context[0].label} of text at or below`;
          },
          label: (context) => {
            const value = context.raw as number;
            const label = context.dataset.label;
            const formatted = props.usePercentage ? `${value.toFixed(0)}%` : `${value.toFixed(2)}/5`;
            if (label === 'Overall Average') {
              return `Overall average: ${formatted}`;
            }
            return `Difficulty ${formatted}`;
          },
        },
      },
    },
    scales: {
      x: {
        type: 'category',
        title: {
          display: true,
          text: 'Percentage of Text',
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
          callback: (value) => (props.usePercentage ? `${value}%` : value),
        },
        grid: { color: GRID },
      },
    },
  }));
</script>

<template>
  <div class="deciles-chart-wrapper">
    <Bar v-if="deciles && Object.keys(deciles).length > 0" :data="chartData" :options="chartOptions" />
    <div v-else class="text-center text-gray-500 dark:text-gray-400 py-8">No deciles data available</div>
  </div>
</template>

<style scoped>
  .deciles-chart-wrapper {
    width: 100%;
    min-height: 300px;
  }
</style>
