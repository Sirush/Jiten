<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { FsrsRating } from '~/types';
  import type { StudyMoreParams } from '~/types';
  import { useStudyKeyboard } from '~/composables/useStudyKeyboard';
  import { useSwipeGesture } from '~/composables/useSwipeGesture';
  import { useToast } from 'primevue/usetoast';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Study' });

  const srsStore = useSrsStore();
  const router = useRouter();
  const confirm = useConfirm();
  const toast = useToast();
  const studyHeaderVisible = inject<Ref<boolean>>('studyHeaderVisible', ref(false));

  const hasActiveSession = computed(() =>
    srsStore.sessionStats.cardsReviewed > 0 && !srsStore.isSessionComplete
  );

  onBeforeRouteLeave(() => {
    if (hasActiveSession.value) {
      return window.confirm('You have an active study session. Leave without finishing?');
    }
  });

  const showSettingsDialog = ref(false);
  const settingsRef = ref<{ saveState: 'idle' | 'saving' | 'saved' | 'error'; save: () => void } | null>(null);
  const showProgressTooltip = ref(false);

  function dismissProgressTooltip() { showProgressTooltip.value = false; }

  onMounted(() => {
    window.addEventListener('beforeunload', onBeforeUnload);
    window.addEventListener('click', dismissProgressTooltip);
  });
  onUnmounted(() => {
    window.removeEventListener('beforeunload', onBeforeUnload);
    window.removeEventListener('click', dismissProgressTooltip);
  });

  function onBeforeUnload(e: BeforeUnloadEvent) {
    if (hasActiveSession.value) {
      e.preventDefault();
    }
  }

  const swipeCardRef = ref<HTMLElement | null>(null);
  const cardEntering = ref(false);

  const swipe = useSwipeGesture({
    elementRef: swipeCardRef,
    isEnabled: computed(() => srsStore.isFlipped && srsStore.studySettings.enableSwipeGesture),
    isBusy: computed(() => srsStore.isBusy),
    onSwipeComplete: (dir) => {
      handleGrade(dir === 'right' ? FsrsRating.Good : FsrsRating.Again);
    },
  });

  const swipeStyle = computed(() => {
    if (swipe.isDismissing.value) {
      return {
        transform: `translate(${swipe.offsetX.value}px, ${swipe.offsetY.value}px) rotate(${swipe.rotation.value}deg)`,
        opacity: 0,
      };
    }
    if (!swipe.isDragging.value && !swipe.isTransitioning.value) return {};
    return {
      transform: `translate(${swipe.offsetX.value}px, ${swipe.offsetY.value}px) rotate(${swipe.rotation.value}deg)`,
      opacity: swipe.cardOpacity.value,
    };
  });

  const OVERLAY_MAX = 0.15;
  const LABEL_MAX = 0.9;
  const rightOverlayOpacity = computed(() =>
    swipe.swipeDirection.value === 'right' ? swipe.swipeProgress.value * OVERLAY_MAX : 0
  );
  const leftOverlayOpacity = computed(() =>
    swipe.swipeDirection.value === 'left' ? swipe.swipeProgress.value * OVERLAY_MAX : 0
  );
  const rightLabelOpacity = computed(() =>
    swipe.swipeDirection.value === 'right' ? Math.max(0, (swipe.swipeProgress.value - 0.4) / 0.6) * LABEL_MAX : 0
  );
  const leftLabelOpacity = computed(() =>
    swipe.swipeDirection.value === 'left' ? Math.max(0, (swipe.swipeProgress.value - 0.4) / 0.6) * LABEL_MAX : 0
  );

  const cardKey = computed(() => {
    const c = srsStore.currentCard;
    return c ? `${c.wordId}-${c.readingIndex}` : '';
  });

  watch(cardKey, () => {
    const wasDismissing = swipe.isDismissing.value;
    swipe.resetInstant();
    if (wasDismissing) {
      cardEntering.value = true;
      setTimeout(() => { cardEntering.value = false; }, 250);
    }
  });

  const cardWidths = ['max-w-xl', 'max-w-3xl', 'max-w-5xl'] as const;
  const cardWidthLabels = ['S', 'M', 'L'] as const;
  const cardWidthIndex = ref(0);
  onMounted(() => {
    const stored = localStorage.getItem('srs-card-width');
    if (stored) cardWidthIndex.value = Math.min(Number(stored) || 0, 2);
  });
  function setCardWidth(index: number) {
    cardWidthIndex.value = index;
    localStorage.setItem('srs-card-width', String(index));
  }
  const cardWidthClass = computed(() => cardWidths[cardWidthIndex.value]);

  const studyThemes = [
    { id: 'rainbow', icon: 'material-symbols:palette-outline' },
    { id: 'mono', icon: 'material-symbols:contrast' },
  ] as const;
  type StudyThemeId = (typeof studyThemes)[number]['id'];
  const studyTheme = ref<StudyThemeId>('rainbow');
  const isMono = computed(() => studyTheme.value === 'mono');
  onMounted(() => {
    const stored = localStorage.getItem('srs-study-theme') as StudyThemeId | null;
    if (stored && studyThemes.some(t => t.id === stored)) studyTheme.value = stored;
  });
  function setStudyTheme(id: StudyThemeId) {
    studyTheme.value = id;
    localStorage.setItem('srs-study-theme', id);
  }

  const compactBar = ref(false);
  onMounted(() => {
    compactBar.value = localStorage.getItem('srs-compact-bar') === '1';
  });
  function toggleCompactBar() {
    compactBar.value = !compactBar.value;
    localStorage.setItem('srs-compact-bar', compactBar.value ? '1' : '0');
  }

  const bottomBarRef = ref<HTMLElement | null>(null);
  const bottomBarHeight = ref(0);
  let barObserver: ResizeObserver | null = null;
  onMounted(() => {
    barObserver = new ResizeObserver((entries) => {
      bottomBarHeight.value = entries[0]?.target.getBoundingClientRect().height ?? 0;
    });
    if (bottomBarRef.value) {
      barObserver.observe(bottomBarRef.value);
      bottomBarHeight.value = bottomBarRef.value.offsetHeight;
    }
  });
  watch(bottomBarRef, (el, oldEl) => {
    if (oldEl) barObserver?.unobserve(oldEl);
    if (el) { barObserver?.observe(el); bottomBarHeight.value = el.offsetHeight; }
  });
  onUnmounted(() => barObserver?.disconnect());

  const barColors = {
    rainbow: { easy: 'bg-blue-400', good: 'bg-emerald-400', hard: 'bg-orange-300', action: 'bg-gray-400', again: 'bg-red-400' },
    mono: { easy: 'bg-surface-500', good: 'bg-surface-400', hard: 'bg-surface-300', action: 'bg-surface-300', again: 'bg-surface-600 dark:bg-surface-500' },
  } as const;
  const dotColors = {
    rainbow: { again: 'bg-red-400', hard: 'bg-orange-300', good: 'bg-emerald-400', easy: 'bg-blue-400' },
    mono: { again: 'bg-surface-600', hard: 'bg-surface-400', good: 'bg-surface-400', easy: 'bg-surface-500' },
  } as const;

  const barSegments = computed(() => {
    const total = srsStore.progress.total;
    if (total === 0) return [];
    const counts = { hard: 0, good: 0, easy: 0, action: 0 };
    for (const g of srsStore.clearedGrades) counts[g]++;
    const c = barColors[studyTheme.value];
    const segments: { key: string; width: number; color: string }[] = [];
    if (counts.easy > 0) segments.push({ key: 'easy', width: (counts.easy / total) * 100, color: c.easy });
    if (counts.good > 0) segments.push({ key: 'good', width: (counts.good / total) * 100, color: c.good });
    if (counts.hard > 0) segments.push({ key: 'hard', width: (counts.hard / total) * 100, color: c.hard });
    if (counts.action > 0) segments.push({ key: 'action', width: (counts.action / total) * 100, color: c.action });
    if (srsStore.againCardsAhead > 0) segments.push({ key: 'again', width: (srsStore.againCardsAhead / total) * 100, color: c.again });
    return segments;
  });

  const { pressedKey } = useStudyKeyboard({
    onGrade: handleGrade,
    onBlacklist: handleBlacklist,
    onForget: handleForget,
    onMaster: handleMaster,
    onSuspend: handleSuspend,
    onBury: handleBury,
    onUndo: handleUndo,
    onWrapUp: handleWrapUp,
  });

  const elapsedSeconds = ref(0);
  let elapsedTimer: ReturnType<typeof setInterval> | null = null;
  function startElapsedTimer() {
    stopElapsedTimer();
    elapsedTimer = setInterval(() => {
      const start = srsStore.sessionStats.startTime;
      if (start) elapsedSeconds.value = Math.floor((Date.now() - start.getTime()) / 1000);
    }, 1000);
  }
  function stopElapsedTimer() {
    if (elapsedTimer) { clearInterval(elapsedTimer); elapsedTimer = null; }
  }
  const elapsedDisplay = computed(() => {
    const s = elapsedSeconds.value;
    const m = Math.floor(s / 60);
    const sec = s % 60;
    return m >= 60
      ? `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}:${String(sec).padStart(2, '0')}`
      : `${m}:${String(sec).padStart(2, '0')}`;
  });
  watch(() => srsStore.isSessionComplete, (done) => { if (done) stopElapsedTimer(); });
  onUnmounted(stopElapsedTimer);

  const loading = ref(true);

  onMounted(async () => {
    loading.value = true;
    await srsStore.fetchSettings();
    await srsStore.fetchBatch();
    srsStore.fetchDueSummary();
    loading.value = false;
    if (!srsStore.isSessionComplete && srsStore.currentCard) startElapsedTimer();
  });

  async function handleGrade(rating: FsrsRating) {
    const ok = await srsStore.gradeCard(rating);
    const wasDismissing = swipe.isDismissing.value;
    swipe.resetInstant();
    if (wasDismissing) {
      cardEntering.value = true;
      setTimeout(() => { cardEntering.value = false; }, 250);
    }
    if (!ok) toast.add({ severity: 'error', summary: 'Review failed', detail: 'Your grade was not saved. Try again.', life: 5000 });
  }

  watch(() => srsStore.lastLeechEvent, (event) => {
    if (!event) return;
    const summary = event.suspended ? 'Card marked as leech and suspended' : 'Card marked as leech';
    toast.add({ severity: 'warn', summary, life: 3000 });
  });

  watch(() => srsStore.lastReviewError, (err) => {
    if (!err) return;
    toast.add({ severity: 'error', summary: 'Review not saved due to an error', detail: `“${err.wordText}” will be shown again.`, life: 5000 });
  });

  function handleFlip() {
    srsStore.revealCard();
  }

  async function handleBlacklist() {
    const ok = await srsStore.quickAction('blacklist');
    if (!ok) toast.add({ severity: 'error', summary: 'Action failed', detail: 'Could not blacklist card. Try again.', life: 5000 });
  }

  async function handleMaster() {
    const ok = await srsStore.quickAction('master');
    if (!ok) toast.add({ severity: 'error', summary: 'Action failed', detail: 'Could not master card. Try again.', life: 5000 });
  }

  async function handleSuspend() {
    const ok = await srsStore.quickAction('suspend');
    if (!ok) toast.add({ severity: 'error', summary: 'Action failed', detail: 'Could not suspend card. Try again.', life: 5000 });
  }

  async function handleBury() {
    const ok = await srsStore.quickAction('bury');
    if (!ok) toast.add({ severity: 'error', summary: 'Action failed', detail: 'Could not bury card. Try again.', life: 5000 });
  }

  function handleForget() {
    confirm.require({
      message: 'Reset this card? All review history will be permanently deleted. This cannot be undone.',
      header: 'Forget Card',
      acceptLabel: 'Forget',
      rejectLabel: 'Cancel',
      accept: async () => {
        const ok = await srsStore.quickAction('forget');
        if (!ok) toast.add({ severity: 'error', summary: 'Action failed', detail: 'Could not reset card. Try again.', life: 5000 });
      },
    });
  }

  async function handleUndo() {
    const ok = await srsStore.undoLastAction();
    if (ok) toast.add({ severity: 'info', summary: 'Undone', life: 2000 });
    else toast.add({ severity: 'error', summary: 'Undo failed', detail: 'Could not undo last action. Try again.', life: 5000 });
  }

  function handleWrapUp() {
    if (srsStore.isWrappingUp) srsStore.cancelWrapUp();
    else srsStore.wrapUp();
  }

  async function exitStudy() {
    stopElapsedTimer();
    await router.push('/srs/decks');
    srsStore.resetSession();
  }

  const showStudyMoreDialog = ref(false);

  function studyMore() {
    showStudyMoreDialog.value = true;
  }

  async function handleStudyMoreSelect(params: StudyMoreParams) {
    showStudyMoreDialog.value = false;
    srsStore.startStudyMore(params);
    await srsStore.fetchBatch();
    if (!srsStore.isSessionComplete) startElapsedTimer();
  }

  // Human-readable time until the next review comes due (from the due summary).
  const nextReviewText = computed(() => {
    const at = srsStore.dueSummary?.nextReviewAt;
    if (!at) return null;
    const diffMs = new Date(at).getTime() - Date.now();
    if (diffMs <= 0) return 'now';
    const diffMin = Math.floor(diffMs / 60000);
    if (diffMin < 60) return `${diffMin}m`;
    const diffHr = Math.floor(diffMin / 60);
    if (diffHr < 24) return `${diffHr}h ${diffMin % 60}m`;
    return `${Math.floor(diffHr / 24)}d ${diffHr % 24}h`;
  });

  // Why is the queue empty? The batch returned nothing, so disambiguate the cause
  // using the due summary (raw due/budget counts) rather than just the new-card budget.
  type EmptyReason = 'reviewCap' | 'newLimit' | 'caughtUp';
  const emptyReason = computed<EmptyReason>(() => {
    const ds = srsStore.dueSummary;
    // Reviews are due right now but blocked by today's review cap.
    if (ds && ds.reviewsDue > 0 && ds.reviewBudgetLeft <= 0) return 'reviewCap';
    // Daily new-card budget used up — reviews are caught up, more new words may remain for tomorrow.
    if (srsStore.newCardsRemaining <= 0) return 'newLimit';
    // New-card budget remains but nothing was servable: every available word is tracked and
    // no reviews are due → genuinely caught up.
    return 'caughtUp';
  });
