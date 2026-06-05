<script setup lang="ts">
  import { Line } from 'vue-chartjs';
  import {
    Chart as ChartJS,
    CategoryScale,
    LinearScale,
    PointElement,
    LineElement,
    Tooltip,
    Legend,
    type ChartOptions,
    type ChartData,
  } from 'chart.js';
  import type { RetentionResponseDto, RetentionWindowDto, RetentionBucketDto, PeriodRetentionDto } from '~/types';

  ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Tooltip, Legend);

  const { $api } = useNuxtApp();

  const isLoading = ref(true);
  const data = ref<RetentionResponseDto | null>(null);
  type WindowKey = 'last30' | 'last90' | 'all';
  const windowKey = ref<WindowKey>('last30');

  const windowOptions: { key: WindowKey; label: string }[] = [
    { key: 'last30', label: '30 days' },
    { key: 'last90', label: '90 days' },
    { key: 'all', label: 'All time' },
  ];

  onMounted(async () => {
    try {
      data.value = await $api<RetentionResponseDto>('srs/retention');
    } catch {
      data.value = null;
    } finally {
      isLoading.value = false;
    }
  });

  // Only worth showing once the user has accumulated some real (≥1 day interval) reviews.
  const hasData = computed(() => (data.value?.windows.all.overall.total ?? 0) >= 5);

  const matureThreshold = computed(() => data.value?.matureThresholdDays ?? 21);
  const targetPct = computed(() => (data.value ? Math.round(data.value.desiredRetention * 100) : 90));

  const activeWindow = computed<RetentionWindowDto | null>(() =>
    data.value ? data.value.windows[windowKey.value] : null);

  function pct(b: RetentionBucketDto | undefined): number | null {
    if (!b || b.total === 0 || b.retention == null) return null;
    return Math.round(b.retention * 100);
  }

  const overallPct = computed(() => pct(activeWindow.value?.overall));

  // How the active overall retention compares to the target.
  const targetDelta = computed(() => (overallPct.value == null ? null : overallPct.value - targetPct.value));

  const overallColor = computed(() => {
    const d = targetDelta.value;
    if (d == null) return 'text-gray-400';
    if (d >= 0) return 'text-green-600 dark:text-green-400';
    if (d >= -5) return 'text-amber-600 dark:text-amber-400';
    return 'text-red-600 dark:text-red-400';
  });

  const tiles = computed(() => {
    const w = activeWindow.value;
    return [
      { key: 'overall', label: 'Overall', bucket: w?.overall, hint: 'All reviews' },
      { key: 'young', label: 'Young', bucket: w?.young, hint: `< ${matureThreshold.value}d interval` },
      { key: 'mature', label: 'Mature', bucket: w?.mature, hint: `≥ ${matureThreshold.value}d interval` },
    ];
  });

  // Time series — weekly or monthly, over a selectable range.
  type Granularity = 'week' | 'month';
  const granularity = ref<Granularity>('month');

  type RangeKey = '1y' | '2y' | 'all';
  const range = ref<RangeKey>('1y');
  const rangeOptions: { key: RangeKey; label: string }[] = [
    { key: '1y', label: '1Y' },
    { key: '2y', label: '2Y' },
    { key: 'all', label: 'All' },
  ];

  const series = computed<PeriodRetentionDto[]>(() => {
    const full = granularity.value === 'week' ? (data.value?.weekly ?? []) : (data.value?.monthly ?? []);
    if (range.value === 'all') return full;
    const perYear = granularity.value === 'week' ? 52 : 12;
    return full.slice(-(perYear * (range.value === '2y' ? 2 : 1)));
  });
  const showChart = computed(() => series.value.length >= 2);

  const MONTH_NAMES = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

  function periodLabel(period: string): string {
    const parts = period.split('-');
    if (granularity.value === 'week') {
      // "yyyy-MM-dd" → "Mon D" (week commencing).
      return `${MONTH_NAMES[Number(parts[1]) - 1]} ${Number(parts[2])}`;
    }
    return `${MONTH_NAMES[Number(parts[1]) - 1]} ${String(parts[0]).slice(2)}`;
  }

  const chartData = computed<ChartData<'line'>>(() => {
    const rows = series.value;
    const labels = rows.map((r) => periodLabel(r.period));
    const target = rows.map(() => targetPct.value);
    return {
      labels,
      datasets: [
        {
          label: 'Overall',
          data: rows.map((r) => pct(r.overall)),
          borderColor: 'rgb(37, 99, 235)',
          backgroundColor: 'rgba(37, 99, 235, 0.1)',
          borderWidth: 2,
          cubicInterpolationMode: 'monotone' as const,
          spanGaps: true,
          clip: false as const,
          pointRadius: 3,
        },
        {
          label: 'Young',
          data: rows.map((r) => pct(r.young)),
          borderColor: 'rgb(168, 85, 247)',
          borderWidth: 1.5,
          cubicInterpolationMode: 'monotone' as const,
          spanGaps: true,
          clip: false as const,
          pointRadius: 2,
        },
        {
          label: 'Mature',
          data: rows.map((r) => pct(r.mature)),
          borderColor: 'rgb(16, 185, 129)',
          borderWidth: 1.5,
          cubicInterpolationMode: 'monotone' as const,
          spanGaps: true,
          clip: false as const,
          pointRadius: 2,
        },
        {
          label: `Target (${targetPct.value}%)`,
          data: target,
          borderColor: 'rgba(107, 114, 128, 0.7)',
          borderWidth: 1.5,
          borderDash: [5, 4],
          pointRadius: 0,
          tension: 0,
        },
      ],
    };
  });

  const chartOptions = computed<ChartOptions<'line'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    layout: { padding: { top: 8, bottom: 4 } },
    interaction: { mode: 'index', intersect: false },
    plugins: {
      // The datalabels plugin is registered globally by other charts; suppress it here.
      datalabels: { display: false },
      legend: { display: true, position: 'bottom', labels: { usePointStyle: true, padding: 12, boxWidth: 8, font: { size: 11 } } },
      tooltip: {
        callbacks: {
          label: (ctx) => {
            const v = ctx.raw as number | null;
            if (v == null) return `${ctx.dataset.label}: —`;
            if (ctx.dataset.label?.startsWith('Target')) return `${ctx.dataset.label}`;
            const row = series.value[ctx.dataIndex];
            const bucket = ctx.dataset.label === 'Overall' ? row?.overall
              : ctx.dataset.label === 'Young' ? row?.young : row?.mature;
            const n = bucket?.total ?? 0;
            return `${ctx.dataset.label}: ${v}% (${n} reviews)`;
          },
        },
      },
    },
    scales: {
      y: {
        min: 0,
        max: 100,
        ticks: { stepSize: 25, callback: (v) => `${v}%` },
        grid: { color: 'rgba(107, 114, 128, 0.15)' },
      },
      x: { grid: { display: false } },
    },
  }));
