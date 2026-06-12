<script setup lang="ts">
  import { Bar } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, BarElement, Tooltip, type ChartOptions, type ChartData } from 'chart.js';

  ChartJS.register(CategoryScale, LinearScale, BarElement, Tooltip);

  const props = withDefaults(
    defineProps<{
      buckets: number[];
      labels: string[];
      colorMode?: 'difficulty' | 'retrievability' | 'neutral';
      title: string;
      subtitle?: string;
    }>(),
    {
      colorMode: 'neutral',
      subtitle: undefined,
    }
  );

  const hasData = computed(() => props.buckets.some((b) => b > 0));

  // Interpolate green ↔ red through amber, matching the Anki FSRS histogram feel.
  function lerp(a: number, b: number, t: number): number {
    return Math.round(a + (b - a) * t);
  }
  function gradientColor(t: number): string {
    // t = 0 → green, 0.5 → amber, 1 → red.
    const green = [16, 185, 129];
    const amber = [245, 158, 11];
    const red = [239, 68, 68];
    let c: number[];
    if (t < 0.5) {
      const u = t / 0.5;
      c = [lerp(green[0]!, amber[0]!, u), lerp(green[1]!, amber[1]!, u), lerp(green[2]!, amber[2]!, u)];
    } else {
      const u = (t - 0.5) / 0.5;
      c = [lerp(amber[0]!, red[0]!, u), lerp(amber[1]!, red[1]!, u), lerp(amber[2]!, red[2]!, u)];
    }
    return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
  }

  const barColors = computed(() => {
    const n = props.buckets.length;
    return props.buckets.map((_, i) => {
      const t = n <= 1 ? 0 : i / (n - 1);
      if (props.colorMode === 'difficulty') return gradientColor(t); // green (easy) → red (hard)
      if (props.colorMode === 'retrievability') return gradientColor(1 - t); // red (low) → green (high)
      return 'rgba(168, 85, 247, 0.7)';
    });
  });

  const chartData = computed<ChartData<'bar'>>(() => ({
    labels: props.labels,
    datasets: [
      {
        data: props.buckets.map((v) => (v === 0 ? null : v)),
        backgroundColor: barColors.value,
        borderRadius: 3,
        minBarLength: 2,
      },
    ],
  }));

  const chartOptions = computed<ChartOptions<'bar'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      // The datalabels plugin is registered globally by other charts; suppress it here.
      datalabels: { display: false },
      legend: { display: false },
      tooltip: {
        callbacks: {
          title: (items) => props.labels[items[0]?.dataIndex ?? 0] ?? '',
          label: (ctx) => {
            const v = (ctx.raw as number | null) ?? 0;
            return `${v} card${v !== 1 ? 's' : ''}`;
          },
        },
      },
    },
    scales: {
      x: { grid: { display: false }, ticks: { maxRotation: 45, autoSkip: true, font: { size: 10 } } },
      y: { beginAtZero: true, grace: '10%', grid: { color: 'rgba(107, 114, 128, 0.15)' }, ticks: { precision: 0 } },
    },
  }));
</script>

<template>
  <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
    <div class="mb-1 font-semibold">{{ title }}</div>
    <div v-if="subtitle" class="text-xs text-gray-500 dark:text-gray-400 mb-3">{{ subtitle }}</div>

    <div v-if="hasData">
      <div style="height: 220px">
        <Bar :data="chartData" :options="chartOptions" />
      </div>
      <div class="mt-2 text-xs text-gray-500 dark:text-gray-400">
        <slot name="footer" />
      </div>
    </div>
    <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-500">Not enough data yet.</div>
  </div>
</template>