</script>

<template>
  <div class="flex-1 min-h-0 flex flex-col">
    <!-- Loading (card-front skeleton) -->
    <div v-if="loading" class="flex-1 overflow-y-auto min-h-0 flex flex-col items-center px-2 pt-4 md:pt-8">
      <!-- Progress row placeholder -->
      <div class="w-full mb-4 grid grid-cols-[1fr_auto_1fr] items-center" :class="cardWidthClass">
        <div class="flex items-center gap-2">
          <div class="h-5 w-14 rounded bg-surface-200 dark:bg-surface-700 animate-pulse" />
          <div class="h-5 w-16 rounded bg-surface-200 dark:bg-surface-700 animate-pulse" />
        </div>
        <div class="h-6 w-16 rounded bg-surface-200 dark:bg-surface-700 animate-pulse justify-self-center" />
        <div />
      </div>
      <!-- Card front placeholder -->
      <div class="w-full" :class="cardWidthClass">
        <div class="rounded-2xl shadow-lg dark:shadow-none border border-surface-200 dark:border-surface-700 p-6 md:p-8 flex flex-col items-center">
          <div class="h-4 w-16 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mb-6" />
          <div class="h-12 md:h-14 w-48 md:w-56 rounded-lg bg-surface-200 dark:bg-surface-700 animate-pulse" />
          <div class="h-4 w-40 rounded bg-surface-200 dark:bg-surface-700 animate-pulse mt-10" />
        </div>
      </div>
    </div>

    <!-- Error state -->
    <div v-else-if="srsStore.fetchError" class="flex-1 flex items-center justify-center px-2">
      <div class="text-center py-16">
        <div class="text-red-400 text-lg mb-4">{{ srsStore.fetchError }}</div>
        <div class="flex justify-center gap-3">
          <Button label="Retry" icon="pi pi-refresh" @click="srsStore.fetchBatch()" />
          <Button label="Back to Decks" severity="secondary" @click="exitStudy" />
        </div>
      </div>
    </div>

    <!-- Session complete -->
    <div v-else-if="srsStore.isSessionComplete" class="flex-1 flex items-center justify-center px-2">
      <SrsSessionSummary
        :cards-reviewed="srsStore.sessionStats.cardsReviewed"
        :new-cards-learned="srsStore.sessionStats.newCardsLearned"
        :correct-count="srsStore.sessionStats.correctCount"
        :start-time="srsStore.sessionStats.startTime"
        :hardest-cards="srsStore.hardestCards"
        :grade-counts="srsStore.sessionStats.gradeCounts"
        :leeches="srsStore.sessionLeeches"
        @close="exitStudy"
        @study-more="studyMore"
        @suspend-leech="(wordId, readingIndex) => srsStore.suspendLeech(wordId, readingIndex)"
      />
    </div>

    <!-- Study mode -->
    <template v-else-if="srsStore.currentCard || srsStore.isLoading">
      <!-- Scrollable content area -->
      <div class="flex-1 overflow-y-auto min-h-0 flex flex-col items-center px-2 pt-4 md:pt-8" :style="{ paddingBottom: `${bottomBarHeight + 16}px`, minHeight: `${bottomBarHeight + 200}px` }">
        <!-- Progress info -->
        <div class="w-full mb-4 grid grid-cols-[1fr_auto_1fr] items-center" :class="cardWidthClass">
          <div class="flex items-center gap-2">
            <span class="text-xs px-1.5 py-0.5 rounded bg-blue-100 dark:bg-blue-900/40 text-blue-600 dark:text-blue-300 tabular-nums">{{ srsStore.remainingByType.new }} new</span>
            <span class="text-xs px-1.5 py-0.5 rounded bg-emerald-100 dark:bg-emerald-900/40 text-emerald-600 dark:text-emerald-300 tabular-nums">{{ srsStore.remainingByType.review }} review</span>
            <span v-if="srsStore.isWrappingUp" class="text-xs px-1.5 py-0.5 rounded bg-amber-100 dark:bg-amber-900/40 text-amber-600 dark:text-amber-300 tabular-nums">{{ srsStore.progress.remaining }} left</span>
          </div>
          <div class="text-center">
            <span class="text-lg font-semibold tabular-nums text-surface-700 dark:text-surface-200">
              {{ srsStore.progress.current }}<span class="text-surface-400 dark:text-surface-500 font-normal"> / {{ srsStore.progress.total }}</span>
            </span>
            <div v-if="srsStore.studySettings.showElapsedTime" class="text-[11px] tabular-nums text-surface-400 dark:text-surface-500 -mt-0.5">{{ elapsedDisplay }}</div>
          </div>
          <div class="flex items-center gap-1 justify-end">
            <button
              v-for="theme in studyThemes"
              :key="theme.id"
              @click="setStudyTheme(theme.id)"
              class="md:hidden p-2 rounded-lg transition-colors cursor-pointer"
              :class="studyTheme === theme.id
                ? 'bg-indigo-500 text-white'
                : 'bg-gray-200/60 dark:bg-gray-800/60 text-gray-500 dark:text-gray-400 hover:bg-gray-300 dark:hover:bg-gray-700'"
              :aria-label="theme.id + ' theme'"
            >
              <Icon :name="theme.icon" size="18" />
            </button>
            <Tooltip :content="srsStore.isWrappingUp ? 'Cancel wrap-up' : 'Finish learning cards then end'">
              <button
                @click="handleWrapUp()"
                class="p-2 rounded-lg transition-colors"
                :class="srsStore.isWrappingUp
                  ? 'bg-amber-500 text-white hover:bg-amber-600'
                  : 'bg-gray-200/60 dark:bg-gray-800/60 hover:bg-gray-300 dark:hover:bg-gray-700 text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200'"
                :aria-label="srsStore.isWrappingUp ? 'Cancel wrap-up' : 'Wrap up session'"
              >
                <Icon :name="srsStore.isWrappingUp ? 'material-symbols:undo' : 'material-symbols:exit-to-app'" size="18" />
              </button>
            </Tooltip>
            <button
              @click="studyHeaderVisible = !studyHeaderVisible"
              class="p-2 rounded-lg bg-gray-200/60 dark:bg-gray-800/60 hover:bg-gray-300 dark:hover:bg-gray-700 text-gray-500 hover:text-gray-700 dark:text-gray-400 dark:hover:text-gray-200 transition-colors"
              aria-label="Toggle navigation"
            >
              <Icon :name="studyHeaderVisible ? 'material-symbols:close' : 'material-symbols:menu'" size="18" />
            </button>
          </div>
        </div>

        <!-- Progress indicator -->
        <div class="w-full mb-6" :class="cardWidthClass">
          <div class="group relative" @click.stop="showProgressTooltip = !showProgressTooltip">
            <div class="h-1.5 bg-surface-200 dark:bg-surface-700 rounded-full overflow-hidden flex cursor-pointer">
              <div
                v-for="seg in barSegments"
                :key="seg.key"
                class="h-full transition-all duration-300"
                :class="seg.color"
                :style="{ width: `${seg.width}%` }"
              />
            </div>
            <!-- Hover/tap tooltip -->
            <div class="absolute left-1/2 -translate-x-1/2 top-full mt-2 px-3 py-2 bg-gray-800 dark:bg-gray-100 text-white dark:text-gray-900 text-xs rounded-lg transition-opacity pointer-events-none z-10 whitespace-nowrap" :class="showProgressTooltip ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'">
              <div class="flex items-center gap-3">
                <span class="flex items-center gap-1">
                  <span class="inline-block w-2 h-2 rounded-full" :class="dotColors[studyTheme].again"></span>
                  Again {{ srsStore.sessionStats.gradeCounts.again }}
                </span>
                <span v-if="srsStore.studySettings.gradingButtons === 4" class="flex items-center gap-1">
                  <span class="inline-block w-2 h-2 rounded-full" :class="dotColors[studyTheme].hard"></span>
                  Hard {{ srsStore.sessionStats.gradeCounts.hard }}
                </span>
                <span class="flex items-center gap-1">
                  <span class="inline-block w-2 h-2 rounded-full" :class="dotColors[studyTheme].good"></span>
                  Good {{ srsStore.sessionStats.gradeCounts.good }}
                </span>
                <span class="flex items-center gap-1">
                  <span class="inline-block w-2 h-2 rounded-full" :class="dotColors[studyTheme].easy"></span>
                  Easy {{ srsStore.sessionStats.gradeCounts.easy }}
                </span>
              </div>
              <div class="mt-1 text-gray-300 dark:text-gray-500 text-center">
                {{ srsStore.progress.current }}/{{ srsStore.progress.total }} completed
                <template v-if="srsStore.againCardsAhead > 0"> · {{ srsStore.againCardsAhead }} again pending</template>
              </div>
              <div class="absolute bottom-full left-1/2 -translate-x-1/2 border-4 border-transparent border-b-gray-800 dark:border-b-gray-100"></div>
            </div>
          </div>
        </div>

        <!-- Card -->
        <div class="w-full" :class="cardWidthClass">
          <div
            v-if="srsStore.currentCard"
            ref="swipeCardRef"
            class="relative touch-pan-y"
            :class="{
              'swipe-transitioning': swipe.isTransitioning.value,
              'swipe-snap': swipe.isTransitioning.value && !swipe.isDismissing.value,
              'swipe-dismiss': swipe.isTransitioning.value && swipe.isDismissing.value,
              'select-none': swipe.isDragging.value,
              'will-change-transform': swipe.isDragging.value,
              'card-entering': cardEntering,
            }"
            :style="swipeStyle"
          >
            <!-- Green overlay (right swipe = Good) -->
            <div
              class="absolute inset-0 rounded-2xl pointer-events-none z-10"
              :class="isMono ? 'bg-surface-500' : 'bg-green-500'"
              :style="{ opacity: rightOverlayOpacity }"
            />
            <!-- Red overlay (left swipe = Again) -->
            <div
              class="absolute inset-0 rounded-2xl pointer-events-none z-10"
              :class="isMono ? 'bg-surface-600' : 'bg-red-500'"
              :style="{ opacity: leftOverlayOpacity }"
            />
            <!-- Direction labels (Tinder-style at edges) -->
            <div
              class="absolute top-6 right-4 font-black text-2xl uppercase tracking-widest pointer-events-none z-20 border-3 rounded-lg px-3 py-1 -rotate-12"
              :class="isMono ? 'text-surface-500 border-surface-500' : 'text-green-500 border-green-500'"
              :style="{ opacity: rightLabelOpacity }"
            >Good</div>
            <div
              class="absolute top-6 left-4 font-black text-2xl uppercase tracking-widest pointer-events-none z-20 border-3 rounded-lg px-3 py-1 rotate-12"
              :class="isMono ? 'text-surface-600 border-surface-600' : 'text-red-500 border-red-500'"
              :style="{ opacity: leftLabelOpacity }"
            >Again</div>

            <SrsStudyCard
              :card="srsStore.currentCard"
              :is-flipped="srsStore.isFlipped"
              @flip="handleFlip"
            />
          </div>
          <div v-else class="flex justify-center py-8">
            <ProgressSpinner style="width: 32px; height: 32px" />
          </div>
        </div>
      </div>

      <!-- Fixed bottom buttons -->
      <div ref="bottomBarRef" class="border-t border-surface-200 dark:border-surface-800 bg-surface-0 dark:bg-surface-900 fixed bottom-0 left-0 right-0 z-40 isolate" :class="compactBar ? 'px-3 pt-1.5 pb-2' : 'px-4 pt-4 pb-[max(1rem,env(safe-area-inset-bottom))]'">
        <div class="w-full mx-auto" :class="cardWidthClass">
          <SrsGradeButtons
            :grading-buttons="srsStore.studySettings.gradingButtons"
            :is-flipped="srsStore.isFlipped"
            :can-undo="srsStore.canUndo"
            :keybinds="srsStore.studySettings.keybinds"
            :monochrome="isMono"
            :interval-preview="srsStore.studySettings.showNextInterval ? srsStore.currentCard?.intervalPreview : undefined"
            :show-keybinds="srsStore.studySettings.showKeybinds"
            :show-swipe-hints="srsStore.studySettings.enableSwipeGesture"
            :disabled="srsStore.isBusy"
            :compact="compactBar"
            :pressed-key="pressedKey"
            @grade="handleGrade"
            @flip="handleFlip"
            @blacklist="handleBlacklist"
            @master="handleMaster"
            @suspend="handleSuspend"
            @bury="handleBury"
            @forget="handleForget"
            @undo="handleUndo"
            @settings="showSettingsDialog = true"
            @expand="toggleCompactBar"
          />
          <div v-if="!compactBar" class="mt-1 hidden md:flex items-center justify-center gap-2 text-xs text-gray-400">
            <div class="flex gap-0.5">
              <button
                v-for="(label, i) in cardWidthLabels"
                :key="label"
                @click="setCardWidth(i)"
                class="px-1.5 py-0.5 rounded text-[10px] font-medium transition-colors cursor-pointer"
                :class="cardWidthIndex === i
                  ? 'bg-indigo-500 text-white'
                  : 'bg-gray-200 dark:bg-gray-700 text-gray-500 dark:text-gray-400 hover:bg-gray-300 dark:hover:bg-gray-600'"
              >
                {{ label }}
              </button>
            </div>
            <div class="flex gap-0.5">
              <button
                v-for="theme in studyThemes"
                :key="theme.id"
                @click="setStudyTheme(theme.id)"
                class="p-1 rounded transition-colors cursor-pointer"
                :class="studyTheme === theme.id
                  ? 'bg-indigo-500 text-white'
                  : 'bg-gray-200 dark:bg-gray-700 text-gray-500 dark:text-gray-400 hover:bg-gray-300 dark:hover:bg-gray-600'"
                :aria-label="theme.id + ' theme'"
              >
                <Icon :name="theme.icon" size="14" />
              </button>
            </div>
            <button
              class="p-1 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 transition-colors cursor-pointer"
              aria-label="SRS Settings"
              @click="showSettingsDialog = true"
            >
              <Icon name="material-symbols:settings-outline" size="14" />
            </button>
            <button
              class="p-1 rounded text-gray-400 hover:text-gray-600 dark:hover:text-gray-200 transition-colors cursor-pointer"
              aria-label="Collapse bar"
              @click="toggleCompactBar"
            >
              <Icon name="material-symbols:keyboard-double-arrow-down" size="18" />
            </button>
          </div>
        </div>
      </div>
    </template>

    <!-- No cards available -->
    <div v-else class="flex-1 flex items-center justify-center px-2">
      <div class="text-center py-16">
        <Icon
          :name="emptyReason === 'reviewCap' ? 'material-symbols:hourglass-top' : 'material-symbols:check-circle'"
          size="3rem"
          class="mb-4"
          :class="emptyReason === 'reviewCap' ? 'text-amber-400 dark:text-amber-500' : 'text-gray-300 dark:text-gray-600'"
        />
        <div class="text-gray-400 text-lg mb-4">
          <template v-if="emptyReason === 'reviewCap'">Daily review limit reached</template>
          <template v-else-if="emptyReason === 'newLimit'">Daily new cards done</template>
          <template v-else>All caught up!</template>
        </div>
        <p class="text-gray-500 mb-6">
          <template v-if="emptyReason === 'reviewCap'">
            {{ srsStore.dueSummary?.reviewsDue }} review{{ srsStore.dueSummary?.reviewsDue === 1 ? '' : 's' }} waiting, but you've hit your daily cap of {{ srsStore.studySettings.maxReviewsPerDay }}. They'll be ready tomorrow or you can use Study More to push ahead.
          </template>
          <template v-else-if="emptyReason === 'newLimit'">
            You've added your {{ srsStore.studySettings.newCardsPerDay }} new card{{ srsStore.studySettings.newCardsPerDay === 1 ? '' : 's' }} for today.
            <template v-if="nextReviewText"> Next review in {{ nextReviewText }}.</template>
            <template v-else> Reviews are all caught up.</template>
          </template>
          <template v-else>
            <template v-if="nextReviewText">You're all caught up — next review in {{ nextReviewText }}.</template>
            <template v-else>Every available word is tracked and no reviews are due. Add a deck or adjust your filters to learn more.</template>
          </template>
        </p>
        <div class="flex justify-center gap-6 text-xs text-gray-400 mb-6">
          <span>New today: {{ srsStore.newCardsToday }}</span>
          <span>Reviews today: {{ srsStore.reviewsToday }}</span>
        </div>
        <div class="flex justify-center gap-3">
          <Button label="Study More" icon="pi pi-plus" @click="studyMore" />
          <Button label="Back to Decks" severity="secondary" @click="exitStudy" />
        </div>
      </div>
    </div>

    <Dialog v-model:visible="showSettingsDialog" header="Study Settings" :modal="true" class="w-full md:w-[36rem]">
      <SettingsSrsStudy v-if="showSettingsDialog" ref="settingsRef" inline />
      <template #footer>
        <div class="flex items-center justify-end w-full gap-2">
          <SrsSaveStatus class="mr-auto" :state="settingsRef?.saveState ?? 'idle'" @retry="() => settingsRef?.save()" />
          <Button label="Close" severity="secondary" @click="showSettingsDialog = false" />
        </div>
      </template>
    </Dialog>

    <SrsStudyMoreDialog
      v-model:visible="showStudyMoreDialog"
      :review-budget-hit="srsStore.reviewsToday >= srsStore.studySettings.maxReviewsPerDay"
      @select="handleStudyMoreSelect"
    />
  </div>
</template>

<style scoped>
.swipe-snap {
  transition: transform 0.4s cubic-bezier(0.175, 0.885, 0.32, 1.275), opacity 0.4s ease;
}
.swipe-dismiss {
  transition: transform 0.15s ease-in, opacity 0.15s ease-in;
}
@keyframes card-enter {
  from { opacity: 0; transform: scale(0.95) translateY(8px); }
  to { opacity: 1; transform: scale(1) translateY(0); }
}
.card-entering {
  animation: card-enter 0.2s ease-out;
}
@media (prefers-reduced-motion: reduce) {
  .swipe-snap,
  .swipe-dismiss {
    transition: none;
  }
  .card-entering {
    animation: none;
  }
}
</style>
