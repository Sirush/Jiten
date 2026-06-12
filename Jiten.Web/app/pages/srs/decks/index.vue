<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { DeckOrder, MediaType, StudyDeckType, type StudyDeckDto } from '~/types';
  import { Bar } from 'vue-chartjs';
  import { Chart as ChartJS, CategoryScale, LinearScale, BarElement, Tooltip as ChartTooltip } from 'chart.js';
  import ChartDataLabels from 'chartjs-plugin-datalabels';

  ChartJS.register(CategoryScale, LinearScale, BarElement, ChartTooltip);

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Study Decks' });

  const srsStore = useSrsStore();
  const toast = useToast();
  const confirm = useConfirm();
  const localiseTitle = useLocaliseTitle();
  const router = useRouter();

  const showAddDialog = ref(false);
  const editingDeck = ref<StudyDeckDto | undefined>(undefined);
  const showEditDialog = ref(false);
  const downloadDeck = ref<StudyDeckDto | undefined>(undefined);
  const showDownloadDialog = ref(false);
  const decksLoading = ref(!srsStore.studyDecks.length);
  const activeDeckListRef = ref<HTMLElement | null>(null);
  const inactiveDeckListRef = ref<HTMLElement | null>(null);

  const refreshing = ref(false);

  onMounted(async () => {
    const isCold = !srsStore.studyDecks.length;
    if (isCold) {
      decksLoading.value = true;
      await srsStore.refreshOverview(true);
      decksLoading.value = false;
    } else {
      srsStore.refreshOverview();
    }
    srsStore.fetchReviewForecast30d();
  });

  async function refresh() {
    refreshing.value = true;
    await srsStore.refreshOverview(true);
    refreshing.value = false;
  }

  function openEdit(deck: StudyDeckDto) {
    editingDeck.value = deck;
    showEditDialog.value = true;
  }

  function openDownload(deck: StudyDeckDto) {
    downloadDeck.value = deck;
    showDownloadDialog.value = true;
  }

  function confirmRemove(id: number, title: string) {
    confirm.require({
      message: `Remove "${title}" from your study list? Your existing cards and progress will be kept.`,
      header: 'Remove Study Deck',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      accept: async () => {
        try {
          await srsStore.removeStudyDeck(id);
          toast.add({ severity: 'info', summary: 'Deck removed', life: 2000 });
        } catch {
          toast.add({ severity: 'error', summary: 'Failed to remove deck', life: 3000 });
        }
      },
    });
  }

  function startStudy() {
    srsStore.resetSession();
    router.push('/srs/study');
  }

  function getCoverUrl(coverName?: string) {
    if (!coverName || coverName === 'nocover.jpg') return null;
    return coverName;
  }

  const orderLabels: Record<number, string> = {
    [DeckOrder.Chronological]: 'Chronological',
    [DeckOrder.GlobalFrequency]: 'Global frequency',
    [DeckOrder.DeckFrequency]: 'Deck frequency',
    [DeckOrder.ImportOrder]: 'Import order',
    [DeckOrder.Random]: 'Random',
  };

  function pct(count: number, total: number) {
    return total > 0 ? ((count / total) * 100).toFixed(1) : '0.0';
  }

  function knownPct(deck: StudyDeckDto) {
    return pct(deck.masteredCount + deck.reviewCount, deck.totalWords);
  }

  function maturePct(deck: StudyDeckDto) {
    return pct(deck.masteredCount + deck.matureCount, deck.totalWords);
  }

  function combinedPct(deck: StudyDeckDto) {
    return pct(deck.masteredCount + deck.reviewCount + deck.learningCount, deck.totalWords);
  }

  const newCardDeckIds = computed(() => {
    const ids = new Set<number>();
    const ds = srsStore.dueSummary;
    if (!ds || ds.newCardsAvailable <= 0) return ids;

    const active = srsStore.activeDecks;
    if (srsStore.studySettings.newCardGathering === 'TopDeck') {
      for (const d of active) {
        if (d.unseenCount > 0) {
          ids.add(d.userStudyDeckId);
          break;
        }
      }
    } else {
      for (const d of active) {
        if (d.unseenCount > 0) ids.add(d.userStudyDeckId);
      }
    }
    return ids;
  });

  const deckUsage = computed(() => srsStore.studyDecks.length);
  const staticWordUsage = computed(() =>
    srsStore.studyDecks.filter((d) => d.deckType === StudyDeckType.StaticWordList).reduce((sum, d) => sum + d.totalWords, 0)
  );
  const hasStaticDecks = computed(() => srsStore.studyDecks.some((d) => d.deckType === StudyDeckType.StaticWordList));

  function usageColor(current: number, max: number) {
    const ratio = current / max;
    if (ratio >= 0.95) return 'text-red-500 dark:text-red-400';
    if (ratio >= 0.8) return 'text-yellow-500 dark:text-yellow-400';
    return 'text-gray-500 dark:text-gray-400';
  }

  const {
    dragIndex: activeDragIndex,
    dropIndex: activeDropIndex,
    handlePointerDown: activePointerDown,
  } = useTouchReorder({
    containerRef: activeDeckListRef,
    onReorder(from, to) {
      const decks = [...srsStore.activeDecks];
      const [moved] = decks.splice(from, 1);
      decks.splice(to, 0, moved);
      srsStore.reorderStudyDecks([...decks, ...srsStore.inactiveDecks]);
    },
  });

  const {
    dragIndex: inactiveDragIndex,
    dropIndex: inactiveDropIndex,
    handlePointerDown: inactivePointerDown,
  } = useTouchReorder({
    containerRef: inactiveDeckListRef,
    onReorder(from, to) {
      const decks = [...srsStore.inactiveDecks];
      const [moved] = decks.splice(from, 1);
      decks.splice(to, 0, moved);
      srsStore.reorderStudyDecks([...srsStore.activeDecks, ...decks]);
    },
  });

  async function moveActiveDeck(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= srsStore.activeDecks.length) return;
    const decks = [...srsStore.activeDecks];
    [decks[index], decks[target]] = [decks[target], decks[index]];
    await srsStore.reorderStudyDecks([...decks, ...srsStore.inactiveDecks]);
  }

  async function moveInactiveDeck(index: number, direction: -1 | 1) {
    const target = index + direction;
    if (target < 0 || target >= srsStore.inactiveDecks.length) return;
    const decks = [...srsStore.inactiveDecks];
    [decks[index], decks[target]] = [decks[target], decks[index]];
    await srsStore.reorderStudyDecks([...srsStore.activeDecks, ...decks]);
  }

  const nextReviewText = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds?.nextReviewAt) return null;
    const next = new Date(ds.nextReviewAt);
    const diffMs = next.getTime() - Date.now();
    if (diffMs <= 0) return 'now';
    const diffMin = Math.floor(diffMs / 60000);
    if (diffMin < 60) return `${diffMin}m`;
    const diffHr = Math.floor(diffMin / 60);
    if (diffHr < 24) return `${diffHr}h ${diffMin % 60}m`;
    return `${Math.floor(diffHr / 24)}d ${diffHr % 24}h`;
  });

  const totalDue = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds) return 0;
    return Math.min(ds.reviewsDue, ds.reviewBudgetLeft) + ds.newCardsAvailable;
  });

  const goalReviewsDone = computed(() => srsStore.dueSummary?.reviewsToday ?? 0);
  const goalReviewsTarget = computed(() => {
    const ds = srsStore.dueSummary;
    if (!ds) return 0;
    return ds.reviewsToday + Math.min(ds.reviewsDue, ds.reviewBudgetLeft);
  });
  const goalNewDone = computed(() => srsStore.dueSummary?.newCardsToday ?? 0);
  const goalNewTarget = computed(() => srsStore.studySettings.newCardsPerDay);

  const CELL = 10;
  const GAP = 2;
  const WEEKS = 12;

  interface MiniDay {
    date: string;
    count: number;
    dow: number;
    weekIdx: number;
  }

  const miniHeatmap = computed<MiniDay[]>(() => {
    const ds = srsStore.deckStreak;
    if (!ds || ds.recentDays.length === 0) return [];

    const countMap = new Map<string, number>();
    for (const d of ds.recentDays) countMap.set(d.date, d.count);

    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const todayDow = (today.getDay() + 6) % 7; // Mon=0
    const startOffset = (WEEKS - 1) * 7 + todayDow;

    const days: MiniDay[] = [];
    for (let i = startOffset; i >= 0; i--) {
      const d = new Date(today);
      d.setDate(d.getDate() - i);
      const dateStr = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
      const dow = (d.getDay() + 6) % 7;
      const weekIdx = Math.floor((startOffset - i) / 7);
      days.push({ date: dateStr, count: countMap.get(dateStr) ?? 0, dow, weekIdx });
    }
    return days;
  });

  const miniMaxCount = computed(() => {
    let max = 0;
    for (const d of miniHeatmap.value) {
      if (d.count > max) max = d.count;
    }
    return max || 1;
  });

  const miniTooltip = ref({ visible: false, text: '', x: 0, y: 0 });

  function showMiniTooltip(event: MouseEvent, day: MiniDay) {
    const d = new Date(day.date + 'T00:00:00');
    const dateStr = d.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
    miniTooltip.value = {
      visible: true,
      text: day.count === 0 ? `${dateStr}: No reviews` : `${dateStr}: ${day.count} reviews`,
      x: event.clientX,
      y: event.clientY,
    };
  }

  function hideMiniTooltip() {
    miniTooltip.value.visible = false;
  }

  function miniIntensity(count: number): string {
    if (count <= 0) return 'bg-surface-200 dark:bg-surface-700';
    const ratio = count / miniMaxCount.value;
    if (ratio <= 0.25) return 'bg-purple-200 dark:bg-purple-800';
    if (ratio <= 0.5) return 'bg-purple-400 dark:bg-purple-600';
    if (ratio <= 0.75) return 'bg-purple-500 dark:bg-purple-500';
    return 'bg-purple-700 dark:bg-purple-400';
  }

  const forecastNext7d = computed(() => srsStore.reviewForecast30d?.days.slice(0, 7).reduce((s, d) => s + d.count, 0) ?? 0);
  const forecastNext30d = computed(() => srsStore.reviewForecast30d?.days.reduce((s, d) => s + d.count, 0) ?? 0);

  const forecastChartData = computed(() => {
    const forecast = srsStore.reviewForecast30d;
    if (!forecast || forecast.days.length === 0) return null;
    return {
      labels: forecast.days.map((d) => {
        const date = new Date(d.date + 'T00:00:00');
        return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
      }),
      datasets: [
        {
          // Zero days → null so chart.js draws no bar (minBarLength would otherwise stub them);
          // non-zero days keep the minBarLength floor so a 1-review day stays visible next to a 180 spike.
          data: forecast.days.map((d) => (d.count === 0 ? null : d.count)),
          backgroundColor: forecast.days.map((_, i) => (i === 0 ? 'rgba(59, 130, 246, 0.8)' : 'rgba(168, 85, 247, 0.6)')),
          borderRadius: 3,
          minBarLength: 4,
        },
      ],
    };
  });

  const forecastChartOptions = {
    responsive: true,
    maintainAspectRatio: false,
    // Headroom inside the canvas so the always-on label above the first bar isn't clipped.
    layout: { padding: { top: 14 } },
    // Hover/tap anywhere along a column reveals that day — no need to hit the thin bar.
    interaction: { mode: 'index' as const, intersect: false },
    plugins: {
      legend: { display: false },
      // Always show the first (current) day's count so it's readable without hovering (mobile-friendly).
      datalabels: {
        display: (ctx: any) => ctx.dataIndex === 0,
        anchor: 'end' as const,
        align: 'end' as const,
        offset: 0,
        clamp: true,
        clip: false,
        color: 'rgb(59, 130, 246)',
        font: { weight: 'bold' as const, size: 11 },
        formatter: (value: number) => value,
      },
      tooltip: {
        mode: 'index' as const,
        intersect: false,
        backgroundColor: 'rgba(0, 0, 0, 0.8)',
        titleColor: '#fff',
        bodyColor: '#fff',
        borderColor: 'rgba(168, 85, 247, 1)',
        borderWidth: 1,
        padding: 10,
        displayColors: false,
        callbacks: {
          title: (context: any) => {
            const idx = context[0].dataIndex;
            const day = srsStore.reviewForecast30d?.days[idx];
            if (!day) return '';
            const date = new Date(day.date + 'T00:00:00');
            return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' });
          },
          label: (context: any) => {
            const count = srsStore.reviewForecast30d?.days[context.dataIndex]?.count ?? 0;
            return `${count} review${count !== 1 ? 's' : ''}`;
          },
        },
      },
    },
    scales: {
      x: {
        grid: { display: false },
        ticks: { maxRotation: 45, autoSkip: true, maxTicksLimit: 10 },
      },
      y: {
        beginAtZero: true,
        grace: '15%',
        grid: { color: 'rgba(0, 0, 0, 0.06)' },
        ticks: { precision: 0 },
      },
    },
  };
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <SrsSubNav />
    <div class="flex flex-wrap items-center justify-between gap-2 mb-4 min-h-[2.5rem]">
      <h1 class="text-2xl font-bold">Study Decks</h1>
      <div class="flex gap-2">
        <Tooltip content="Refresh decks and due counts" placement="bottom">
          <Button icon="pi pi-refresh" severity="secondary" :loading="refreshing" @click="refresh" />
        </Tooltip>
        <Button icon="pi pi-plus" label="Add Deck" class="!hidden sm:!inline-flex" @click="showAddDialog = true" />
        <Button icon="pi pi-plus" class="sm:!hidden" @click="showAddDialog = true" />
      </div>
    </div>

    <!-- Due Summary Skeleton -->
    <div
      v-if="!srsStore.dueSummary"
      class="mb-6 rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-hidden"
    >
      <div class="flex flex-col md:flex-row">
        <div class="flex items-center gap-3 py-3 px-4 border-b md:border-b-0 md:border-r border-surface-200 dark:border-surface-700">
          <Skeleton shape="circle" width="96px" height="96px" />
          <div class="flex flex-col gap-2">
            <Skeleton width="4rem" height="0.75rem" />
            <Skeleton width="3rem" height="0.75rem" />
          </div>
        </div>
        <div class="grid grid-cols-3 divide-x divide-surface-200 dark:divide-surface-700 flex-1">
          <div v-for="i in 3" :key="i" class="flex flex-col items-center justify-center gap-2 py-3 px-3">
            <Skeleton width="2.5rem" height="2rem" />
            <Skeleton width="3rem" height="0.75rem" />
          </div>
        </div>
      </div>
    </div>

    <!-- Due Summary Banner -->
    <div v-else class="mb-6 rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm overflow-hidden">
      <div class="flex flex-col md:flex-row">
        <!-- Today zone: progress ring -->
        <div class="flex flex-col items-center md:items-start gap-1.5 py-3 px-4 border-b md:border-b-0 md:border-r border-surface-200 dark:border-surface-700">
          <span class="text-[10px] font-semibold uppercase tracking-wide text-surface-400 dark:text-surface-500">Today</span>
          <GoalRing
            :reviews-done="goalReviewsDone"
            :reviews-target="goalReviewsTarget"
            :new-done="goalNewDone"
            :new-target="goalNewTarget"
            :total-to-study="totalDue"
          />
        </div>

        <!-- Due now zone: remaining queue -->
        <div class="flex-1 flex flex-col">
          <span class="text-[10px] font-semibold uppercase tracking-wide text-surface-400 dark:text-surface-500 px-3 pt-3">Due now</span>
          <div class="grid grid-cols-3 divide-x divide-surface-200 dark:divide-surface-700 flex-1">
            <button
              class="flex flex-col items-center justify-center gap-0.5 py-3 px-3 transition-colors hover:bg-surface-50 dark:hover:bg-surface-800"
              :class="srsStore.dueSummary.reviewsDue > 0 ? 'cursor-pointer' : 'opacity-50 cursor-default'"
              @click="srsStore.dueSummary!.reviewsDue > 0 && startStudy()"
            >
              <span
                class="text-[clamp(0.95rem,5vw,1.5rem)] font-bold tabular-nums"
                :class="srsStore.dueSummary.reviewsDue > 0 ? 'text-blue-600 dark:text-blue-400' : 'text-gray-400 dark:text-gray-500'"
                >{{ srsStore.dueSummary.reviewsDue }}</span
              >
              <span class="text-xs text-gray-500 dark:text-gray-400">Reviews</span>
            </button>

            <button
              class="flex flex-col items-center justify-center gap-0.5 py-3 px-3 transition-colors hover:bg-surface-50 dark:hover:bg-surface-800"
              :class="srsStore.dueSummary.newCardsAvailable > 0 ? 'cursor-pointer' : 'opacity-50 cursor-default'"
              @click="srsStore.dueSummary!.newCardsAvailable > 0 && startStudy()"
            >
              <span
                class="text-[clamp(0.95rem,5vw,1.5rem)] font-bold tabular-nums"
                :class="srsStore.dueSummary.newCardsAvailable > 0 ? 'text-green-400 dark:text-green-600' : 'text-gray-400 dark:text-gray-500'"
                >{{ srsStore.dueSummary.newCardsAvailable }}</span
              >
              <span class="text-xs text-gray-500 dark:text-gray-400">New</span>
            </button>

            <div class="flex flex-col items-center justify-center gap-0.5 py-3 px-3">
              <span class="text-base font-bold tabular-nums text-gray-600 dark:text-gray-300">{{ nextReviewText ?? '—' }}</span>
              <span class="text-xs text-gray-500 dark:text-gray-400">Next review</span>
            </div>
          </div>
        </div>
      </div>
    </div>

    <!-- Streak & Mini Heatmap -->
    <div
      v-if="
        (srsStore.deckStreak && srsStore.deckStreak.totalReviewDays > 0) ||
        (srsStore.studySettings.showReviewForecast && forecastChartData && srsStore.reviewForecast30d?.days.some((d) => d.count > 0))
      "
      class="mb-6 rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4"
    >
      <div class="flex flex-col md:flex-row gap-4">
        <!-- Left: streak + heatmap -->
        <div v-if="srsStore.studySettings.showReviewActivity" class="flex-1 flex flex-col gap-3">
          <div v-if="srsStore.deckStreak && srsStore.deckStreak.totalReviewDays > 0" class="flex flex-wrap items-center gap-x-5 gap-y-3">
            <!-- Streak -->
            <div class="flex items-center gap-2">
              <Icon name="material-symbols:local-fire-department" size="1.5rem" class="text-orange-500" />
              <span class="text-xl font-bold tabular-nums">{{ srsStore.deckStreak.currentStreak }}</span>
              <span class="text-sm text-gray-500">day streak</span>
            </div>
            <div v-if="srsStore.deckStreak.isNewRecord && srsStore.deckStreak.currentStreak > 1" class="text-xs font-semibold text-orange-500">New record!</div>
            <div class="text-sm text-gray-500">
              Longest: <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ srsStore.deckStreak.longestStreak }}</span>
            </div>
            <div class="text-sm text-gray-500">
              Days studied: <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ srsStore.deckStreak.totalReviewDays }}</span>
            </div>
          </div>
          <!-- Mini Heatmap -->
          <div v-if="miniHeatmap.length > 0" class="overflow-x-auto flex flex-col items-start">
            <div class="text-xs text-gray-500 mb-1">Review activity</div>
            <div class="relative" :style="{ width: `${WEEKS * (CELL + GAP) - GAP}px`, height: `${7 * (CELL + GAP) - GAP}px` }">
              <div
                v-for="(day, i) in miniHeatmap"
                :key="i"
                class="absolute rounded-sm"
                :class="miniIntensity(day.count)"
                :style="{
                  left: `${day.weekIdx * (CELL + GAP)}px`,
                  top: `${day.dow * (CELL + GAP)}px`,
                  width: `${CELL}px`,
                  height: `${CELL}px`,
                }"
                @mouseenter="showMiniTooltip($event, day)"
                @mouseleave="hideMiniTooltip"
              />
            </div>
          </div>
        </div>

        <!-- Right: forecast totals + chart -->
        <div
          v-if="srsStore.studySettings.showReviewForecast && forecastChartData && srsStore.reviewForecast30d?.days.some((d) => d.count > 0)"
          class="w-full md:w-1/2 flex-shrink-0 min-w-0 flex flex-col gap-3"
        >
          <div class="flex flex-wrap items-center gap-x-5 gap-y-3">
            <div class="text-sm text-gray-500">
              Next 7d: <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ forecastNext7d }}</span>
            </div>
            <div class="text-sm text-gray-500">
              Next 30d: <span class="font-semibold text-gray-700 dark:text-gray-300 tabular-nums">{{ forecastNext30d }}</span>
            </div>
          </div>
          <div class="text-xs text-gray-500 dark:text-gray-400 mb-1">Upcoming reviews</div>
          <div :style="{ height: `${7 * (CELL + GAP) - GAP + 24}px` }">
            <Bar :data="forecastChartData" :options="forecastChartOptions" :plugins="[ChartDataLabels]" />
          </div>
        </div>
      </div>
    </div>

    <!-- Stats link-tile -->
    <div v-if="srsStore.studySettings.showReviewActivity && srsStore.deckStreak && srsStore.deckStreak.totalReviewDays > 0" class="mb-6">
      <NuxtLink
        to="/srs/stats"
        class="flex items-center justify-between gap-3 rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4 transition-colors hover:bg-surface-50 dark:hover:bg-surface-800"
      >
        <div class="flex items-center gap-3 min-w-0">
          <Icon name="material-symbols:bar-chart-4-bars" size="1.5rem" class="text-primary-500 flex-shrink-0" />
          <div class="min-w-0">
            <div class="font-semibold">Stats</div>
            <div class="text-xs text-gray-500 dark:text-gray-400">Retention, difficulty, stability, forecast and more</div>
          </div>
        </div>
        <span class="flex items-center gap-1 text-sm text-primary-600 dark:text-primary-400 flex-shrink-0">
          View <Icon name="material-symbols:arrow-forward-rounded" size="1.1rem" />
        </span>
      </NuxtLink>
    </div>

    <!-- Deck Skeletons -->
    <div v-if="decksLoading && !srsStore.studyDecks.length" class="flex flex-col gap-3">
      <div
        v-for="i in 3"
        :key="i"
        class="flex items-center gap-4 p-4 bg-surface-0 dark:bg-surface-900 rounded-xl shadow-sm border border-surface-200 dark:border-surface-700"
      >
        <Skeleton width="64px" height="80px" borderRadius="4px" />
        <div class="flex-1">
          <Skeleton width="60%" height="1.2rem" class="mb-2" />
          <Skeleton width="40%" height="0.9rem" class="mb-3" />
          <Skeleton width="100%" height="1.5rem" borderRadius="8px" />
        </div>
      </div>
    </div>

    <!-- Error state -->
    <div v-else-if="!decksLoading && srsStore.fetchError" class="text-center py-16">
      <div class="text-red-400 text-lg mb-4">{{ srsStore.fetchError }}</div>
      <Button icon="pi pi-refresh" label="Retry" @click="srsStore.fetchStudyDecks()" />
    </div>

    <!-- Empty state -->
    <div v-else-if="!decksLoading && srsStore.studyDecks.length === 0" class="text-center py-16">
      <div class="text-gray-400 text-lg mb-4">No study decks yet</div>
      <p class="text-gray-500 mb-6">Add decks to start learning vocabulary with spaced repetition.</p>
      <Button icon="pi pi-plus" label="Add Your First Deck" @click="showAddDialog = true" />
    </div>

    <template v-else-if="srsStore.studyDecks.length > 0">
      <!-- Usage -->
      <div class="flex items-center gap-1 mb-3 text-xs">
        <Tooltip
          content="Current usage of your study deck limits.<br>Word counts are from word list decks only.<br>These limits are subject to change."
          placement="bottom"
        >
          <div class="flex items-center gap-3">
            <span :class="usageColor(deckUsage, 50)">
              <span class="font-semibold tabular-nums">{{ deckUsage }}</span
              ><span class="opacity-60">/50 decks</span>
            </span>
            <span v-if="hasStaticDecks" :class="usageColor(staticWordUsage, 200_000)">
              <span class="font-semibold tabular-nums">{{ staticWordUsage.toLocaleString() }}</span
              ><span class="opacity-60">/200K custom words</span>
            </span>
          </div>
        </Tooltip>
      </div>

      <!-- Active Decks -->
      <div v-if="srsStore.activeDecks.length > 0">
        <h3 v-if="srsStore.inactiveDecks.length > 0" class="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-2">
          Active Decks
        </h3>
        <div ref="activeDeckListRef" class="flex flex-col gap-3">
          <div
            v-for="(deck, index) in srsStore.activeDecks"
            :key="deck.userStudyDeckId"
            class="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4 p-4 bg-surface-0 dark:bg-surface-900 rounded-xl shadow-sm border border-surface-200 dark:border-surface-700 transition-opacity"
            :class="{
              'opacity-50': activeDragIndex === index,
              'border-purple-400 dark:border-purple-500': activeDropIndex === index && activeDragIndex !== index,
              'border-l-3 border-l-green-300 dark:border-l-green-700': newCardDeckIds.has(deck.userStudyDeckId),
            }"
          >
            <div class="flex items-center gap-4 flex-1 min-w-0">
              <!-- Drag handle -->
              <div
                v-if="srsStore.activeDecks.length > 1"
                class="flex-shrink-0 cursor-grab active:cursor-grabbing text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                style="touch-action: none"
                @pointerdown="activePointerDown($event, index)"
              >
                <Icon name="material-symbols:drag-indicator" size="20" />
              </div>

              <!-- Cover -->
              <NuxtLink
                v-if="deck.deckType === StudyDeckType.MediaDeck && deck.deckId"
                :to="`/decks/media/${deck.deckId}/detail`"
                class="w-16 h-20 flex-shrink-0 rounded overflow-hidden bg-surface-100 dark:bg-surface-700"
              >
                <img
                  v-if="getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName)"
                  :src="(getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName))!"
                  :alt="deck.title"
                  class="w-full h-full object-cover"
                />
                <div v-else class="w-full h-full flex items-center justify-center text-gray-400">
                  <Icon name="material-symbols:book-2" size="24" />
                </div>
              </NuxtLink>
              <div v-else class="w-16 h-20 flex-shrink-0 rounded overflow-hidden bg-surface-100 dark:bg-surface-700">
                <div v-if="deck.deckType === StudyDeckType.GlobalDynamic" class="w-full h-full flex items-center justify-center text-blue-400">
                  <Icon name="material-symbols:language" size="28" />
                </div>
                <div v-else-if="deck.deckType === StudyDeckType.MediaDeck">
                  <img
                    v-if="getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName)"
                    :src="(getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName))!"
                    :alt="deck.title"
                    class="w-full h-full object-cover"
                  />
                  <div v-else class="w-full h-full flex items-center justify-center text-gray-400">
                    <Icon name="material-symbols:book-2" size="24" />
                  </div>
                </div>
                <div v-else class="w-full h-full flex items-center justify-center text-green-400">
                  <Icon name="material-symbols:list-alt" size="28" />
                </div>
              </div>

              <!-- Info -->
              <div class="flex-1 min-w-0">
                <div v-if="deck.parentTitle" class="text-xs text-surface-400 dark:text-surface-500 truncate">
                  {{ localiseTitle({ originalTitle: deck.parentTitle, romajiTitle: deck.parentRomajiTitle, englishTitle: deck.parentEnglishTitle }) }}
                </div>
                <div class="font-semibold truncate">
                  <NuxtLink
                    v-if="deck.deckType === StudyDeckType.MediaDeck && deck.deckId"
                    :to="`/decks/media/${deck.deckId}/detail`"
                    class="hover:text-primary-500 transition-colors"
                  >
                    {{ localiseTitle({ originalTitle: deck.title, romajiTitle: deck.romajiTitle, englishTitle: deck.englishTitle }) }}
                  </NuxtLink>
                  <template v-else-if="deck.deckType === StudyDeckType.MediaDeck">
                    {{ localiseTitle({ originalTitle: deck.title, romajiTitle: deck.romajiTitle, englishTitle: deck.englishTitle }) }}
                  </template>
                  <template v-else>{{ deck.name }}</template>
                </div>
                <div class="text-sm text-gray-500">
                  <template v-if="deck.deckType === StudyDeckType.MediaDeck">{{ getMediaTypeText(deck.mediaType) }}</template>
                  <template v-else-if="deck.deckType === StudyDeckType.GlobalDynamic">Global Frequency</template>
                  <template v-else>Word List</template>
                  <span v-if="deck.totalWords"> · {{ deck.totalWords }} words</span>
                  <span v-if="orderLabels[deck.order]"> · {{ orderLabels[deck.order] }}</span>
                  <span v-if="deck.description"> · {{ deck.description }}</span>
                  <span v-if="newCardDeckIds.has(deck.userStudyDeckId)" class="text-green-400 dark:text-green-600 font-medium"> · New cards from here</span>
                </div>
                <div v-if="deck.totalWords > 0" class="mt-2">
                  <div class="relative w-full bg-surface-200 dark:bg-surface-700 rounded-lg h-6 overflow-hidden">
                    <div class="absolute bg-purple-500/30 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedPct(deck) + '%' }" />
                    <div class="absolute bg-purple-500/60 h-6 rounded-lg transition-all duration-700" :style="{ width: knownPct(deck) + '%' }" />
                    <div class="absolute bg-purple-600 h-6 rounded-lg transition-all duration-700" :style="{ width: maturePct(deck) + '%' }" />
                    <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold z-10 text-white drop-shadow-[0_0_2px_rgba(0,0,0,0.6)]">
                      {{ knownPct(deck) }}%
                    </span>
                  </div>
                  <div class="flex gap-3 mt-1 text-xs text-gray-500 flex-wrap">
                    <span>{{ deck.unseenCount }} unknown</span>
                    <span class="text-purple-400">{{ deck.learningCount }} learning</span>
                    <span class="text-purple-500">{{ deck.youngCount }} young</span>
                    <span class="text-purple-700 dark:text-purple-300">{{ deck.matureCount + deck.masteredCount }} mature</span>
                    <span v-if="deck.dueReviewCount > 0" class="text-blue-500 font-semibold">{{ deck.dueReviewCount }} due</span>
                  </div>
                  <div v-if="deck.warning" class="text-xs text-yellow-500 mt-1">{{ deck.warning }}</div>
                </div>
              </div>
            </div>

            <!-- Actions -->
            <div class="flex gap-1 flex-shrink-0 items-center justify-end sm:justify-start">
              <div v-if="srsStore.activeDecks.length > 1" class="flex flex-col">
                <Tooltip content="Move up" placement="top">
                  <Button icon="pi pi-chevron-up" text size="small" :disabled="index === 0" @click="moveActiveDeck(index, -1)" />
                </Tooltip>
                <Tooltip content="Move down" placement="top">
                  <Button icon="pi pi-chevron-down" text size="small" :disabled="index === srsStore.activeDecks.length - 1" @click="moveActiveDeck(index, 1)" />
                </Tooltip>
              </div>
              <Tooltip content="Deactivate" placement="top">
                <Button icon="pi pi-pause" severity="secondary" text size="small" @click="srsStore.toggleDeckActive(deck.userStudyDeckId)" />
              </Tooltip>
              <Tooltip content="Vocabulary" placement="top">
                <Button icon="pi pi-eye" severity="secondary" text size="small" @click="router.push(`/srs/decks/${deck.userStudyDeckId}/vocabulary`)" />
              </Tooltip>
              <Tooltip content="Edit" placement="top">
                <Button icon="pi pi-pencil" severity="secondary" text size="small" @click="openEdit(deck)" />
              </Tooltip>
              <Tooltip content="Download" placement="top">
                <Button icon="pi pi-download" severity="secondary" text size="small" @click="openDownload(deck)" />
              </Tooltip>
              <Tooltip content="Remove" placement="top">
                <Button
                  icon="pi pi-trash"
                  severity="danger"
                  text
                  size="small"
                  @click="confirmRemove(deck.userStudyDeckId, deck.deckType === 0 ? deck.title : deck.name)"
                />
              </Tooltip>
            </div>
          </div>
        </div>
      </div>

      <!-- No active decks warning -->
      <div v-else-if="srsStore.inactiveDecks.length > 0" class="text-center py-8 text-gray-500">
        <p>No active decks. New cards won't be added during study.</p>
      </div>

      <!-- Inactive Decks -->
      <div v-if="srsStore.inactiveDecks.length > 0" class="mt-6">
        <h3 class="text-sm font-semibold text-gray-500 dark:text-gray-400 uppercase tracking-wide mb-2">Inactive Decks</h3>
        <div ref="inactiveDeckListRef" class="flex flex-col gap-3">
          <div
            v-for="(deck, index) in srsStore.inactiveDecks"
            :key="deck.userStudyDeckId"
            class="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4 p-4 bg-surface-0 dark:bg-surface-900 rounded-xl shadow-sm border border-surface-200 dark:border-surface-700 transition-opacity opacity-60"
            :class="{
              '!opacity-30': inactiveDragIndex === index,
              'border-purple-400 dark:border-purple-500': inactiveDropIndex === index && inactiveDragIndex !== index,
            }"
          >
            <div class="flex items-center gap-4 flex-1 min-w-0">
              <!-- Drag handle -->
              <div
                v-if="srsStore.inactiveDecks.length > 1"
                class="flex-shrink-0 cursor-grab active:cursor-grabbing text-gray-400 hover:text-gray-600 dark:hover:text-gray-300"
                style="touch-action: none"
                @pointerdown="inactivePointerDown($event, index)"
              >
                <Icon name="material-symbols:drag-indicator" size="20" />
              </div>

              <!-- Cover -->
              <NuxtLink
                v-if="deck.deckType === StudyDeckType.MediaDeck && deck.deckId"
                :to="`/decks/media/${deck.deckId}/detail`"
                class="w-16 h-20 flex-shrink-0 rounded overflow-hidden bg-surface-100 dark:bg-surface-700"
              >
                <img
                  v-if="getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName)"
                  :src="(getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName))!"
                  :alt="deck.title"
                  class="w-full h-full object-cover"
                />
                <div v-else class="w-full h-full flex items-center justify-center text-gray-400">
                  <Icon name="material-symbols:book-2" size="24" />
                </div>
              </NuxtLink>
              <div v-else class="w-16 h-20 flex-shrink-0 rounded overflow-hidden bg-surface-100 dark:bg-surface-700">
                <div v-if="deck.deckType === StudyDeckType.GlobalDynamic" class="w-full h-full flex items-center justify-center text-blue-400">
                  <Icon name="material-symbols:language" size="28" />
                </div>
                <div v-else-if="deck.deckType === StudyDeckType.MediaDeck">
                  <img
                    v-if="getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName)"
                    :src="(getCoverUrl(deck.coverName) || getCoverUrl(deck.parentCoverName))!"
                    :alt="deck.title"
                    class="w-full h-full object-cover"
                  />
                  <div v-else class="w-full h-full flex items-center justify-center text-gray-400">
                    <Icon name="material-symbols:book-2" size="24" />
                  </div>
                </div>
                <div v-else class="w-full h-full flex items-center justify-center text-green-400">
                  <Icon name="material-symbols:list-alt" size="28" />
                </div>
              </div>

              <!-- Info -->
              <div class="flex-1 min-w-0">
                <div v-if="deck.parentTitle" class="text-xs text-surface-400 dark:text-surface-500 truncate">
                  {{ localiseTitle({ originalTitle: deck.parentTitle, romajiTitle: deck.parentRomajiTitle, englishTitle: deck.parentEnglishTitle }) }}
                </div>
                <div class="font-semibold truncate">
                  <NuxtLink
                    v-if="deck.deckType === StudyDeckType.MediaDeck && deck.deckId"
                    :to="`/decks/media/${deck.deckId}/detail`"
                    class="hover:text-primary-500 transition-colors"
                  >
                    {{ localiseTitle({ originalTitle: deck.title, romajiTitle: deck.romajiTitle, englishTitle: deck.englishTitle }) }}
                  </NuxtLink>
                  <template v-else-if="deck.deckType === StudyDeckType.MediaDeck">
                    {{ localiseTitle({ originalTitle: deck.title, romajiTitle: deck.romajiTitle, englishTitle: deck.englishTitle }) }}
                  </template>
                  <template v-else>{{ deck.name }}</template>
                </div>
                <div class="text-sm text-gray-500">
                  <template v-if="deck.deckType === StudyDeckType.MediaDeck">{{ getMediaTypeText(deck.mediaType) }}</template>
                  <template v-else-if="deck.deckType === StudyDeckType.GlobalDynamic">Global Frequency</template>
                  <template v-else>Word List</template>
                  <span v-if="deck.totalWords"> · {{ deck.totalWords }} words</span>
                  <span v-if="orderLabels[deck.order]"> · {{ orderLabels[deck.order] }}</span>
                  <span v-if="deck.description"> · {{ deck.description }}</span>
                </div>
                <div v-if="deck.totalWords > 0" class="mt-2">
                  <div class="relative w-full bg-surface-200 dark:bg-surface-700 rounded-lg h-6 overflow-hidden">
                    <div class="absolute bg-purple-500/30 h-6 rounded-lg transition-all duration-700" :style="{ width: combinedPct(deck) + '%' }" />
                    <div class="absolute bg-purple-500/60 h-6 rounded-lg transition-all duration-700" :style="{ width: knownPct(deck) + '%' }" />
                    <div class="absolute bg-purple-600 h-6 rounded-lg transition-all duration-700" :style="{ width: maturePct(deck) + '%' }" />
                    <span class="absolute inset-0 flex items-center pl-2 text-xs font-bold z-10 text-white drop-shadow-[0_0_2px_rgba(0,0,0,0.6)]">
                      {{ knownPct(deck) }}%
                    </span>
                  </div>
                  <div class="flex gap-3 mt-1 text-xs text-gray-500 flex-wrap">
                    <span>{{ deck.unseenCount }} unseen</span>
                    <span class="text-purple-400">{{ deck.learningCount }} learning</span>
                    <span class="text-purple-500">{{ deck.youngCount }} young</span>
                    <span class="text-purple-700 dark:text-purple-300">{{ deck.matureCount + deck.masteredCount }} mature</span>
                    <span v-if="deck.dueReviewCount > 0" class="text-blue-500 font-semibold">{{ deck.dueReviewCount }} due</span>
                  </div>
                  <div v-if="deck.warning" class="text-xs text-yellow-500 mt-1">{{ deck.warning }}</div>
                </div>
              </div>
            </div>

            <!-- Actions -->
            <div class="flex gap-1 flex-shrink-0 items-center justify-end sm:justify-start">
              <div v-if="srsStore.inactiveDecks.length > 1" class="flex flex-col">
                <Tooltip content="Move up" placement="top">
                  <Button icon="pi pi-chevron-up" text size="small" :disabled="index === 0" @click="moveInactiveDeck(index, -1)" />
                </Tooltip>
                <Tooltip content="Move down" placement="top">
                  <Button
                    icon="pi pi-chevron-down"
                    text
                    size="small"
                    :disabled="index === srsStore.inactiveDecks.length - 1"
                    @click="moveInactiveDeck(index, 1)"
                  />
                </Tooltip>
              </div>
              <Tooltip content="Activate" placement="top">
                <Button icon="pi pi-play" severity="success" text size="small" @click="srsStore.toggleDeckActive(deck.userStudyDeckId)" />
              </Tooltip>
              <Tooltip content="Vocabulary" placement="top">
                <Button icon="pi pi-eye" severity="secondary" text size="small" @click="router.push(`/srs/decks/${deck.userStudyDeckId}/vocabulary`)" />
              </Tooltip>
              <Tooltip content="Edit" placement="top">
                <Button icon="pi pi-pencil" severity="secondary" text size="small" @click="openEdit(deck)" />
              </Tooltip>
              <Tooltip content="Download" placement="top">
                <Button icon="pi pi-download" severity="secondary" text size="small" @click="openDownload(deck)" />
              </Tooltip>
              <Tooltip content="Remove" placement="top">
                <Button
                  icon="pi pi-trash"
                  severity="danger"
                  text
                  size="small"
                  @click="confirmRemove(deck.userStudyDeckId, deck.deckType === 0 ? deck.title : deck.name)"
                />
              </Tooltip>
            </div>
          </div>
        </div>
      </div>
    </template>

    <SrsAddDeckDialog v-model:visible="showAddDialog" />
    <SrsAddDeckDialog v-model:visible="showEditDialog" :edit-deck="editingDeck" />
    <MediaDeckDownloadDialog v-if="downloadDeck" v-model:visible="showDownloadDialog" :study-deck="downloadDeck" />

    <Teleport to="body">
      <div
        v-if="miniTooltip.visible"
        class="fixed z-50 px-2.5 py-1.5 rounded-md text-xs font-medium bg-gray-900 text-white dark:bg-gray-100 dark:text-gray-900 shadow-lg pointer-events-none whitespace-nowrap"
        :style="{ left: `${miniTooltip.x + 12}px`, top: `${miniTooltip.y - 32}px` }"
      >
        {{ miniTooltip.text }}
      </div>
    </Teleport>
  </div>
</template>