</script>

<template>
  <div v-if="isLoading" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
    <div class="h-5 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-4" />
    <div class="grid grid-cols-3 gap-3">
      <div v-for="i in 3" :key="i" class="h-16 rounded-lg bg-surface-100 dark:bg-surface-800 animate-pulse" />
    </div>
  </div>

  <div
    v-else-if="hasData"
    class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4"
  >
    <!-- Header + window selector -->
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4">
      <div class="flex items-center gap-2">
        <Icon name="material-symbols:target" size="1.25rem" class="text-primary-500" />
        <span class="font-semibold">Retention</span>
        <span
          v-if="targetDelta != null"
          class="text-xs px-1.5 py-0.5 rounded-full"
          :class="targetDelta >= 0
            ? 'bg-green-100 dark:bg-green-900/40 text-green-700 dark:text-green-300'
            : targetDelta >= -5
              ? 'bg-amber-100 dark:bg-amber-900/40 text-amber-700 dark:text-amber-300'
              : 'bg-red-100 dark:bg-red-900/40 text-red-700 dark:text-red-300'"
        >
          {{ targetDelta >= 0 ? 'On target' : `${targetDelta}% vs target` }}
        </span>
      </div>
      <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
        <button
          v-for="opt in windowOptions"
          :key="opt.key"
          class="px-2.5 py-1 rounded-md transition-colors"
          :class="windowKey === opt.key
            ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
            : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'"
          @click="windowKey = opt.key"
        >
          {{ opt.label }}
        </button>
      </div>
    </div>

    <!-- Stat tiles -->
    <div class="grid grid-cols-3 gap-3 mb-4">
      <div
        v-for="tile in tiles"
        :key="tile.key"
        class="rounded-lg border border-surface-100 dark:border-surface-800 bg-surface-50 dark:bg-surface-800/40 p-3 text-center"
      >
        <div class="text-xs text-gray-500 mb-1">{{ tile.label }}</div>
        <div
          class="text-2xl font-bold tabular-nums"
          :class="tile.key === 'overall' ? overallColor : 'text-gray-800 dark:text-gray-100'"
        >
          <template v-if="pct(tile.bucket) != null">{{ pct(tile.bucket) }}%</template>
          <template v-else>—</template>
        </div>
        <div class="text-[11px] text-gray-400 mt-0.5">
          <template v-if="tile.bucket && tile.bucket.total > 0">{{ tile.bucket.total }} reviews</template>
          <template v-else>{{ tile.hint }}</template>
        </div>
      </div>
    </div>

    <div class="text-xs text-gray-500 dark:text-gray-400 mb-3">
      Target {{ targetPct }}% · measured from reviews on cards already in rotation (graded a day or more after the previous review).
    </div>

    <!-- Over-time chart -->
    <div v-if="showChart">
      <div class="flex flex-wrap items-center justify-between gap-2 mb-1">
        <div class="text-xs text-gray-500 dark:text-gray-400">Retention over time</div>
        <div class="flex items-center gap-2">
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in rangeOptions"
              :key="opt.key"
              class="px-2 py-0.5 rounded-md transition-colors"
              :class="range === opt.key
                ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'"
              @click="range = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in [{ key: 'week', label: 'Weekly' }, { key: 'month', label: 'Monthly' }] as const"
              :key="opt.key"
              class="px-2 py-0.5 rounded-md transition-colors"
              :class="granularity === opt.key
                ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'"
              @click="granularity = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
      </div>
      <div style="height: 220px">
        <Line :data="chartData" :options="chartOptions" />
      </div>
    </div>
  </div>
</template>
