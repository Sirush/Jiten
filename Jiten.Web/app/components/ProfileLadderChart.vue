<script setup lang="ts">
  import { Scatter } from 'vue-chartjs';
  import { Chart as ChartJS, LinearScale, PointElement, Tooltip as ChartTooltip, type ChartData, type ChartOptions } from 'chart.js';
  import type { ProfileLadderPoint } from '~/types';
  import { formatDateShort, formatMonthYear } from '~/utils/dateFormat';
  import { difficultyNames, EXPERT_BAND, shelfDifficultyBand } from '~/utils/difficultyColours';

  ChartJS.register(LinearScale, PointElement, ChartTooltip);

  const props = defineProps<{ points: ProfileLadderPoint[] }>();

  const AXIS = '#6b7280';
  const GRID = 'rgba(107, 114, 128, 0.15)';

  const { chartColour } = useDifficultyColours();
  const localiseTitle = useLocaliseTitle();

  const DAY = 86_400_000;
  const TOP = EXPERT_BAND + 1;
  const bandName = (difficulty: number) => difficultyNames[shelfDifficultyBand(difficulty)]!;
  const plotted = (difficulty: number) => Math.min(difficulty, TOP);
  /** Steps the date axis may use, in months; the smallest that keeps it to about six labels wins. */
  const TICK_STEPS = [1, 2, 3, 6, 12, 24, 60];
  /** Dots closer than this share of the time span, at about the same difficulty, are nudged sideways so each stays hoverable. */
  const DODGE_SHARE = 0.015;
  const DODGE_DIFFICULTY = 0.2;

  /** Finish dates are bare days, so they are placed and labelled in UTC to keep a dot and its label on the same day everywhere. */
  const time = (p: ProfileLadderPoint) => new Date(`${p.finishedOn}T00:00:00Z`).getTime();

  const range = computed(() => {
    const times = props.points.map(time);
    if (!times.length) return { min: 0, max: DAY };
    const first = Math.min(...times);
    const last = Math.max(...times);
    const pad = Math.max(30 * DAY, (last - first) * 0.04);
    return { min: first - pad, max: last + pad };
  });

  const positions = computed(() => {
    const threshold = (range.value.max - range.value.min) * DODGE_SHARE;
    const placed: { x: number; y: number }[] = [];
    for (const p of props.points) {
      const t = time(p);
      let x = t;
      for (let k = 1; placed.some((q) => Math.abs(q.x - x) < threshold && Math.abs(q.y - plotted(p.difficulty)) < DODGE_DIFFICULTY); k++)
        x = t + (k % 2 ? 1 : -1) * Math.ceil(k / 2) * threshold;
      placed.push({ x, y: plotted(p.difficulty) });
    }
    return placed;
  });

  const tickStep = computed(() => {
    const months = (range.value.max - range.value.min) / (30.44 * DAY);
    return TICK_STEPS.find((step) => months / step <= 6) ?? TICK_STEPS[TICK_STEPS.length - 1]!;
  });

  /** Month starts whose month count is a multiple of the step, so labels fall on January, July and so on rather than wherever the data begins. */
  function buildTicks(min: number, max: number) {
    const step = tickStep.value;
    const start = new Date(min);
    const ticks: { value: number }[] = [];
    for (let index = Math.ceil((start.getUTCFullYear() * 12 + start.getUTCMonth()) / step) * step; ; index += step) {
      const value = Date.UTC(Math.floor(index / 12), index % 12, 1);
      if (value > max) break;
      if (value >= min) ticks.push({ value });
    }
    return ticks;
  }

  const tickLabel = (value: number) => (tickStep.value >= 12 ? String(new Date(value).getUTCFullYear()) : formatMonthYear(value, { utc: true }));

  const chartData = computed<ChartData<'scatter'>>(() => ({
    datasets: [
      {
        data: positions.value,
        pointBackgroundColor: props.points.map((p) => chartColour(Math.min(p.difficulty, TOP - 0.01))),
        pointBorderColor: 'rgba(255,255,255,0.9)',
        pointBorderWidth: 1.5,
        pointRadius: 5,
        pointHoverRadius: 7,
        pointHitRadius: 10,
      },
    ],
  }));

  const options = computed<ChartOptions<'scatter'>>(() => ({
    responsive: true,
    maintainAspectRatio: false,
    animation: false,
    layout: { padding: { top: 8 } },
    plugins: {
      legend: { display: false },
      tooltip: {
        displayColors: false,
        callbacks: {
          title: (items) => {
            const p = props.points[items[0]!.dataIndex]!;
            return p.title ? localiseTitle({ originalTitle: p.title, romajiTitle: p.romajiTitle, englishTitle: p.englishTitle }) : '';
          },
          label: (item) => {
            const p = props.points[item.dataIndex]!;
            return [`Finished ${formatDateShort(time(p), { utc: true })}`, `${bandName(p.difficulty)} (${p.difficulty.toFixed(1)})`];
          },
        },
      },
    },
    scales: {
      x: {
        type: 'linear',
        min: range.value.min,
        max: range.value.max,
        afterBuildTicks: (axis) => {
          axis.ticks = buildTicks(axis.min, axis.max);
        },
        ticks: { color: AXIS, autoSkip: false, maxRotation: 0, callback: (v) => tickLabel(Number(v)) },
        grid: { color: GRID },
      },
      y: {
        min: 0,
        max: TOP,
        afterBuildTicks: (axis) => {
          axis.ticks = Array.from({ length: TOP * 2 + 1 }, (_, i) => ({ value: i / 2 }));
        },
        ticks: {
          color: AXIS,
          autoSkip: false,
          callback: (v) => (Number.isInteger(Number(v)) ? '' : bandName(Number(v))),
        },
        grid: {
          color: (ctx) => (Number.isInteger(ctx.tick.value) ? GRID : 'transparent'),
          drawTicks: false,
        },
      },
    },
    onClick: (_event, elements) => {
      const deckId = elements[0] ? props.points[elements[0].index]!.deckId : null;
      if (deckId) navigateTo(`/decks/media/${deckId}/detail`);
    },
    onHover: (event, elements) => {
      const target = event.native?.target as HTMLElement | undefined;
      if (target) target.style.cursor = elements[0] && props.points[elements[0].index]!.deckId ? 'pointer' : 'default';
    },
  }));
</script>

<template>
  <div class="h-64" role="img" :aria-label="`Difficulty of ${points.length} finished titles over time`">
    <Scatter :data="chartData" :options="options" />
  </div>
</template>
