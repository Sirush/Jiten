<script setup lang="ts">
  import type { StudyHeatmapResponse } from '~/types';

  const props = defineProps<{
    username: string;
  }>();

  const { $api } = useNuxtApp();

  const isLoading = ref(false);
  const error = ref<string | null>(null);
  const heatmapData = ref<StudyHeatmapResponse | null>(null);
  const selectedYear = ref(new Date().getFullYear());

  const tooltip = ref({ visible: false, text: '', x: 0, y: 0 });

  const CELL_SIZE = 11;
  const CELL_GAP = 2;
  const CELL_STEP = CELL_SIZE + CELL_GAP;
  const LABEL_WIDTH = 28;

  const fetchHeatmap = async () => {
    isLoading.value = true;
    error.value = null;
    try {
      heatmapData.value = await $api<StudyHeatmapResponse>(
        `user/profile/${props.username}/study-heatmap`,
        { query: { year: selectedYear.value } }
      );
    } catch (err: unknown) {
      const fetchError = err as { response?: { status?: number } };
      error.value = fetchError?.response?.status === 404
        ? 'Study data not available'
        : 'Failed to load study heatmap';
    } finally {
      isLoading.value = false;
    }
  };

  watch(selectedYear, () => fetchHeatmap());
  onMounted(() => fetchHeatmap());

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

  const weeks = computed(() => {
    const year = selectedYear.value;
    const dec31 = new Date(year, 11, 31);

    const jan1 = new Date(year, 0, 1);
    const startDay = jan1.getDay();
    const mondayOffset = startDay === 0 ? -6 : 1 - startDay;
    const start = new Date(year, 0, 1 + mondayOffset);

    const result: DayCell[][] = [];
    const current = new Date(start);

    while (current <= dec31 || result.length === 0) {
      const week: DayCell[] = [];
      for (let d = 0; d < 7; d++) {
        const dateStr = formatDate(current);
        const data = reviewMap.value.get(dateStr);
        const isInYear = current.getFullYear() === year;
        week.push({
          date: new Date(current),
          dateStr,
          count: isInYear ? (data?.reviewCount ?? 0) : -1,
          correct: isInYear ? (data?.correctCount ?? 0) : -1,
        });
        current.setDate(current.getDate() + 1);
      }
      result.push(week);
      if (current.getFullYear() > year && current.getDay() === 1) break;
    }

    return result;
  });

  const monthLabels = computed(() => {
    const labels: { text: string; col: number }[] = [];
    const monthNames = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    let lastMonth = -1;
    for (let w = 0; w < weeks.value.length; w++) {
      for (const day of weeks.value[w]) {
        if (day.count === -1) continue;
        const m = day.date.getMonth();
        if (m !== lastMonth) {
          labels.push({ text: monthNames[m], col: w });
          lastMonth = m;
        }
        break;
      }
    }
    return labels;
  });

  const maxCount = computed(() => {
    if (!heatmapData.value) return 1;
    let max = 0;
    for (const day of heatmapData.value.days) {
      if (day.reviewCount > max) max = day.reviewCount;
    }
    return max || 1;
  });

  const gridWidth = computed(() => LABEL_WIDTH + weeks.value.length * CELL_STEP);

  function getIntensityClass(count: number): string {
    if (count <= 0) return 'bg-gray-100 dark:bg-gray-800';
    const ratio = count / maxCount.value;
    if (ratio <= 0.25) return 'bg-purple-200 dark:bg-purple-900/60';
    if (ratio <= 0.5) return 'bg-purple-400 dark:bg-purple-700';
    if (ratio <= 0.75) return 'bg-purple-500 dark:bg-purple-500';
    return 'bg-purple-700 dark:bg-purple-400';
  }

  function formatDate(d: Date): string {
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  }

  function formatDisplayDate(d: Date): string {
    return d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
  }

  function showTooltip(event: MouseEvent | TouchEvent, day: DayCell) {
    if (day.count === -1) return;
    const dateStr = formatDisplayDate(day.date);
    const passRate = day.count > 0 ? Math.round((day.correct / day.count) * 100) : 0;
    const point = 'touches' in event ? event.touches[0] : event;
    tooltip.value = {
      visible: true,
      text: day.count === 0
        ? `${dateStr}: No reviews`
        : `${dateStr}: ${day.count} reviews (${passRate}% pass rate)`,
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
    <div v-if="isLoading" class="flex justify-center py-8">
      <ProgressSpinner style="width: 2rem; height: 2rem" />
    </div>

    <div v-else-if="error" class="text-center py-4">
      <Message severity="warn" :closable="false">{{ error }}</Message>
    </div>

    <div v-else-if="heatmapData && heatmapData.totalReviews === 0 && heatmapData.totalReviewDays === 0" class="text-center py-6 text-gray-500">
      <Icon name="material-symbols:calendar-month" size="2.5rem" class="mb-3 text-gray-300 dark:text-gray-600" />
      <p class="mb-2">No study activity yet</p>
      <NuxtLink to="/srs/decks" class="text-sm text-purple-500 hover:text-purple-600 dark:text-purple-400 dark:hover:text-purple-300">
        Start studying to see your activity here
      </NuxtLink>
    </div>

    <div v-else-if="heatmapData">
      <!-- Streak and summary stats -->
      <div class="flex flex-wrap items-center gap-x-6 gap-y-2 mb-5">
        <div class="flex items-center gap-2">
          <Icon name="material-symbols:local-fire-department" size="1.5rem" class="text-orange-500" />
          <div>
            <span class="text-xl font-bold tabular-nums">{{ heatmapData.currentStreak }}</span>
            <span class="text-sm text-gray-500 ml-1">day streak</span>
          </div>
        </div>
        <div>
          <span class="text-sm text-gray-500">Longest:</span>
          <span class="font-semibold tabular-nums ml-1">{{ heatmapData.longestStreak }} days</span>
        </div>
        <div>
          <span class="text-sm text-gray-500">Days studied:</span>
          <span class="font-semibold tabular-nums ml-1">{{ heatmapData.totalReviewDays }}</span>
        </div>
        <div>
          <span class="text-sm text-gray-500">Reviews this year:</span>
          <span class="font-semibold tabular-nums ml-1">{{ heatmapData.totalReviews.toLocaleString() }}</span>
        </div>
      </div>

      <!-- Year selector -->
      <div class="flex items-center gap-2 mb-3">
        <button
          class="p-1 rounded hover:bg-gray-200 dark:hover:bg-gray-700 transition-colors"
          @click="selectedYear--"
        >
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
      <div class="overflow-x-auto">
        <div class="relative" :style="{ width: `${gridWidth}px` }">
          <!-- Month labels row -->
          <div class="h-4 relative" :style="{ marginLeft: `${LABEL_WIDTH}px` }">
            <span
              v-for="label in monthLabels"
              :key="label.text + label.col"
              class="absolute text-[10px] text-gray-400 leading-none"
              :style="{ left: `${label.col * CELL_STEP}px` }"
            >
              {{ label.text }}
            </span>
          </div>

          <!-- Day rows -->
          <div class="flex flex-col" :style="{ gap: `${CELL_GAP}px` }">
            <div
              v-for="dayIndex in 7"
              :key="dayIndex"
              class="flex items-center"
              :style="{ gap: `${CELL_GAP}px` }"
            >
              <span
                class="text-[10px] text-gray-400 text-right shrink-0 leading-none"
                :style="{ width: `${LABEL_WIDTH - 4}px` }"
              >
                {{ dayIndex === 1 ? 'Mon' : dayIndex === 3 ? 'Wed' : dayIndex === 5 ? 'Fri' : '' }}
              </span>
              <template v-for="(week, wi) in weeks" :key="wi">
                <div
                  class="rounded-sm cursor-default"
                  :style="{ width: `${CELL_SIZE}px`, height: `${CELL_SIZE}px` }"
                  :class="week[dayIndex - 1].count === -1
                    ? 'bg-transparent'
                    : getIntensityClass(week[dayIndex - 1].count)"
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
