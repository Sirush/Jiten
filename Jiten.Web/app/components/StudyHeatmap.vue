<script setup lang="ts">
  import type { StudyHeatmapResponse } from '~/types';
  import { MONTHS_SHORT, formatWeekdayDayMonth } from '~/utils/dateFormat';

  const props = withDefaults(
    defineProps<{
      /** The last twelve months up to today instead of a calendar year with year navigation. */
      rolling?: boolean;
      /** Already loaded with the page, so no request is made; null when the profile hides study data. */
      data?: StudyHeatmapResponse | null;
    }>(),
    { rolling: false, data: undefined }
  );

  const { $api } = useNuxtApp();
  const { apiPath, viewAsQuery } = useProfileContext();

  const isLoading = ref(false);
  const error = ref<string | null>(null);
  const heatmapData = ref<StudyHeatmapResponse | null>(null);
  const selectedYear = ref(new Date().getFullYear());

  const tooltip = ref({ visible: false, text: '', x: 0, y: 0 });

  const CELL_SIZE = 11;
  /** Largest cell the rolling view grows to when filling a wide card, in px. */
  const MAX_CELL_SIZE = 16;
  const CELL_GAP = 2;
  const LABEL_WIDTH = 28;
  const cellSize = ref(CELL_SIZE);
  const cellStep = computed(() => cellSize.value + CELL_GAP);

  const NOT_AVAILABLE = 'Study data not available';

  const fetchHeatmap = async () => {
    isLoading.value = true;
    error.value = null;
    try {
      heatmapData.value = await $api<StudyHeatmapResponse>(apiPath('study-heatmap'), {
        query: { ...viewAsQuery.value, ...(props.rolling ? { rolling: true } : { year: selectedYear.value }) },
      });
    } catch (err: unknown) {
      const fetchError = err as { response?: { status?: number } };
      error.value = fetchError?.response?.status === 404 ? NOT_AVAILABLE : 'Failed to load study heatmap';
      heatmapData.value = null;
    } finally {
      isLoading.value = false;
    }
  };

  function showData(data: StudyHeatmapResponse | null) {
    heatmapData.value = data;
    error.value = data ? null : NOT_AVAILABLE;
  }

  watch(selectedYear, () => fetchHeatmap());
  watch(
    () => props.data,
    (data) => {
      if (data !== undefined) showData(data);
    }
  );

  const gridScroller = ref<HTMLElement | null>(null);
  let resizeObserver: ResizeObserver | undefined;
  watch(gridScroller, (el) => {
    resizeObserver?.disconnect();
    if (!el || !props.rolling) return;
    resizeObserver = new ResizeObserver(([entry]) => {
      const fit = ((entry?.contentRect.width ?? 0) - LABEL_WIDTH - 1) / Math.max(1, weeks.value.length) - CELL_GAP;
      cellSize.value = Math.min(MAX_CELL_SIZE, Math.max(CELL_SIZE, fit));
      el.scrollLeft = el.scrollWidth;
    });
    resizeObserver.observe(el);
  });
  onBeforeUnmount(() => resizeObserver?.disconnect());
  /** Applied on mount rather than during SSR: the grid is laid out from the browser's today and locale. */
  onMounted(() => (props.data === undefined ? fetchHeatmap() : showData(props.data)));

  interface DayCell {
    date: Date;
    dateStr: string;
    count: number;
    correct: number;
  }

  const reviewMap = computed(() => {
    const map = new Map<string, { reviewCount: number; correctCount: number }>();
    if (!heatmapData.value) return map;
    for (const day of heatmapData.value.days) {
      map.set(day.date, { reviewCount: day.reviewCount, correctCount: day.correctCount });
    }
    return map;
  });

  function buildWeeks(start: Date, end: Date, inRange: (day: Date) => boolean): DayCell[][] {
    const current = new Date(start);
    const startDay = current.getDay();
    current.setDate(current.getDate() + (startDay === 0 ? -6 : 1 - startDay));

    const result: DayCell[][] = [];
    while (current <= end) {
      const week: DayCell[] = [];
      for (let d = 0; d < 7; d++) {
        const dateStr = formatDate(current);
        const data = reviewMap.value.get(dateStr);
        const shown = inRange(current);
        week.push({
          date: new Date(current),
          dateStr,
          count: shown ? (data?.reviewCount ?? 0) : -1,
          correct: shown ? (data?.correctCount ?? 0) : -1,
        });
        current.setDate(current.getDate() + 1);
      }
      result.push(week);
    }
    return result;
  }

  const weeks = computed(() => {
    if (props.rolling) {
      const today = new Date();
      today.setHours(0, 0, 0, 0);
      const start = new Date(today);
      start.setDate(start.getDate() - 364);
      return buildWeeks(start, today, (day) => day <= today);
    }
    const year = selectedYear.value;
    return buildWeeks(new Date(year, 0, 1), new Date(year, 11, 31), (day) => day.getFullYear() === year);
  });

  const monthLabels = computed(() => {
    const labels: { text: string; col: number }[] = [];
    let lastMonth = -1;
    for (let w = 0; w < weeks.value.length; w++) {
      for (const day of weeks.value[w]) {
        if (day.count === -1) continue;
        const m = day.date.getMonth();
        if (m !== lastMonth) {
          labels.push({ text: MONTHS_SHORT[m], col: w });
          lastMonth = m;
        }
        break;
      }
    }
    if (labels.length > 1 && labels[1]!.col - labels[0]!.col < 3) labels.shift();
    return labels;
  });

  const thresholds = computed<[number, number, number]>(() => {
    if (!heatmapData.value) return [1, 2, 3];
    const counts = heatmapData.value.days
      .map((d) => d.reviewCount)
      .filter((c) => c > 0)
      .sort((a, b) => a - b);
    if (counts.length === 0) return [1, 2, 3];

    const quantile = (q: number) => {
      const pos = (counts.length - 1) * q;
      const base = Math.floor(pos);
      const rest = pos - base;
      const lo = counts[base];
      const hi = counts[base + 1] ?? lo;
      return lo + (hi - lo) * rest;
    };

    return [quantile(0.25), quantile(0.5), quantile(0.75)];
  });

  const gridWidth = computed(() => LABEL_WIDTH + weeks.value.length * cellStep.value);

  function getIntensityClass(count: number): string {
    if (count <= 0) return 'bg-gray-100 dark:bg-gray-800';
    const [t1, t2, t3] = thresholds.value;
    if (count < t1) return 'bg-purple-200 dark:bg-purple-900/60';
    if (count < t2) return 'bg-purple-400 dark:bg-purple-700';
    if (count < t3) return 'bg-purple-500 dark:bg-purple-500';
    return 'bg-purple-700 dark:bg-purple-400';
  }

  function formatDate(d: Date): string {
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  }

  function formatDisplayDate(d: Date): string {
    return formatWeekdayDayMonth(d);
  }

  function showTooltip(event: MouseEvent | TouchEvent, day: DayCell) {
    if (day.count === -1) return;
    const dateStr = formatDisplayDate(day.date);
    const passRate = day.count > 0 ? Math.round((day.correct / day.count) * 100) : 0;
    const point = 'touches' in event ? event.touches[0] : event;
    tooltip.value = {
      visible: true,
      text: day.count === 0 ? `${dateStr}: No reviews` : `${dateStr}: ${day.count} reviews (${passRate}% pass rate)`,
      x: point.clientX,
      y: point.clientY,
    };
  }

  function hideTooltip() {
    tooltip.value.visible = false;
  }

  const currentYear = new Date().getFullYear();
  const canGoNext = computed(() => selectedYear.value < currentYear);
