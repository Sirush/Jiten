<script setup lang="ts">
  import { Bar } from 'vue-chartjs';
  import {
    Chart as ChartJS,
    CategoryScale,
    LinearScale,
    BarElement,
    PointElement,
    LineElement,
    Tooltip as ChartTooltip,
    Legend,
    type ChartOptions,
    type ChartData,
  } from 'chart.js';
  import ChartDataLabels from 'chartjs-plugin-datalabels';
  import type { CardStatsResponseDto, RetentionResponseDto, ReviewForecast30dDto, AnswerButtonsDto, HourlyReviewDto } from '~/types';

  ChartJS.register(CategoryScale, LinearScale, BarElement, PointElement, LineElement, ChartTooltip, Legend);

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'SRS Stats' });

  const { $api } = useNuxtApp();

  const loadingStats = ref(true);
  const loadingRetention = ref(true);
  const cardStats = ref<CardStatsResponseDto | null>(null);
  const retention = ref<RetentionResponseDto | null>(null);

  onMounted(async () => {
    await Promise.all([
      (async () => {
        try {
          cardStats.value = await $api<CardStatsResponseDto>('srs/card-stats');
        } catch {
          cardStats.value = null;
        } finally {
          loadingStats.value = false;
        }
      })(),
      (async () => {
        try {
          retention.value = await $api<RetentionResponseDto>('srs/retention');
        } catch {
          retention.value = null;
        } finally {
          loadingRetention.value = false;
        }
      })(),
    ]);
  });

  // ── Today strip ───────────────────────────────────────────────────────────
  const today = computed(() => retention.value?.today ?? null);
  const todayStats = computed(() => {
    const t = today.value;
    return [
      { label: 'Reviews', value: t ? String(t.reviews) : '—' },
      { label: 'Pass rate', value: t && t.passRate != null ? `${Math.round(t.passRate * 100)}%` : '—' },
      { label: 'Minutes', value: t ? String(t.minutes) : '—' },
      { label: 'New cards', value: t ? String(t.newCards) : '—' },
    ];
  });

  // ── Card states ───────────────────────────────────────────────────────────
  const stateCounts = computed(() => cardStats.value?.stateCounts ?? null);
  const stateTiles = computed(() => {
    const s = stateCounts.value;
    if (!s) return [];
    return [
      { key: 'new', label: 'New', count: s.new, color: 'text-blue-600 dark:text-blue-400' },
      { key: 'learning', label: 'Learning', count: s.learning, color: 'text-orange-600 dark:text-orange-400' },
      { key: 'relearning', label: 'Relearning', count: s.relearning, color: 'text-red-600 dark:text-red-400' },
      { key: 'young', label: 'Young', count: s.young, color: 'text-purple-600 dark:text-purple-400' },
      { key: 'mature', label: 'Mature', count: s.mature, color: 'text-green-600 dark:text-green-400' },
      { key: 'suspended', label: 'Suspended', count: s.suspended, color: 'text-gray-500 dark:text-gray-400' },
      { key: 'mastered', label: 'Mastered', count: s.mastered, color: 'text-amber-600 dark:text-amber-400' },
      { key: 'blacklisted', label: 'Blacklisted', count: s.blacklisted, color: 'text-gray-500 dark:text-gray-400' },
    ];
  });
  const hasStates = computed(() => (stateCounts.value?.total ?? 0) > 0);

  // Stacked horizontal bar segments for the active (non-suspended/blacklisted) breakdown.
  const stateBarSegments = computed(() => {
    const s = stateCounts.value;
    if (!s) return [];
    const segs = [
      { key: 'new', count: s.new, color: 'bg-blue-500' },
      { key: 'learning', count: s.learning, color: 'bg-orange-500' },
      { key: 'relearning', count: s.relearning, color: 'bg-red-500' },
      { key: 'young', count: s.young, color: 'bg-purple-500' },
      { key: 'mature', count: s.mature, color: 'bg-green-500' },
      { key: 'mastered', count: s.mastered, color: 'bg-amber-500' },
    ].filter((seg) => seg.count > 0);
    const sum = segs.reduce((acc, seg) => acc + seg.count, 0) || 1;
    return segs.map((seg) => ({ ...seg, pct: (seg.count / sum) * 100 }));
  });

  // ── Difficulty / Retrievability labels (20 buckets, 5% wide) ──────────────
  const pctBucketLabels = Array.from({ length: 20 }, (_, i) => `${i * 5}–${(i + 1) * 5}%`);

  const difficulty = computed(() => cardStats.value?.difficulty ?? null);
  const retrievability = computed(() => cardStats.value?.retrievability ?? null);
  const stability = computed(() => cardStats.value?.stability ?? null);

  // ── Per-section time-window toggle ────────────────────────────────────────
  type WindowKey = 'last30' | 'last90' | 'all';
  const windowOptions: { key: WindowKey; label: string }[] = [
    { key: 'last30', label: '30 days' },
    { key: 'last90', label: '90 days' },
    { key: 'all', label: 'All time' },
  ];

  function answerHasData(a: AnswerButtonsDto | undefined): boolean {
    if (!a) return false;
    return [...a.learning, ...a.young, ...a.mature].some((n) => n > 0);
  }
  function hourlyHasData(h: HourlyReviewDto[] | undefined): boolean {
    return (h ?? []).some((x) => x.count > 0);
  }

  // ── Answer buttons (grouped bar) ──────────────────────────────────────────
  const answerWindow = ref<WindowKey>('all');
  const answerBlocks = computed(() => retention.value?.answerButtons ?? null);
  const answerButtons = computed(() => answerBlocks.value?.[answerWindow.value] ?? null);
  const hasAnswerButtons = computed(() => answerHasData(answerButtons.value ?? undefined));

  const answerChartData = computed<ChartData<'bar'>>(() => {
    const a = answerButtons.value ?? { learning: [0, 0, 0, 0], young: [0, 0, 0, 0], mature: [0, 0, 0, 0] };
    const groups = [a.learning, a.young, a.mature];
    const mk = (idx: number, label: string, color: string) => ({
      label,
      data: groups.map((g) => g[idx] ?? 0),
      backgroundColor: color,
      borderRadius: 3,
    });
    return {
      labels: ['Learning', 'Young', 'Mature'],
      datasets: [
        mk(0, 'Again', 'rgba(239, 68, 68, 0.85)'),
        mk(1, 'Hard', 'rgba(245, 158, 11, 0.85)'),
        mk(2, 'Good', 'rgba(16, 185, 129, 0.85)'),
        mk(3, 'Easy', 'rgba(59, 130, 246, 0.85)'),
      ],
    };
  });

  const answerChartOptions = computed<ChartOptions<'bar'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      datalabels: { display: false },
      legend: { display: true, position: 'bottom', labels: { usePointStyle: true, padding: 12, boxWidth: 8, font: { size: 11 } } },
      tooltip: {
        callbacks: { label: (ctx) => `${ctx.dataset.label}: ${ctx.raw as number}` },
      },
    },
    scales: {
      x: { stacked: false, grid: { display: false } },
      y: { beginAtZero: true, grid: { color: 'rgba(107, 114, 128, 0.15)' }, ticks: { precision: 0 } },
    },
  }));

  // Correct % per category (Good + Easy share).
  function correctPct(arr: number[] | undefined): number | null {
    if (!arr) return null;
    const total = arr.reduce((s, n) => s + n, 0);
    if (total === 0) return null;
    return Math.round((((arr[2] ?? 0) + (arr[3] ?? 0)) / total) * 100);
  }
  const answerFooter = computed(() => {
    const a = answerButtons.value;
    if (!a) return [];
    return [
      { label: 'Learning', pct: correctPct(a.learning) },
      { label: 'Young', pct: correctPct(a.young) },
      { label: 'Mature', pct: correctPct(a.mature) },
    ];
  });

  // Again rate = Again share across all three maturity categories.
  function againRate(a: AnswerButtonsDto | undefined): number | null {
    if (!a) return null;
    const cats = [a.learning, a.young, a.mature];
    const total = cats.reduce((s, arr) => s + arr.reduce((x, n) => x + n, 0), 0);
    if (total === 0) return null;
    const again = cats.reduce((s, arr) => s + (arr[0] ?? 0), 0);
    return Math.round((again / total) * 100);
  }
  // Delta line: shown only when both the 30-day and all-time windows have data.
  const answerDelta = computed(() => {
    const b = answerBlocks.value;
    if (!b) return null;
    const r30 = againRate(b.last30);
    const rAll = againRate(b.all);
    if (r30 == null || rAll == null) return null;
    return { r30, rAll };
  });

  // ── Hourly breakdown ──────────────────────────────────────────────────────
  const hourlyWindow = ref<WindowKey>('all');
  const hourlyBlocks = computed(() => retention.value?.hourly ?? null);
  const hourly = computed(() => hourlyBlocks.value?.[hourlyWindow.value] ?? null);
  const hasHourly = computed(() => hourlyHasData(hourly.value ?? undefined));

  const hourlyChartData = computed<ChartData<'bar' | 'line'>>(() => {
    const h = hourly.value ?? [];
    return {
      labels: Array.from({ length: 24 }, (_, i) => `${i}`),
      datasets: [
        {
          type: 'bar' as const,
          label: 'Reviews',
          data: h.map((x) => x.count),
          backgroundColor: 'rgba(168, 85, 247, 0.6)',
          borderRadius: 3,
          yAxisID: 'y',
          order: 2,
        },
        {
          type: 'line' as const,
          label: 'Pass rate',
          data: h.map((x) => (x.passRate == null ? null : Math.round(x.passRate * 100))),
          borderColor: 'rgb(16, 185, 129)',
          backgroundColor: 'rgba(16, 185, 129, 0.1)',
          borderWidth: 2,
          pointRadius: 2,
          spanGaps: true,
          yAxisID: 'yPct',
          order: 1,
        },
      ],
    };
  });

  const hourlyChartOptions = computed<ChartOptions<'bar' | 'line'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      datalabels: { display: false },
      legend: { display: true, position: 'bottom', labels: { usePointStyle: true, padding: 12, boxWidth: 8, font: { size: 11 } } },
      tooltip: {
        callbacks: {
          title: (items) => `${items[0]?.label ?? ''}:00`,
          label: (ctx) => {
            if (ctx.dataset.label === 'Pass rate') {
              const v = ctx.raw as number | null;
              return v == null ? 'Pass rate: —' : `Pass rate: ${v}%`;
            }
            return `Reviews: ${ctx.raw as number}`;
          },
        },
      },
    },
    scales: {
      x: { grid: { display: false }, ticks: { font: { size: 10 }, autoSkip: true, maxTicksLimit: 12 } },
      y: { beginAtZero: true, grid: { color: 'rgba(107, 114, 128, 0.15)' }, ticks: { precision: 0 }, title: { display: false } },
      yPct: {
        position: 'right' as const,
        min: 0,
        max: 100,
        grid: { display: false },
        ticks: { stepSize: 25, callback: (v) => `${v}%` },
      },
    },
  }));

  // ── Review time ───────────────────────────────────────────────────────────
  const reviewTimeWindow = ref<WindowKey>('all');
  const reviewTimeRoot = computed(() => retention.value?.reviewTime ?? null);
  // Selected-window stats; bucketLabels live once at the top level.
  const reviewTime = computed(() => {
    const root = reviewTimeRoot.value;
    if (!root) return null;
    return { ...root[reviewTimeWindow.value], bucketLabels: root.bucketLabels };
  });
  const reviewTimeHasData = computed(() => (reviewTime.value?.count ?? 0) > 0);
  // Delta line: show last-30 and all-time averages when both windows have reviews.
  function avgSeconds(v: number | null | undefined): number | null {
    return v == null ? null : Math.round(v * 10) / 10;
  }
  const reviewTimeDelta = computed(() => {
    const root = reviewTimeRoot.value;
    if (!root || root.last30.count === 0 || root.all.count === 0) return null;
    return { s30: avgSeconds(root.last30.averageSeconds), sAll: avgSeconds(root.all.averageSeconds) };
  });

  const reviewTimeChartData = computed<ChartData<'bar'>>(() => {
    const rt = reviewTime.value;
    return {
      labels: rt?.bucketLabels ?? [],
      datasets: [
        {
          data: (rt?.buckets ?? []).map((v) => (v === 0 ? null : v)),
          backgroundColor: 'rgba(168, 85, 247, 0.7)',
          borderRadius: 3,
          minBarLength: 2,
        },
      ],
    };
  });

  const reviewTimeChartOptions = computed<ChartOptions<'bar'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      datalabels: { display: false },
      legend: { display: false },
      tooltip: {
        callbacks: { label: (ctx) => `${(ctx.raw as number | null) ?? 0} reviews` },
      },
    },
    scales: {
      x: { grid: { display: false }, ticks: { maxRotation: 45, autoSkip: true, font: { size: 10 } } },
      y: { beginAtZero: true, grace: '10%', grid: { color: 'rgba(107, 114, 128, 0.15)' }, ticks: { precision: 0 } },
    },
  }));

  // ── Forecast ──────────────────────────────────────────────────────────────
  type ForecastRange = 30 | 90 | 365;
  const forecastDays = ref<ForecastRange>(90);
  const forecastOptions: { key: ForecastRange; label: string }[] = [
    { key: 30, label: '30d' },
    { key: 90, label: '90d' },
    { key: 365, label: '365d' },
  ];
  const forecast = ref<ReviewForecast30dDto | null>(null);
  const loadingForecast = ref(true);

  async function loadForecast() {
    loadingForecast.value = true;
    try {
      forecast.value = await $api<ReviewForecast30dDto>('srs/review-forecast-30d', { query: { days: forecastDays.value } });
    } catch {
      forecast.value = null;
    } finally {
      loadingForecast.value = false;
    }
  }
  onMounted(loadForecast);
  watch(forecastDays, loadForecast);

  const hasForecast = computed(() => (forecast.value?.days ?? []).some((d) => d.count > 0));

  const forecastChartData = computed<ChartData<'bar' | 'line'>>(() => {
    const days = forecast.value?.days ?? [];
    let cumulative = 0;
    const cum = days.map((d) => (cumulative += d.count));
    return {
      labels: days.map((d) => {
        const date = new Date(d.date + 'T00:00:00');
        return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
      }),
      datasets: [
        {
          type: 'bar' as const,
          label: 'Due',
          data: days.map((d) => (d.count === 0 ? null : d.count)),
          backgroundColor: days.map((_, i) => (i === 0 ? 'rgba(59, 130, 246, 0.8)' : 'rgba(168, 85, 247, 0.6)')),
          borderRadius: 3,
          minBarLength: 2,
          yAxisID: 'y',
          order: 2,
        },
        {
          type: 'line' as const,
          label: 'Cumulative',
          data: cum,
          borderColor: 'rgb(16, 185, 129)',
          backgroundColor: 'rgba(16, 185, 129, 0.1)',
          borderWidth: 2,
          pointRadius: 0,
          yAxisID: 'yCum',
          order: 1,
        },
      ],
    };
  });

  const forecastChartOptions = computed<ChartOptions<'bar' | 'line'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    layout: { padding: { top: 14 } },
    interaction: { mode: 'index', intersect: false },
    plugins: {
      datalabels: { display: false },
      legend: { display: true, position: 'bottom', labels: { usePointStyle: true, padding: 12, boxWidth: 8, font: { size: 11 } } },
      tooltip: {
        callbacks: {
          title: (items) => {
            const idx = items[0]?.dataIndex ?? 0;
            const day = forecast.value?.days[idx];
            if (!day) return '';
            const date = new Date(day.date + 'T00:00:00');
            return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
          },
          label: (ctx) => {
            if (ctx.dataset.label === 'Cumulative') return `Cumulative: ${ctx.raw as number}`;
            const count = (ctx.raw as number | null) ?? 0;
            return `${count} review${count !== 1 ? 's' : ''}`;
          },
        },
      },
    },
    scales: {
      x: { grid: { display: false }, ticks: { maxRotation: 45, autoSkip: true, maxTicksLimit: 12, font: { size: 10 } } },
      y: { beginAtZero: true, grace: '15%', grid: { color: 'rgba(107, 114, 128, 0.15)' }, ticks: { precision: 0 } },
      yCum: { position: 'right' as const, beginAtZero: true, grid: { display: false }, ticks: { precision: 0 } },
    },
  }));
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <SrsSubNav />

    <h1 class="text-2xl font-bold mb-4">SRS Stats</h1>

    <div class="flex flex-col gap-6">
      <!-- 1. Today strip -->
      <div v-if="loadingRetention" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <div v-for="i in 4" :key="i" class="h-14 rounded-lg bg-surface-100 dark:bg-surface-800 animate-pulse" />
        </div>
      </div>
      <div v-else class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="text-xs font-semibold uppercase tracking-wide text-surface-400 dark:text-surface-500 mb-3">Today</div>
        <div class="flex flex-wrap gap-3">
          <div
            v-for="s in todayStats"
            :key="s.label"
            class="flex-1 min-w-[6.5rem] rounded-lg border border-surface-100 dark:border-surface-800 bg-surface-50 dark:bg-surface-800/40 p-3 text-center"
          >
            <div class="text-[clamp(0.95rem,5vw,1.5rem)] font-bold tabular-nums text-gray-800 dark:text-gray-100">{{ s.value }}</div>
            <div class="text-xs text-gray-500 dark:text-gray-400 mt-0.5">{{ s.label }}</div>
          </div>
        </div>
      </div>

      <!-- 2. Card states -->
      <div v-if="loadingStats" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="h-5 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-4" />
        <div class="grid grid-cols-2 sm:grid-cols-4 gap-3">
          <div v-for="i in 8" :key="i" class="h-16 rounded-lg bg-surface-100 dark:bg-surface-800 animate-pulse" />
        </div>
      </div>
      <div v-else class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="flex items-center justify-between gap-2 mb-3">
          <span class="font-semibold">Card states</span>
          <span v-if="hasStates" class="text-sm text-gray-500 dark:text-gray-400 tabular-nums">{{ stateCounts!.total }} total</span>
        </div>
        <template v-if="hasStates">
          <div class="flex h-3 w-full overflow-hidden rounded-full bg-surface-100 dark:bg-surface-800 mb-4">
            <div v-for="seg in stateBarSegments" :key="seg.key" class="h-full" :class="seg.color" :style="{ width: `${seg.pct}%` }" />
          </div>
          <div class="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <div
              v-for="tile in stateTiles"
              :key="tile.key"
              class="rounded-lg border border-surface-100 dark:border-surface-800 bg-surface-50 dark:bg-surface-800/40 p-3 text-center"
            >
              <div class="text-[clamp(0.95rem,5vw,1.4rem)] font-bold tabular-nums" :class="tile.color">{{ tile.count }}</div>
              <div class="text-xs text-gray-500 dark:text-gray-400 mt-0.5">{{ tile.label }}</div>
            </div>
          </div>
        </template>
        <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-500">No cards yet.</div>
      </div>

      <!-- 3. Retention -->
      <SrsRetentionPanel :data="retention" :loading="loadingRetention" />

      <!-- 4. Difficulty + Retrievability -->
      <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
        <div v-if="loadingStats" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
          <div class="h-5 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-4" />
          <div class="h-[220px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
        </div>
        <SrsStatsHistogram
          v-else-if="difficulty"
          :buckets="difficulty.buckets"
          :labels="pctBucketLabels"
          color-mode="difficulty"
          title="Card Difficulty"
          subtitle="The higher the difficulty, the slower stability will increase."
        >
          <template #footer> Median difficulty: {{ difficulty.medianPct != null ? `${Math.round(difficulty.medianPct)}%` : '—' }} </template>
        </SrsStatsHistogram>

        <div v-if="loadingStats" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
          <div class="h-5 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-4" />
          <div class="h-[220px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
        </div>
        <SrsStatsHistogram
          v-else-if="retrievability"
          :buckets="retrievability.buckets"
          :labels="pctBucketLabels"
          color-mode="retrievability"
          title="Card Retrievability"
          subtitle="The probability of recalling a card today."
        >
          <template #footer>
            <div>Average retrievability: {{ retrievability.averagePct != null ? `${Math.round(retrievability.averagePct)}%` : '—' }}</div>
            <div>
              Estimated knowledge: {{ retrievability.estimatedKnowledge }} words
              <span v-if="retrievability.masteredCount > 0">(+{{ retrievability.masteredCount }} mastered)</span>
            </div>
          </template>
        </SrsStatsHistogram>
      </div>

      <!-- 5. Card stability -->
      <div v-if="loadingStats" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="h-5 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-4" />
        <div class="h-[220px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
      </div>
      <SrsStatsHistogram
        v-else-if="stability"
        :buckets="stability.buckets"
        :labels="stability.bucketLabels"
        color-mode="neutral"
        title="Card Stability"
        subtitle="How long a card can go before it needs reviewing."
      >
        <template #footer> Median stability: {{ stability.medianDays != null ? `${Math.round(stability.medianDays * 10) / 10} days` : '—' }} </template>
      </SrsStatsHistogram>

      <!-- 6. Answer buttons -->
      <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="flex flex-wrap items-center justify-between gap-2 mb-1">
          <div class="font-semibold">Answer buttons</div>
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in windowOptions"
              :key="opt.key"
              class="px-2.5 py-1 rounded-md transition-colors"
              :class="
                answerWindow === opt.key
                  ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                  : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'
              "
              @click="answerWindow = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <div class="text-xs text-gray-500 dark:text-gray-400 mb-3">How you grade cards across maturity levels.</div>
        <div v-if="loadingRetention" class="h-[220px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
        <template v-else-if="hasAnswerButtons">
          <div style="height: 220px">
            <Bar :data="answerChartData" :options="answerChartOptions" />
          </div>
          <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500 dark:text-gray-400">
            <span v-for="f in answerFooter" :key="f.label">
              {{ f.label }} correct: <span class="font-semibold text-gray-700 dark:text-gray-300">{{ f.pct != null ? `${f.pct}%` : '—' }}</span>
            </span>
          </div>
          <div v-if="answerDelta" class="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500 dark:text-gray-400">
            <span>
              Again rate (30d): <span class="font-semibold text-gray-700 dark:text-gray-300">{{ answerDelta.r30 }}%</span>
            </span>
            <span>
              Again rate (all time): <span class="font-semibold text-gray-700 dark:text-gray-300">{{ answerDelta.rAll }}%</span>
            </span>
          </div>
        </template>
        <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-500">Not enough data yet.</div>
      </div>

      <!-- 7. Hourly breakdown -->
      <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="flex flex-wrap items-center justify-between gap-2 mb-1">
          <div class="font-semibold">Hourly breakdown</div>
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in windowOptions"
              :key="opt.key"
              class="px-2.5 py-1 rounded-md transition-colors"
              :class="
                hourlyWindow === opt.key
                  ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                  : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'
              "
              @click="hourlyWindow = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <div class="text-xs text-gray-500 dark:text-gray-400 mb-3">Reviews and pass rate by hour of day (your local time).</div>
        <div v-if="loadingRetention" class="h-[220px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
        <div v-else-if="hasHourly" style="height: 220px">
          <Bar :data="hourlyChartData as any" :options="hourlyChartOptions as any" />
        </div>
        <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-500">Not enough data yet.</div>
      </div>

      <!-- 8. Review time -->
      <div v-if="loadingRetention" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="h-5 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-4" />
        <div class="h-[220px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
      </div>
      <div v-else-if="reviewTime" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="flex flex-wrap items-center justify-between gap-2 mb-1">
          <div class="font-semibold">Review time</div>
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in windowOptions"
              :key="opt.key"
              class="px-2.5 py-1 rounded-md transition-colors"
              :class="
                reviewTimeWindow === opt.key
                  ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                  : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'
              "
              @click="reviewTimeWindow = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <div class="text-xs text-gray-500 dark:text-gray-400 mb-3">How long you spend answering each card.</div>
        <template v-if="reviewTimeHasData">
          <div style="height: 220px">
            <Bar :data="reviewTimeChartData" :options="reviewTimeChartOptions" />
          </div>
          <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-500 dark:text-gray-400">
            <template v-if="reviewTimeDelta">
              <span>
                Average (30d): <span class="font-semibold text-gray-700 dark:text-gray-300">{{ reviewTimeDelta.s30 }}s</span>
              </span>
              <span>
                Average (all time): <span class="font-semibold text-gray-700 dark:text-gray-300">{{ reviewTimeDelta.sAll }}s</span>
              </span>
            </template>
            <span v-else>
              Average:
              <span class="font-semibold text-gray-700 dark:text-gray-300">
                {{ reviewTime.averageSeconds != null ? `${Math.round(reviewTime.averageSeconds * 10) / 10}s` : '—' }}
              </span>
            </span>
            <span>
              Total:
              <span class="font-semibold text-gray-700 dark:text-gray-300">{{ Math.round(reviewTime.totalHours * 10) / 10 }} hours</span>
            </span>
          </div>
        </template>
        <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-500">Not enough data yet.</div>
      </div>

      <!-- 9. Forecast -->
      <div class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
        <div class="flex flex-wrap items-center justify-between gap-2 mb-3">
          <div>
            <div class="font-semibold">Forecast</div>
            <div class="text-xs text-gray-500 dark:text-gray-400">Upcoming reviews by day.</div>
          </div>
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in forecastOptions"
              :key="opt.key"
              class="px-2.5 py-1 rounded-md transition-colors"
              :class="
                forecastDays === opt.key
                  ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                  : 'text-gray-500 hover:text-gray-700 dark:hover:text-gray-300'
              "
              @click="forecastDays = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
        </div>
        <div v-if="loadingForecast" class="h-[260px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />
        <div v-else-if="hasForecast" style="height: 260px">
          <Bar :data="forecastChartData as any" :options="forecastChartOptions as any" :plugins="[ChartDataLabels]" />
        </div>
        <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-500">No upcoming reviews scheduled.</div>
      </div>
    </div>
  </div>
</template>
