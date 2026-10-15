<script setup lang="ts">
  import { Line } from 'vue-chartjs';
  import { Chart as ChartJS, LogarithmicScale, LinearScale, PointElement, LineElement, Filler, Tooltip, type ChartData, type ChartOptions } from 'chart.js';
  import type { SiteStatsFrequencyBand } from '~/types';
  import { bandPosition, describeShare, medianShareOfTopWords, rankLabel } from '~/utils/siteStats';

  ChartJS.register(LogarithmicScale, LinearScale, PointElement, LineElement, Filler, Tooltip);

  const props = defineProps<{
    bands: SiteStatsFrequencyBand[];
  }>();

  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';
  const LINE = '#9333ea';
  const TICKS = [100, 250, 500, 1000, 2000, 5000, 10000, 20000, 50000];
  const NARROW_TICKS = [100, 1000, 10000];
  const NARROW_CHART_WIDTH = 480;

  const topThousand = computed(() => medianShareOfTopWords(props.bands, 1000));

  const point = (band: SiteStatsFrequencyBand, value: number) => ({ x: bandPosition(band), y: value });

  const chartData = computed<ChartData<'line'>>(() => ({
    datasets: [
      {
        label: 'Upper quartile',
        data: props.bands.map((b) => point(b, b.p75)),
        borderWidth: 0,
        pointRadius: 0,
        pointHoverRadius: 0,
        backgroundColor: 'rgba(168, 85, 247, 0.2)',
        fill: '+1',
      },
      {
        label: 'Lower quartile',
        data: props.bands.map((b) => point(b, b.p25)),
        borderWidth: 0,
        pointRadius: 0,
        pointHoverRadius: 0,
        fill: false,
      },
      {
        label: 'Median learner',
        data: props.bands.map((b) => point(b, b.median)),
        borderColor: LINE,
        backgroundColor: LINE,
        borderWidth: 2.5,
        pointRadius: 3.5,
        pointHoverRadius: 5,
        fill: false,
      },
      {
        label: 'Top 10% of learners',
        data: props.bands.map((b) => point(b, b.p90)),
        borderColor: LINE,
        backgroundColor: LINE,
        borderWidth: 1.5,
        borderDash: [5, 4],
        pointRadius: 0,
        pointHoverRadius: 0,
        fill: false,
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
        filter: (item) => item.datasetIndex === 2,
        callbacks: {
          title: (items) => {
            const band = props.bands[items[0]!.dataIndex]!;
            return `Words ranked ${band.from.toLocaleString('en-US')} to ${band.to.toLocaleString('en-US')}`;
          },
          label: (item) => {
            const band = props.bands[item.dataIndex]!;
            return [
              `Median learner: ${Math.round(band.median)}%`,
              `Half of learners: ${Math.round(band.p25)}% to ${Math.round(band.p75)}%`,
              `Top 10% of learners: ${Math.round(band.p90)}% or more`,
            ];
          },
        },
      },
    },
    scales: {
      x: {
        type: 'logarithmic',
        min: bandPosition(props.bands[0]!),
        max: props.bands[props.bands.length - 1]!.to,
        title: { display: true, text: 'Frequency rank', color: AXIS },
        grid: { color: GRID },
        afterBuildTicks: (axis) => {
          const ticks = axis.chart.width < NARROW_CHART_WIDTH ? NARROW_TICKS : TICKS;
          axis.ticks = ticks.filter((t) => t >= axis.min && t <= axis.max).map((value) => ({ value }));
        },
        ticks: { color: AXIS, autoSkip: false, maxRotation: 0, callback: (v) => rankLabel(Number(v)) },
      },
      y: {
        min: 0,
        max: 100,
        grid: { color: GRID },
        ticks: { color: AXIS, stepSize: 25, callback: (v) => `${v}%` },
      },
    },
  }));
</script>

<template>
  <StatsSection
    v-if="bands.length > 1"
    title="Which words learners know"
    subtitle="How much of each frequency band learners active in the last 90 days know"
  >
    <p v-if="topThousand !== null" class="m-0">
      The median learner knows <b class="text-primary-700 dark:text-primary-300">{{ describeShare(topThousand) }}</b> of the 1,000 most common words.
    </p>
    <div class="h-56 sm:h-64">
      <ClientOnly>
        <Line :data="chartData" :options="chartOptions" role="img" aria-label="Share of each frequency band known by the median learner, the middle half of learners and the top 10% of learners" />
      </ClientOnly>
    </div>
    <div class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-600 dark:text-gray-400" aria-hidden="true">
      <span class="flex items-center gap-1.5"><span class="inline-block w-4 h-0.5 bg-primary-600" />Median learner</span>
      <span class="flex items-center gap-1.5"><span class="inline-block w-4 h-2.5 rounded-sm bg-primary-500/20" />Half of learners fall in this range</span>
      <span class="flex items-center gap-1.5"><span class="inline-block w-4 border-t-[1.5px] border-dashed border-primary-600" />Top 10% of learners</span>
    </div>
  </StatsSection>
</template>