</script>

<template>
  <div>
    <div v-if="!heatmapData && !error" aria-busy="true">
      <Skeleton width="70%" height="1.75rem" class="mb-5" />
      <Skeleton :height="rolling ? '8.5rem' : '7.5rem'" />
    </div>

    <div v-else-if="error" class="text-center py-4">
      <Message severity="warn" :closable="false">{{ error }}</Message>
    </div>

    <div
      v-else-if="heatmapData && heatmapData.totalReviews === 0 && heatmapData.totalReviewDays === 0"
      class="text-center py-6 text-gray-500 dark:text-gray-400"
    >
      <Icon name="material-symbols:calendar-month" size="2.5rem" class="mb-3 text-gray-300 dark:text-gray-400" />
      <p class="mb-2">No study activity yet</p>
      <NuxtLink to="/srs/decks" class="text-sm text-purple-500 hover:text-purple-600 dark:text-purple-400 dark:hover:text-purple-300">
        Start studying to see your activity here
      </NuxtLink>
    </div>

    <div v-else-if="heatmapData" class="transition-opacity duration-200" :class="isLoading && 'opacity-60 delay-200'">
      <!-- Streak and summary stats -->
      <div class="flex flex-wrap items-center gap-x-6 gap-y-2 mb-5">
        <div class="flex items-center gap-2">
          <Icon name="material-symbols:local-fire-department" size="1.5rem" class="text-orange-500" />
          <div>
            <span class="text-xl font-bold tabular-nums">{{ heatmapData.currentStreak }}</span>
            <span class="text-sm text-gray-500 dark:text-gray-400 ml-1">day streak</span>
          </div>
        </div>
        <div>
          <span class="text-sm text-gray-500 dark:text-gray-400">Longest:</span>
          <span class="font-semibold tabular-nums ml-1">{{ heatmapData.longestStreak }} days</span>
        </div>
        <div>
          <span class="text-sm text-gray-500 dark:text-gray-400">Days studied:</span>
          <span class="font-semibold tabular-nums ml-1">{{ heatmapData.totalReviewDays.toLocaleString() }}</span>
        </div>
        <div>
          <span class="text-sm text-gray-500 dark:text-gray-400">{{ rolling ? 'Reviews in the last 12 months:' : 'Reviews this year:' }}</span>
          <span class="font-semibold tabular-nums ml-1">{{ heatmapData.totalReviews.toLocaleString() }}</span>
        </div>
      </div>

      <!-- Year selector -->
      <div v-if="!rolling" class="flex items-center gap-2 mb-3">
        <button class="p-1 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors" @click="selectedYear--">
          <Icon name="material-symbols:chevron-left" size="1.25rem" />
        </button>
        <span class="font-semibold tabular-nums min-w-12 text-center">{{ selectedYear }}</span>
        <button
          class="p-1 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors disabled:opacity-30 disabled:cursor-default"
          :disabled="!canGoNext"
          @click="canGoNext && selectedYear++"
        >
          <Icon name="material-symbols:chevron-right" size="1.25rem" />
        </button>
      </div>

      <!-- Heatmap grid -->
      <div ref="gridScroller" class="scrollbar-styled overflow-x-auto pb-1">
        <div class="relative" :style="{ width: `${gridWidth}px` }">
          <!-- Month labels row -->
          <div class="h-4 relative" :style="{ marginLeft: `${LABEL_WIDTH}px` }">
            <span
              v-for="label in monthLabels"
              :key="label.text + label.col"
              class="absolute text-[10px] text-gray-400 leading-none"
              :style="{ left: `${label.col * cellStep}px` }"
            >
              {{ label.text }}
            </span>
          </div>

          <!-- Day rows -->
          <div class="flex flex-col" :style="{ gap: `${CELL_GAP}px` }">
            <div v-for="dayIndex in 7" :key="dayIndex" class="flex items-center" :style="{ gap: `${CELL_GAP}px` }">
              <span class="text-[10px] text-gray-400 text-right shrink-0 leading-none" :style="{ width: `${LABEL_WIDTH - 4}px` }">
                {{ dayIndex === 1 ? 'Mon' : dayIndex === 3 ? 'Wed' : dayIndex === 5 ? 'Fri' : '' }}
              </span>
              <template v-for="(week, wi) in weeks" :key="wi">
                <div
                  class="rounded-sm cursor-default"
                  :style="{ width: `${cellSize}px`, height: `${cellSize}px` }"
                  :class="week[dayIndex - 1].count === -1 ? 'bg-transparent' : getIntensityClass(week[dayIndex - 1].count)"
                  @mouseenter="showTooltip($event, week[dayIndex - 1])"
                  @mouseleave="hideTooltip"
                  @touchstart.prevent="showTooltip($event, week[dayIndex - 1])"
                  @touchend="hideTooltip"
                />
              </template>
            </div>
          </div>
        </div>
      </div>

      <!-- Legend -->
      <div class="flex items-center gap-1.5 mt-3 text-[11px] text-gray-400">
        <span>Less</span>
        <div class="rounded-sm bg-gray-100 dark:bg-gray-800" :style="{ width: `${CELL_SIZE}px`, height: `${CELL_SIZE}px` }" />
        <div class="rounded-sm bg-purple-200 dark:bg-purple-900/60" :style="{ width: `${CELL_SIZE}px`, height: `${CELL_SIZE}px` }" />
        <div class="rounded-sm bg-purple-400 dark:bg-purple-700" :style="{ width: `${CELL_SIZE}px`, height: `${CELL_SIZE}px` }" />
        <div class="rounded-sm bg-purple-500 dark:bg-purple-500" :style="{ width: `${CELL_SIZE}px`, height: `${CELL_SIZE}px` }" />
        <div class="rounded-sm bg-purple-700 dark:bg-purple-400" :style="{ width: `${CELL_SIZE}px`, height: `${CELL_SIZE}px` }" />
        <span>More</span>
      </div>
    </div>

    <!-- Floating tooltip -->
    <Teleport to="body">
      <div
        v-if="tooltip.visible"
        class="fixed z-50 px-2.5 py-1.5 rounded-md text-xs font-medium bg-gray-900 text-white dark:bg-gray-100 dark:text-gray-900 shadow-lg pointer-events-none whitespace-nowrap"
        :style="{ left: `${tooltip.x + 12}px`, top: `${tooltip.y - 32}px` }"
      >
        {{ tooltip.text }}
      </div>
    </Teleport>
  </div>
</template>
