<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { DEFAULT_KEYBINDS } from '~/composables/useStudyKeyboard';
  import type { CardBlockType, CardImageLayout, CardImagePosition, HeadwordFurigana, StudyKeybinds, StudySettingsDto } from '~/types';
  import {
    getCardImageBlock,
    getCardImagePosition,
    getHeadwordFurigana,
    getSentencePosition,
    hasCustomArrangement,
    layoutHasBlock,
    resolveCardLayout,
    setBlockPresence,
    setCardImageOption,
    setCardImagePosition,
    setHeadwordFurigana,
    setSentenceBlur,
    setSentencePosition,
  } from '~/utils/cardLayout';

  const props = defineProps<{ inline?: boolean }>();

  const srsStore = useSrsStore();
  const toast = useToast();

  const form = reactive({ ...srsStore.studySettings });
  const saveState = ref<'idle' | 'saving' | 'saved' | 'error'>('idle');
  const loaded = ref(false);

  const ADVANCED_LAYOUT_KEY = 'srs-card-layout-advanced';
  const advancedLayout = ref(false);
  watch(advancedLayout, (on) => {
    try {
      localStorage.setItem(ADVANCED_LAYOUT_KEY, on ? '1' : '0');
    } catch {
      /* private mode — the preference simply resets next session */
    }
  });

  const ADVANCED_PREVIEW_KEY = 'srs-card-layout-preview';
  const advancedPreview = ref(false);
  watch(advancedPreview, (on) => {
    try {
      localStorage.setItem(ADVANCED_PREVIEW_KEY, on ? '1' : '0');
    } catch {
      /* private mode — the preference simply resets next session */
    }
  });

  onMounted(async () => {
    try {
      advancedLayout.value = localStorage.getItem(ADVANCED_LAYOUT_KEY) === '1';
      advancedPreview.value = localStorage.getItem(ADVANCED_PREVIEW_KEY) === '1';
    } catch {
      /* private mode — default to simple */
    }
    await srsStore.fetchSettings(true);
    Object.assign(form, srsStore.studySettings);
    form.keybinds = { ...srsStore.studySettings.keybinds };
    form.timedReview = { ...srsStore.studySettings.timedReview };
    form.writeInReview = { ...srsStore.studySettings.writeInReview };
    syncEasyFromForm();
    if (!form.timezone) applyDetectedTimezone();
    // Let the hydration mutations flush through the deep watcher (still guarded by loaded=false)
    // before arming auto-save, otherwise the initial Object.assign triggers a spurious save.
    await nextTick();
    loaded.value = true;
    tickInterval = setInterval(() => {
      nowMinute.value = Date.now();
    }, 60_000);
  });

  const gradingOptions = [
    { label: '4 buttons', value: 4 },
    { label: '2 buttons', value: 2 },
  ];

  const interleavingOptions = [
    { label: 'Mixed', value: 'Mixed' },
    { label: 'New first', value: 'NewFirst' },
    { label: 'Reviews first', value: 'ReviewsFirst' },
  ];

  const newCardGatheringOptions = [
    { label: 'Top deck', value: 'TopDeck' },
    { label: 'All decks equally', value: 'RoundRobin' },
    { label: 'Cross-deck frequency', value: 'CrossDeckFrequency' },
  ];

  const reviewFromOptions = [
    { label: 'All tracked', value: 'AllTracked' },
    { label: 'Study decks only', value: 'StudyDecksOnly' },
  ];

  const exampleSentenceOptions = [
    { label: 'Hidden', value: 'Hidden' },
    { label: 'Front', value: 'Front' },
    { label: 'Back', value: 'Back' },
  ];

  const cardImageLayoutOptions = [
    { label: 'Beside word', value: 'beside' },
    { label: 'Below word', value: 'below' },
  ];

  const cardImagePositionOptions = [
    { label: 'Front', value: 'Front' },
    { label: 'Back', value: 'Back' },
  ];

  const cardAudioPositionOptions = [
    { label: 'Front', value: 'Front' },
    { label: 'Back', value: 'Back' },
    { label: 'Both', value: 'Both' },
  ];

  const leechActionOptions = [
    { label: 'Suspend', value: 'Suspend' },
    { label: 'Notify only', value: 'NotifyOnly' },
  ];

  const revealActionOptions = [
    { label: 'Reveal answer', value: 'Reveal' },
    { label: 'Reveal + auto-fail', value: 'FailLearn' },
    { label: 'Alert only', value: 'Nudge' },
  ];

  const answerActionOptions = [
    { label: 'Soft fail', value: 'SoftFail' },
    { label: 'Hard fail', value: 'HardFail' },
  ];

  const writeInPlacementOptions = [
    { label: 'Bottom bar', value: false },
    { label: 'Inline in card', value: true },
  ];

  const writeInWrongOptions = [
    { label: 'Reveal answer', value: 'Reveal' },
    { label: 'Shake & retry', value: 'Retry' },
  ];

  const exampleSentenceSortingOptions = [
    { label: 'Random', value: 'Random' },
    { label: 'Easiest', value: 'EasiestFirst' },
    { label: 'Hardest', value: 'HardestFirst' },
  ];

  // The simple per-part controls read from the resolved layout and write back into it. The setters
  // always mirror the legacy field (so an old client with a null cardLayout still derives correctly)
  // and, once a concrete cardLayout exists, additionally rewrite it so the toggle keeps working.
  const resolvedLayout = computed(() => resolveCardLayout(form as StudySettingsDto));
  const layoutIsCustom = computed(() => !!form.cardLayout && hasCustomArrangement(form.cardLayout));

  type PresenceKey = 'showCardStatus' | 'showConfusableReadings' | 'showPitchAccent' | 'showFrequencyRank' | 'showKanjiBreakdown' | 'showWordComposition' | 'showWordUsedIn';
  function presenceToggle(key: PresenceKey, type: CardBlockType) {
    return computed<boolean>({
      get: () => layoutHasBlock(resolvedLayout.value, type),
      set: (v) => {
        form[key] = v;
        if (form.cardLayout) form.cardLayout = setBlockPresence(form.cardLayout, type, v);
      },
    });
  }

  const showCardStatus = presenceToggle('showCardStatus', 'cardStatus');
  const showConfusableReadings = presenceToggle('showConfusableReadings', 'confusableReadings');
  const showPitchAccent = presenceToggle('showPitchAccent', 'pitchAccent');
  const showFrequencyRank = presenceToggle('showFrequencyRank', 'frequencyRank');
  const showKanjiBreakdown = presenceToggle('showKanjiBreakdown', 'kanjiBreakdown');
  const showWordComposition = presenceToggle('showWordComposition', 'wordComposition');
  const showWordUsedIn = presenceToggle('showWordUsedIn', 'wordUsedIn');

  function applyFurigana(on: boolean, newOnly: boolean) {
    const furigana: HeadwordFurigana = on ? (newOnly ? 'newOnly' : 'shown') : 'afterFlip';
    form.showFuriganaOnFront = on;
    form.furiganaOnFrontNewOnly = newOnly;
    if (form.cardLayout) form.cardLayout = setHeadwordFurigana(form.cardLayout, furigana);
  }
  const showFuriganaOnFront = computed<boolean>({
    get: () => {
      const f = getHeadwordFurigana(resolvedLayout.value);
      return f === 'shown' || f === 'newOnly';
    },
    set: (v) => applyFurigana(v, furiganaOnFrontNewOnly.value),
  });
  const furiganaOnFrontNewOnly = computed<boolean>({
    get: () => getHeadwordFurigana(resolvedLayout.value) === 'newOnly',
    set: (v) => applyFurigana(showFuriganaOnFront.value, v),
  });

  const exampleSentencePosition = computed<'Hidden' | 'Front' | 'Back'>({
    get: () => getSentencePosition(resolvedLayout.value),
    set: (pos) => {
      form.exampleSentencePosition = pos;
      if (form.cardLayout) form.cardLayout = setSentencePosition(form.cardLayout, pos, form.blurExampleSentence);
    },
  });
  const blurExampleSentence = computed<boolean>({
    get: () => {
      if (!form.cardLayout) return form.blurExampleSentence;
      const es = [...form.cardLayout.front, ...form.cardLayout.back].find((b) => b.type === 'exampleSentence');
      return !!es?.options?.blur;
    },
    set: (v) => {
      form.blurExampleSentence = v;
      if (form.cardLayout) form.cardLayout = setSentenceBlur(form.cardLayout, v);
    },
  });

  const cardImageLayout = computed<CardImageLayout>({
    get: () => (getCardImageBlock(resolvedLayout.value)?.options?.layout ?? 'beside') as CardImageLayout,
    set: (v) => {
      form.cardImageLayout = v;
      if (form.cardLayout) form.cardLayout = setCardImageOption(form.cardLayout, 'layout', v);
    },
  });
  const cardImagePosition = computed<CardImagePosition>({
    get: () => getCardImagePosition(resolvedLayout.value),
    set: (pos) => {
      form.cardImagePosition = pos;
      if (form.cardLayout) form.cardLayout = setCardImagePosition(form.cardLayout, pos);
    },
  });
  const blurCardImage = computed<boolean>({
    get: () => getCardImageBlock(resolvedLayout.value)?.options?.blur ?? true,
    set: (v) => {
      form.blurCardImage = v;
      if (form.cardLayout) form.cardLayout = setCardImageOption(form.cardLayout, 'blur', v);
    },
  });

  // Easy Days: per-weekday load weights, index 0=Sunday…6=Saturday, each in [0,1].
  const easyModeOptions = [
    { label: 'Lighter weekends', value: 'weekends' },
    { label: 'Custom per day', value: 'custom' },
  ];
  const weekendLevelOptions = [
    { label: 'Reduced', value: 0.5 },
    { label: 'Minimum', value: 0 },
  ];
  const easyLevelOptions = [
    { label: 'Normal', value: 1 },
    { label: 'Reduced', value: 0.5 },
    { label: 'Minimum', value: 0 },
  ];
  // Display order Monday→Sunday; idx is the DayOfWeek index used by easyDays / the backend.
  const weekdayRows = [
    { idx: 1, label: 'Monday' },
    { idx: 2, label: 'Tuesday' },
    { idx: 3, label: 'Wednesday' },
    { idx: 4, label: 'Thursday' },
    { idx: 5, label: 'Friday' },
    { idx: 6, label: 'Saturday' },
    { idx: 0, label: 'Sunday' },
  ];

  const easyEnabled = ref(false);
  const easyMode = ref<'weekends' | 'custom'>('weekends');
  const weekendLevel = ref(0.5);
  const customDays = ref<number[]>([1, 1, 1, 1, 1, 1, 1]);
  // Per-day grid is rolled up by default (to save space); expanded only when the user actively switches in.
  const customExpanded = ref(false);

  const customSummary = computed(() => {
    const labels = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
    const reduced: string[] = [];
    const minimum: string[] = [];
    customDays.value.forEach((w, i) => {
      if (w === 0) minimum.push(labels[i]!);
      else if (w < 1) reduced.push(labels[i]!);
    });
    if (!reduced.length && !minimum.length) return 'all days normal';
    const parts: string[] = [];
    if (reduced.length) parts.push(`${reduced.join(', ')} reduced`);
    if (minimum.length) parts.push(`${minimum.join(', ')} minimum`);
    return parts.join(' · ');
  });

  // Auto-expand the grid when the user switches into custom mode (or re-enables while already custom);
  // both watches are loaded-guarded so loading an existing custom config keeps it rolled up.
  watch(easyMode, (mode) => {
    if (loaded.value && mode === 'custom') customExpanded.value = true;
  });
  watch(easyEnabled, (on) => {
    if (loaded.value && on && easyMode.value === 'custom') customExpanded.value = true;
  });

  // Derive the UI controls from the stored weekday weights on load.
  function syncEasyFromForm() {
    customExpanded.value = false; // existing config loads rolled up
    const ed = form.easyDays;
    if (!ed || ed.length !== 7) {
      easyEnabled.value = false;
      easyMode.value = 'weekends';
      weekendLevel.value = 0.5;
      customDays.value = [1, 1, 1, 1, 1, 1, 1];
      return;
    }
    easyEnabled.value = true;
    customDays.value = [...ed];
    const weekdaysNormal = ed[1] === 1 && ed[2] === 1 && ed[3] === 1 && ed[4] === 1 && ed[5] === 1;
    const weekendReduced = ed[0] === ed[6] && ed[0]! < 1;
    if (weekdaysNormal && weekendReduced) {
      easyMode.value = 'weekends';
      weekendLevel.value = ed[0]!;
    } else {
      easyMode.value = 'custom';
    }
  }

  // Push the UI controls back into the stored weekday weights (drives auto-save via the form watcher).
  function applyEasyToForm() {
    if (!easyEnabled.value) {
      form.easyDays = null;
      return;
    }
    if (easyMode.value === 'weekends') {
      const w = weekendLevel.value;
      form.easyDays = [w, 1, 1, 1, 1, 1, w]; // Sunday…Saturday
    } else {
      form.easyDays = [...customDays.value];
    }
  }

  watch(
    [easyEnabled, easyMode, weekendLevel, customDays],
    () => {
      if (loaded.value) applyEasyToForm();
    },
    { deep: true }
  );

  function getUtcOffsetMinutes(zone: string, date?: Date): number {
    const parts = new Intl.DateTimeFormat('en-US', { timeZone: zone, hour: 'numeric', hour12: false, timeZoneName: 'shortOffset' }).formatToParts(
      date ?? new Date()
    );
    const offsetStr = parts.find((p) => p.type === 'timeZoneName')?.value ?? 'GMT';
    const match = offsetStr.match(/GMT([+-]?\d+)?(?::(\d+))?/);
    const hours = match?.[1] ? parseInt(match[1]) : 0;
    const minutes = match?.[2] ? parseInt(match[2]) : 0;
    return hours * 60 + (hours >= 0 ? minutes : -minutes);
  }

  function formatUtcOffset(totalMinutes: number): string {
    if (totalMinutes === 0) return 'UTC';
    const sign = totalMinutes > 0 ? '+' : '-';
    const abs = Math.abs(totalMinutes);
    const h = Math.floor(abs / 60);
    const m = abs % 60;
    return m > 0 ? `UTC${sign}${h}:${m.toString().padStart(2, '0')}` : `UTC${sign}${h}`;
  }

  function formatZoneName(zone: string): string {
    return zone.split('/').pop()!.replace(/_/g, ' ');
  }

  const nowMinute = ref(Date.now());
  let tickInterval: ReturnType<typeof setInterval>;
  onUnmounted(() => {
    clearInterval(tickInterval);
    clearTimeout(saveTimer);
    clearTimeout(savedClearTimer);
  });

  const dayStartOptions = computed(() => {
    void nowMinute.value;
    const now = new Date();
    const zones = Intl.supportedValuesOf('timeZone').filter((z) => !z.startsWith('Etc/'));
    const items = zones.map((zone) => {
      const offsetMinutes = getUtcOffsetMinutes(zone);
      const localTime = now.toLocaleTimeString('en-GB', { timeZone: zone, hour: '2-digit', minute: '2-digit', hour12: false });
      return {
        label: `(${formatUtcOffset(offsetMinutes)}) ${formatZoneName(zone)} — Currently ${localTime}`,
        value: zone,
        offsetMinutes,
      };
    });
    items.sort((a, b) => a.offsetMinutes - b.offsetMinutes || a.label.localeCompare(b.label));
    return items;
  });

  function applyDetectedTimezone() {
    try {
      const zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
      if (dayStartOptions.value.some((o) => o.value === zone)) form.timezone = zone;
    } catch {
      /* no-op */
    }
  }

  const gradeLabels4: [keyof StudyKeybinds, string][] = [
    ['grade1', 'Again'],
    ['grade2', 'Hard'],
    ['grade3', 'Good'],
    ['grade4', 'Easy'],
  ];
  const gradeLabels2: [keyof StudyKeybinds, string][] = [
    ['grade1', 'Again'],
    ['grade2', 'Good'],
  ];
  const actionEntries: [keyof StudyKeybinds, string][] = [
    ['flipCard', 'Flip card / Grade Good'],
    ['blacklist', 'Blacklist'],
    ['bury', 'Bury for a day'],
    ['forget', 'Forget'],
    ['master', 'Master'],
    ['suspend', 'Suspend'],
    ['undo', 'Undo'],
    ['wrapUp', 'Wrap up'],
    ['pauseTimer', 'Pause/resume timer'],
    ['dictPrev', 'Previous dictionary'],
    ['dictNext', 'Next dictionary'],
  ];

  const gradeEntries = computed(() => (form.gradingButtons === 4 ? gradeLabels4 : gradeLabels2));

  function checkConflict(forKey: keyof StudyKeybinds, value: string): string | null {
    for (const [key, label] of [...gradeEntries.value, ...actionEntries]) {
      if (key !== forKey && form.keybinds[key] === value) return label;
    }
    return null;
  }

  function resetKeybinds() {
    Object.assign(form.keybinds, DEFAULT_KEYBINDS);
  }

  // A keybind conflict means an ambiguous binding; hold off saving until the user resolves it.
  const hasKeybindConflict = computed(() => [...gradeEntries.value, ...actionEntries].some(([key]) => checkConflict(key, form.keybinds[key]) !== null));

  let saveTimer: ReturnType<typeof setTimeout> | undefined;
  let savedClearTimer: ReturnType<typeof setTimeout> | undefined;

  async function persist() {
    clearTimeout(saveTimer);
    saveState.value = 'saving';
    try {
      await srsStore.updateSettings({ ...form, keybinds: { ...form.keybinds }, timedReview: { ...form.timedReview }, writeInReview: { ...form.writeInReview } });
      saveState.value = 'saved';
      clearTimeout(savedClearTimer);
      savedClearTimer = setTimeout(() => {
        if (saveState.value === 'saved') saveState.value = 'idle';
      }, 2500);
    } catch {
      saveState.value = 'error';
      toast.add({ severity: 'error', summary: 'Failed to save settings', life: 3000 });
    }
  }

  // Manual flush — used by the status pill's "retry" affordance.
  function save() {
    void persist();
  }

  watch(
    form,
    () => {
      if (!loaded.value) return;
      clearTimeout(saveTimer);
      if (hasKeybindConflict.value) {
        saveState.value = 'idle';
        return;
      }
      saveTimer = setTimeout(() => {
        if (!hasKeybindConflict.value) void persist();
      }, 700);
    },
    { deep: true }
  );

  const CardWrapper = defineComponent({
    props: { card: Boolean },
    setup(wrapperProps, { slots }) {
      return () => {
        if (!wrapperProps.card) return slots.default?.();
        return h(resolveComponent('Card'), null, {
          title: () => h('h3', { class: 'text-lg font-semibold' }, 'SRS Study'),
          content: () => slots.default?.(),
        });
      };
    },
  });

  // Exposed so the inline study-session Dialog can mirror save status in its footer.
  defineExpose({ saveState, save });
</script>

<template>
  <CardWrapper :card="!props.inline">
    <!-- Standalone: a sticky chip rides along the top-right inside the panel, staying visible
         through the long form. Inline (study Dialog): status lives in the Dialog footer instead. -->
    <div v-if="!props.inline" class="sticky top-2 z-20 h-0 pointer-events-none">
      <Transition name="save-chip">
        <div
          v-if="saveState !== 'idle'"
          class="pointer-events-auto absolute right-0 top-0 flex w-max items-center rounded-full border border-surface-200 bg-surface-0 px-3 py-2 shadow-lg dark:border-surface-700 dark:bg-surface-900"
        >
          <SrsSaveStatus :state="saveState" @retry="save" />
        </div>
      </Transition>
    </div>
    <div v-if="!loaded" class="flex justify-center py-4">
      <ProgressSpinner style="width: 24px; height: 24px" />
    </div>
    <div v-else class="flex flex-col gap-4">
      <!-- Session -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Session</h3>
      <div :class="props.inline ? 'flex flex-col gap-4' : 'grid grid-cols-1 md:grid-cols-3 gap-4'">
        <div class="min-w-0">
          <label class="block text-sm font-medium mb-1">
            New cards per day
            <Tooltip content="Maximum number of new words introduced each day. Set to 0 to pause learning new words while still reviewing." placement="top">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <InputNumber v-model="form.newCardsPerDay" :min="0" :max="9999" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
        </div>
        <div class="min-w-0">
          <label class="block text-sm font-medium mb-1">
            Max reviews per day
            <Tooltip content="Maximum number of review cards shown each day. Reviews that exceed this limit carry over to the next day." placement="top">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <InputNumber v-model="form.maxReviewsPerDay" :min="0" :max="9999" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
        </div>
        <div class="min-w-0">
          <label class="block text-sm font-medium mb-1">
            Card batch size
            <Tooltip
              content="Number of cards loaded at a time during a study session. Smaller batches keep sessions focused, larger batches reduce loading pauses."
              placement="top"
            >
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <InputNumber v-model="form.batchSize" :min="1" :max="999" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
        </div>
      </div>

      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.pauseBetweenBatches" input-id="pauseBetweenBatches" />
        <label for="pauseBetweenBatches" class="text-sm cursor-pointer">
          Pause at the end of each batch
          <Tooltip
            content="When enabled, finishing a full batch shows a checkpoint so you can stop or keep going, instead of loading the next batch automatically."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>

      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.countFailedReviews" input-id="countFailedReviews" />
        <label for="countFailedReviews" class="text-sm cursor-pointer">
          Count failed reviews toward daily limit
          <Tooltip
            content="When enabled, every review counts toward your daily limit, including repeated reviews of cards you got wrong. When disabled, only unique cards count, so failing a card multiple times won't eat into your daily budget."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">
          Card interleaving
          <Tooltip
            content="Controls how new cards and reviews are mixed.<br>**Mixed** — shuffles new and review cards together.<br>**New first** — shows all new cards before reviews.<br>**Reviews first** — clears your review backlog before introducing new cards."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton
          v-model="form.interleaving"
          :options="interleavingOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          class="flex-wrap"
        />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">
          New card gathering
          <Tooltip
            content="How new cards are picked when you have multiple decks.<br>**Top deck** — draws all new cards from your highest-priority deck first before moving to the next.<br>**All decks equally** — rotates between decks so you get new cards from each one.<br>**Cross-deck frequency** — draw words by total occurrence count across all your study decks, picking the words you'll see the most first."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton
          v-model="form.newCardGathering"
          :options="newCardGatheringOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          class="flex-wrap"
        />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">
          Review cards from
          <Tooltip
            content="Which words to include in your reviews.<br>**All tracked** — reviews every word you've ever studied, even if it's no longer in an active deck.<br>**Study decks only** — only reviews words that belong to your current study decks."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton v-model="form.reviewFrom" :options="reviewFromOptions" option-label="label" option-value="value" :allow-empty="false" class="flex-wrap" />
      </div>

      <Divider />

      <!-- Modalities -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Modalities</h3>
      <p class="text-sm text-surface-500 -mt-1 mb-1">
        Choose how cards are reviewed. If you choose to have more than one modality active, each card will have a different one at random.
      </p>

      <div class="flex flex-col gap-2">
        <div class="flex items-center gap-2">
          <Checkbox v-model="form.writeInReview.modalitySrs" :binary="true" input-id="writeInSrs" />
          <label for="writeInSrs" class="text-sm cursor-pointer">
            Standard cards
            <Tooltip content="The classic flashcard: see the word, flip to reveal, grade your recall." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox v-model="form.writeInReview.modalityReading" :binary="true" input-id="writeInReading" />
          <label for="writeInReading" class="text-sm cursor-pointer">
            Write-in reading
            <Tooltip content="See the word with its reading hidden, then type the reading (romaji, kana, or kanji)." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox v-model="form.writeInReview.modalityMeaning" :binary="true" input-id="writeInMeaning" />
          <label for="writeInMeaning" class="text-sm cursor-pointer">
            Write-in meaning
            <Tooltip content="See the word, then type one content word from its definition (small words like 'the' or 'to' don't count)." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
      </div>

      <div v-if="form.writeInReview.modalityReading || form.writeInReview.modalityMeaning" class="flex flex-col gap-4 mt-1">
        <div>
          <label class="block text-sm font-medium mb-1">
            Input placement
            <Tooltip content="**Bottom bar** — the input replaces the Show Answer bar at the bottom.<br>**Inline in card** — the input sits inside the card, under the word." placement="top">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <SelectButton
            v-model="form.writeInReview.inlineInput"
            :options="writeInPlacementOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
          />
        </div>

        <div>
          <label class="block text-sm font-medium mb-1">
            When the answer is wrong
            <Tooltip content="**Reveal answer** — flip to the answer and suggest Again.<br>**Shake & retry** — keep the input open so you can try again if you get it wrong." placement="top">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <SelectButton
            v-model="form.writeInReview.wrongBehavior"
            :options="writeInWrongOptions"
            option-label="label"
            option-value="value"
            :allow-empty="false"
          />
        </div>

        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.writeInReview.autoAdvance" input-id="writeInAutoAdvance" />
          <label for="writeInAutoAdvance" class="text-sm cursor-pointer">
            Auto-advance after answering
            <Tooltip content="The suggested grade will be submitted automatically after a delay. You can override by choosing a different grade." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div v-if="form.writeInReview.autoAdvance" class="ml-6 flex flex-col gap-3">
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.writeInReview.autoAdvanceWrong" input-id="writeInAutoAdvanceWrong" />
            <label for="writeInAutoAdvanceWrong" class="text-sm cursor-pointer">
              Also auto-advance wrong answers
              <Tooltip content="When off, only correct answers auto-advance; wrong answers wait for you to grade. When on, wrong answers will be graded Again after the delay." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div class="min-w-0 max-w-[16rem]">
            <label class="block text-sm font-medium mb-1">Delay (seconds)</label>
            <InputNumber
              v-model="form.writeInReview.autoAdvanceSeconds"
              :min="0"
              :max="60"
              :step="0.5"
              :min-fraction-digits="0"
              :max-fraction-digits="1"
              :show-buttons="!props.inline"
              class="w-full [&_input]:w-full"
            />
          </div>
        </div>

        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.writeInReview.skipNewCards" input-id="writeInSkipNew" />
          <label for="writeInSkipNew" class="text-sm cursor-pointer">
            Disable for new cards
            <Tooltip content="New cards you haven't learned yet just reveal normally (standard card) instead of asking you to type an answer." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>

        <div v-if="form.writeInReview.modalityReading" class="flex items-center gap-2">
          <ToggleSwitch v-model="form.writeInReview.romajiInput" input-id="writeInRomaji" />
          <label for="writeInRomaji" class="text-sm cursor-pointer">
            Convert romaji to kana as you type
            <Tooltip content="A built-in IME for the reading input: typing 'neko' becomes ねこ. Turn off to type kana directly." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>

        <div v-if="form.writeInReview.modalityMeaning" class="flex items-center gap-2">
          <ToggleSwitch v-model="form.writeInReview.meaningShowReading" input-id="writeInMeaningReading" />
          <label for="writeInMeaningReading" class="text-sm cursor-pointer">
            Show the reading in meaning mode
            <Tooltip content="Show the word's reading (furigana) on the front when typing the meaning, so only the meaning is tested. Turn off to hide it and test yourself on both at once." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>

        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.writeInReview.sound" input-id="writeInSound" />
          <label for="writeInSound" class="text-sm cursor-pointer">
            Play a sound on correct / wrong
            <Tooltip content="A short chime when your answer is right, and a low tone when it's wrong." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>

        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.writeInReview.timed" input-id="writeInTimed" />
          <label for="writeInTimed" class="text-sm cursor-pointer">
            Keep timed review on write-in cards
            <Tooltip content="Timed review is turned off on write-in cards by default. Standard cards are always timed normally." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
      </div>

      <Divider />

      <!-- Day boundary -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Day boundary</h3>
      <div>
        <label class="block text-sm font-medium mb-1">
          Timezone
          <Tooltip content="Your daily card limits and streaks reset at midnight in this timezone." placement="top">
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <div class="flex flex-col sm:flex-row gap-2">
          <Select v-model="form.timezone" :options="dayStartOptions" option-label="label" option-value="value" filter class="flex-1" />
          <Button type="button" severity="secondary" size="small" label="Detect my timezone" class="shrink-0" @click="applyDetectedTimezone" />
        </div>
      </div>

      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.dayBoundaryScheduling" input-id="dayBoundaryScheduling" />
        <label for="dayBoundaryScheduling" class="text-sm cursor-pointer">
          Group reviews by day
          <Tooltip
            content="When enabled, all reviews scheduled for today become available at the start of the day instead of at their exact time."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>

      <Divider />

      <!-- Review scheduling -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Review scheduling</h3>
      <p class="text-sm text-surface-500 -mt-1 mb-1">
        Reviews are automatically balanced — each card is placed on the least-busy day within its normal scheduling window, smoothing out spike days without
        affecting memory or retention.
      </p>

      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="easyEnabled" input-id="easyDaysEnabled" />
        <label for="easyDaysEnabled" class="text-sm cursor-pointer">
          Easy days
          <Tooltip
            content="Get fewer reviews on the days you pick. Cards shift to nearby days instead, so your overall workload stays the same."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>

      <div v-if="easyEnabled" class="flex flex-col gap-3 ml-6">
        <SelectButton v-model="easyMode" :options="easyModeOptions" option-label="label" option-value="value" :allow-empty="false" class="flex-wrap" />

        <div v-if="easyMode === 'weekends'" class="min-w-0">
          <label class="block text-sm font-medium mb-1">
            Weekend load
            <Tooltip
              content="How much to reduce reviews on Saturday and Sunday. Reduced ≈ half the usual load; Minimum avoids them whenever possible."
              placement="top"
            >
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <SelectButton v-model="weekendLevel" :options="weekendLevelOptions" option-label="label" option-value="value" :allow-empty="false" />
        </div>

        <div v-else class="flex flex-col gap-2">
          <button
            type="button"
            class="flex items-center gap-2 w-fit text-sm font-medium cursor-pointer hover:text-primary-500"
            @click="customExpanded = !customExpanded"
          >
            <i :class="['pi text-xs', customExpanded ? 'pi-chevron-down' : 'pi-chevron-right']" />
            <span>Per-day levels</span>
            <span v-if="!customExpanded" class="font-normal text-surface-500">— {{ customSummary }}</span>
          </button>
          <div v-if="customExpanded" class="flex flex-col gap-2">
            <p class="text-xs text-surface-500">Normal — full load · Reduced — about half · Minimum — avoid when possible.</p>
            <div v-for="day in weekdayRows" :key="day.idx" class="flex items-center justify-between gap-3">
              <span class="text-sm w-24 shrink-0">{{ day.label }}</span>
              <SelectButton
                v-model="customDays[day.idx]"
                :options="easyLevelOptions"
                option-label="label"
                option-value="value"
                :allow-empty="false"
                size="small"
                class="flex-wrap justify-end"
              />
            </div>
          </div>
        </div>
      </div>

      <Divider />

      <!-- Card appearance -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Card appearance</h3>

      <template v-if="!advancedLayout">
        <div :class="props.inline ? 'flex flex-col gap-4' : 'flex flex-col md:flex-row md:items-start md:gap-6'">
          <!-- Live preview (top on mobile, sticky right column on desktop; full-width in the narrow inline dialog). -->
          <div :class="props.inline ? 'mb-2' : 'mb-4 md:mb-0 md:order-2 md:w-80 lg:w-96 xl:w-[30rem] md:shrink-0 md:sticky md:top-4'">
            <SrsCardPreview :settings="form" />
            <p v-if="layoutIsCustom" class="mt-2 text-xs text-surface-500 dark:text-surface-400">
              <i class="pi pi-info-circle mr-1" />
              Your card arrangement has been customised. Reordering and per-block options are managed in the advanced editor.
            </p>
            <Button
              type="button"
              outlined
              icon="pi pi-sliders-h"
              label="Customise layout (advanced)"
              class="mt-3 w-full"
              @click="advancedLayout = true"
            />
          </div>
          <!-- Toggle groups -->
          <div class="flex-1 min-w-0 md:order-1 flex flex-col gap-4">
          <div>
            <label class="block text-sm font-semibold mb-2 pb-1 border-b border-surface-200 dark:border-surface-700">Card front</label>
            <div class="flex flex-col gap-2">
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showCardStatus" input-id="showCardStatus" />
                <label for="showCardStatus" class="text-sm cursor-pointer">
                  Show card learning status
                  <Tooltip content="Display the card status (New, Review, Again) at the top of the card." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showFuriganaOnFront" input-id="showFuriganaOnFront" />
                <label for="showFuriganaOnFront" class="text-sm cursor-pointer">
                  Show furigana
                  <Tooltip content="Display furigana (reading hints) above kanji on the card front." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div v-if="showFuriganaOnFront" class="flex items-center gap-2 ml-6">
                <ToggleSwitch v-model="furiganaOnFrontNewOnly" input-id="furiganaOnFrontNewOnly" />
                <label for="furiganaOnFrontNewOnly" class="text-sm cursor-pointer">
                  New cards only
                  <Tooltip content="Only show furigana on cards you haven't seen before. Review cards will show plain kanji." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showConfusableReadings" input-id="showConfusableReadings" />
                <label for="showConfusableReadings" class="text-sm cursor-pointer">
                  Show confusable readings
                  <Tooltip
                    content="When a kanji has multiple dictionary entries with different readings (e.g. 音 → おと/おん), show the other readings to help avoid mix-ups."
                    placement="right"
                  >
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
            </div>
          </div>

          <div>
            <label class="block text-sm font-semibold mb-2 pb-1 border-b border-surface-200 dark:border-surface-700">Example sentence</label>
            <div class="flex flex-col gap-2">
              <div>
                <label class="text-sm mb-1 block">
                  Position
                  <Tooltip
                    content="Show an example sentence from the media where the word appears.<br>**Hidden** — no sentence shown.<br>**Front** — sentence visible before you flip the card (sentence card).<br>**Back** — sentence shown only after you flip."
                    placement="right"
                  >
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
                <SelectButton
                  v-model="exampleSentencePosition"
                  :options="exampleSentenceOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                />
              </div>
              <div v-if="exampleSentencePosition !== 'Hidden'" class="flex items-center gap-2">
                <ToggleSwitch v-model="blurExampleSentence" input-id="blurExampleSentence" />
                <label for="blurExampleSentence" class="text-sm cursor-pointer">
                  Blur until clicked
                  <Tooltip content="Example sentence is blurred by default. Click it to reveal." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div v-if="exampleSentencePosition !== 'Hidden'">
                <label class="text-sm mb-1 block">
                  Sorting
                  <Tooltip
                    content="**Random** — a random example sentence each time.<br>**Easiest** — prefer simpler sentences.<br>**Hardest** — prefer more complex sentences."
                    placement="right"
                  >
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
                <SelectButton
                  v-model="form.exampleSentenceSorting"
                  :options="exampleSentenceSortingOptions"
                  option-label="label"
                  option-value="value"
                  :allow-empty="false"
                />
              </div>
            </div>
          </div>

          <div>
            <label class="block text-sm font-semibold mb-2 pb-1 border-b border-surface-200 dark:border-surface-700">Card back</label>
            <div class="flex flex-col gap-2">
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showPitchAccent" input-id="showPitchAccent" />
                <label for="showPitchAccent" class="text-sm cursor-pointer">
                  Pitch accent
                  <Tooltip content="Show the pitch accent pattern on the card back." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showFrequencyRank" input-id="showFrequencyRank" />
                <label for="showFrequencyRank" class="text-sm cursor-pointer">
                  Frequency rank
                  <Tooltip content="Show how common the word is in Japanese, based on overall word frequency data." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showKanjiBreakdown" input-id="showKanjiBreakdown" />
                <label for="showKanjiBreakdown" class="text-sm cursor-pointer">
                  Kanji breakdown
                  <Tooltip content="Show the individual kanji that make up the word along with their usual meaning." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showWordComposition" input-id="showWordComposition" />
                <label for="showWordComposition" class="text-sm cursor-pointer">
                  Word composition
                  <Tooltip content="Show the component words that compose this word." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <div class="flex items-center gap-2">
                <ToggleSwitch v-model="showWordUsedIn" input-id="showWordUsedIn" />
                <label for="showWordUsedIn" class="text-sm cursor-pointer">
                  Word used in
                  <Tooltip content="Show other words that contain this word." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
            </div>
          </div>

          <div>
            <label class="block text-sm font-semibold mb-2 pb-1 border-b border-surface-200 dark:border-surface-700">Card image</label>
            <div class="flex flex-col gap-2">
              <div>
                <label class="text-sm mb-1 block">
                  Layout
                  <Tooltip
                    content="Where your uploaded card image appears.<br>**Beside word** — to the right of the word on wider screens (stacks below on mobile).<br>**Below word** — centred under the word."
                    placement="right"
                  >
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
                <SelectButton v-model="cardImageLayout" :options="cardImageLayoutOptions" option-label="label" option-value="value" :allow-empty="false" />
              </div>
              <div>
                <label class="text-sm mb-1 block">
                  Position
                  <Tooltip
                    content="**Front** — image visible before you flip the card.<br>**Back** — image shown only after you flip."
                    placement="right"
                  >
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
                <SelectButton v-model="cardImagePosition" :options="cardImagePositionOptions" option-label="label" option-value="value" :allow-empty="false" />
              </div>
              <div v-if="cardImagePosition === 'Front'" class="flex items-center gap-2">
                <ToggleSwitch v-model="blurCardImage" input-id="blurCardImage" />
                <label for="blurCardImage" class="text-sm cursor-pointer">
                  Blur image before reveal
                  <Tooltip content="Image is blurred until you flip the card or click it." placement="right">
                    <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                  </Tooltip>
                </label>
              </div>
              <p class="text-xs text-surface-500 dark:text-surface-400">
                Card images are a
                <NuxtLink to="/jiten-plus" class="underline font-medium">Jiten+</NuxtLink>
                feature.
              </p>
            </div>
          </div>
          </div>
        </div>
      </template>

      <template v-else>
        <div class="flex flex-col gap-3">
          <div class="flex items-center justify-between gap-2">
            <Button
              type="button"
              severity="secondary"
              size="small"
              icon="pi pi-arrow-left"
              label="Back to simple settings"
              @click="advancedLayout = false"
            />
            <Button
              type="button"
              severity="secondary"
              size="small"
              :icon="advancedPreview ? 'pi pi-eye-slash' : 'pi pi-eye'"
              :label="advancedPreview ? 'Hide preview' : 'Show preview'"
              @click="advancedPreview = !advancedPreview"
            />
          </div>
          <!-- The study-session Dialog embeds this component inline: the dialog is far narrower than
               the viewport, so the md: two-column split must not apply there — always stack. -->
          <div :class="advancedPreview ? (props.inline ? 'flex flex-col gap-4' : 'flex flex-col md:flex-row md:items-start md:gap-6') : ''">
            <div :class="props.inline ? 'min-w-0' : 'flex-1 min-w-0 md:order-1'">
              <SrsCardLayoutEditor v-model:layout="form.cardLayout" :settings="form">
                <template #presetBar>
                  <SrsCardLayoutPresetBar v-model:layout="form.cardLayout" v-model:presets="form.cardLayoutPresets" :settings="form" />
                </template>
              </SrsCardLayoutEditor>
            </div>
            <div
              v-if="advancedPreview"
              :class="props.inline ? 'order-first' : 'mt-4 md:mt-0 md:order-2 md:w-80 lg:w-96 xl:w-[30rem] md:shrink-0 md:sticky md:top-4'"
            >
              <SrsCardPreview :settings="form" />
            </div>
          </div>
        </div>
      </template>

      <Divider />

      <!-- Audio -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Audio</h3>
      <div class="flex flex-col gap-2">
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.autoPlayWord" input-id="autoPlayWord" />
          <label for="autoPlayWord" class="text-sm cursor-pointer">
            Auto-play word audio on flip
            <Tooltip content="Automatically read the headword aloud when you flip a card." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.autoPlaySentence" input-id="autoPlaySentence" />
          <label for="autoPlaySentence" class="text-sm cursor-pointer">
            Auto-play example sentence audio on flip
            <Tooltip
              content="Automatically read the example sentence aloud when you flip a card. If both word and sentence are enabled, they play sequentially."
              placement="right"
            >
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.autoPlayWordOnFront" input-id="autoPlayWordOnFront" />
          <label for="autoPlayWordOnFront" class="text-sm cursor-pointer">
            Auto-play headword audio on front
            <Tooltip content="Automatically read the headword aloud when a new card appears, before flipping." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div v-if="form.autoPlayWordOnFront" class="flex items-center gap-2 ml-6">
          <ToggleSwitch v-model="form.autoPlayWordOnFrontNewOnly" input-id="autoPlayWordOnFrontNewOnly" />
          <label for="autoPlayWordOnFrontNewOnly" class="text-sm cursor-pointer">
            New cards only
            <Tooltip content="Only auto-play on cards you haven't seen before. Review cards will be silent." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div v-if="form.autoPlayWordOnFront" class="flex items-center gap-2 ml-6">
          <ToggleSwitch v-model="form.autoPlaySentenceOnFront" input-id="autoPlaySentenceOnFront" />
          <label for="autoPlaySentenceOnFront" class="text-sm cursor-pointer">
            Also play example sentence
            <Tooltip content="Play the example sentence after the headword audio finishes." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.autoPlayCustomAudio" input-id="autoPlayCustomAudio" />
          <label for="autoPlayCustomAudio" class="text-sm cursor-pointer">
            Auto-play custom card audio
            <Tooltip
              content="On cards with an uploaded audio clip, auto-play the clip. Independent of the headword audio settings above."
              placement="right"
            >
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div v-if="form.autoPlayCustomAudio" class="ml-6 flex flex-col gap-2">
          <div>
            <label class="text-sm mb-1 block">
              Play on
              <Tooltip
                content="**Front** — as a prompt when the card appears.<br>**Back** — after you flip.<br>**Both** — on the front and again on the back."
                placement="right"
              >
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
            <SelectButton
              v-model="form.autoPlayCustomAudioPosition"
              :options="cardAudioPositionOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
            />
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.autoPlayCustomAudioInstead" input-id="autoPlayCustomAudioInstead" />
            <label for="autoPlayCustomAudioInstead" class="text-sm cursor-pointer">
              Play instead of the headword
              <Tooltip
                content="When the headword audio also auto-plays on the same side, play the clip instead of it."
                placement="right"
              >
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
        </div>
        <p class="text-xs text-surface-500 dark:text-surface-400">
          Custom card audio is a
          <NuxtLink to="/jiten-plus" class="underline font-medium">Jiten+</NuxtLink>
          feature.
        </p>
      </div>

      <Divider />

      <!-- Study session UI -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Study session UI</h3>
      <div>
        <label class="block text-sm font-medium mb-1">
          Grading buttons
          <Tooltip
            content="**4 buttons** — Again, Hard, Good, Easy — gives finer control over scheduling.<br>**2 buttons** — Again and Good — simpler and faster to grade."
            placement="top"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton v-model="form.gradingButtons" :options="gradingOptions" option-label="label" option-value="value" :allow-empty="false" />
      </div>

      <div class="flex flex-col gap-2">
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.showNextInterval" input-id="showNextInterval" />
          <label for="showNextInterval" class="text-sm cursor-pointer">
            Show next interval on buttons
            <Tooltip content="Display the next review interval (e.g. '4d', '2w') on each grade button." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.showKeybinds" input-id="showKeybinds" />
          <label for="showKeybinds" class="text-sm cursor-pointer">
            Show keyboard shortcuts
            <Tooltip content="Display keyboard shortcut hints (1, 2, 3, 4) on the grade buttons." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.showElapsedTime" input-id="showElapsedTime" />
          <label for="showElapsedTime" class="text-sm cursor-pointer">
            Show elapsed time
            <Tooltip content="Display a timer showing how long you've spent in the study session." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.enableSwipeGesture" input-id="enableSwipeGesture" />
          <label for="enableSwipeGesture" class="text-sm cursor-pointer">
            Swipe to grade
            <Tooltip content="Swipe the card left (Again) or right (Good) to grade. Works with both mouse and touch." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
      </div>

      <Divider />

      <!-- Timed review -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Timed review</h3>
      <p class="text-sm text-surface-500 -mt-1 mb-2">
        Focus aid mode - each card counts down and can reveal or fail itself automatically. You can toggle it per session with the stopwatch icon on the study
        screen and pause it with a keybind.
      </p>
      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.timedReview.enabled" input-id="timedEnabled" />
        <label for="timedEnabled" class="text-sm cursor-pointer">
          Start each session with timed review on
          <Tooltip
            content="Turns timed review on at the start of every session. You can still flip it on or off per session with the stopwatch icon."
            placement="right"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>
      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.timedReview.showTimer" input-id="timedShowTimer" />
        <label for="timedShowTimer" class="text-sm cursor-pointer">
          Show the countdown bar
          <Tooltip content="Show the depleting bar above the card. Turn off to run the timers invisibly — they still reveal, fail, and beep." placement="right">
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>
      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.timedReview.skipNewCards" input-id="timedSkipNewCards" />
        <label for="timedSkipNewCards" class="text-sm cursor-pointer">
          Don't time new cards
          <Tooltip
            content="Skip the timer (front and back) on brand-new cards, so you can learn them at your own pace. Reviews are still timed."
            placement="right"
          >
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>

      <div class="flex flex-col gap-4 mt-1">
        <!-- Question-side timer -->
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.timedReview.revealEnabled" input-id="timedRevealEnabled" />
          <label for="timedRevealEnabled" class="text-sm cursor-pointer">
            Question timer
            <Tooltip content="Counts down while the question is shown, before you reveal the answer." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div v-if="form.timedReview.revealEnabled" :class="props.inline ? 'flex flex-col gap-4' : 'grid grid-cols-1 md:grid-cols-2 gap-4'">
          <div class="min-w-0">
            <label class="block text-sm font-medium mb-1">Seconds before it fires</label>
            <InputNumber v-model="form.timedReview.revealSeconds" :min="1" :max="600" :step="0.5" :max-fraction-digits="1" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
          </div>
          <div class="min-w-0">
            <label class="block text-sm font-medium mb-1">
              When time runs out
              <Tooltip
                content="**Reveal answer** — flips the card.<br><b>Reveal + auto-fail</b> — flips, then fails using your answer-timer style<br><b>Alert only</b> — just plays a sound."
                placement="top"
              >
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
            <SelectButton
              v-model="form.timedReview.revealAction"
              :options="revealActionOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
            />
          </div>
        </div>

        <!-- Answer-side timer -->
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.timedReview.answerEnabled" input-id="timedAnswerEnabled" />
          <label for="timedAnswerEnabled" class="text-sm cursor-pointer">
            Answer timer
            <Tooltip content="Counts down after the answer is shown, before you grade the card." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div v-if="form.timedReview.answerEnabled" :class="props.inline ? 'flex flex-col gap-4' : 'grid grid-cols-1 md:grid-cols-2 gap-4'">
          <div class="min-w-0">
            <label class="block text-sm font-medium mb-1">
              Seconds before it fires
              <Tooltip content="Set to 0 to fail instantly on reveal and skip the back entirely." placement="top">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
            <InputNumber v-model="form.timedReview.answerSeconds" :min="0" :max="600" :step="0.5" :max-fraction-digits="1" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
          </div>
          <div class="min-w-0">
            <label class="block text-sm font-medium mb-1">
              When time runs out
              <Tooltip
                content="**Soft fail** — highlights Again with a short grace countdown. You can press any other grade to override it.<br><b>Hard fail</b> — marks Again immediately and goes to the next card."
                placement="top"
              >
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
            <SelectButton
              v-model="form.timedReview.answerAction"
              :options="answerActionOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
            />
          </div>
        </div>

        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.timedReview.alertSound" input-id="timedAlertSound" />
          <label for="timedAlertSound" class="text-sm cursor-pointer">
            Play an alert sound
            <Tooltip content="A short beep when a timer runs out." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
      </div>

      <Divider />

      <!-- Leeches -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Leeches</h3>
      <p class="text-sm text-surface-500 -mt-1 mb-2">
        Leeches are cards that you keep forgetting. A card becomes a leech when it reaches a certain number of lapses. A lapse is counted each time you fail a
        review card (i.e. it goes back to relearning).
      </p>
      <div :class="props.inline ? 'flex flex-col gap-4' : 'grid grid-cols-1 md:grid-cols-2 gap-4'">
        <div class="min-w-0">
          <label class="block text-sm font-medium mb-1">
            Leech threshold
            <Tooltip content="Number of lapses before a card is flagged as a leech. Set to 0 to disable leech detection." placement="top">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <InputNumber v-model="form.leechThreshold" :min="0" :max="99" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
        </div>
        <div v-if="form.leechThreshold > 0" class="min-w-0">
          <label class="block text-sm font-medium mb-1">
            Leech action
            <Tooltip
              content="What happens when a card is flagged as a leech.<br><b>Suspend</b> — the card is automatically suspended and won't appear in reviews until you unsuspend it manually.<br><b>Notify only</b> — you'll see a notification but the card stays in rotation."
              placement="top"
            >
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <SelectButton v-model="form.leechAction" :options="leechActionOptions" option-label="label" option-value="value" :allow-empty="false" />
        </div>
      </div>

      <Divider />

      <!-- Keyboard shortcuts -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Keyboard shortcuts</h3>
      <p class="text-xs text-surface-500">Click a key and press the new key to rebind. Escape cancels.</p>
      <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div class="flex flex-col gap-2">
          <h4 class="text-xs font-medium text-surface-400 uppercase tracking-wide">Grading</h4>
          <KeybindInput
            v-for="[key, label] in gradeEntries"
            :key="key"
            v-model="form.keybinds[key]"
            :label="label"
            :conflict="checkConflict(key, form.keybinds[key])"
          />
        </div>
        <div class="flex flex-col gap-2">
          <h4 class="text-xs font-medium text-surface-400 uppercase tracking-wide">Actions</h4>
          <KeybindInput
            v-for="[key, label] in actionEntries"
            :key="key"
            v-model="form.keybinds[key]"
            :label="label"
            :conflict="checkConflict(key, form.keybinds[key])"
          />
        </div>
      </div>
      <div class="flex items-center justify-between">
        <p class="text-xs text-surface-400">Escape and Enter are always available as shortcuts for wrap up and flip card.</p>
        <Button severity="secondary" size="small" label="Reset to defaults" @click="resetKeybinds" />
      </div>

      <Divider />

      <!-- Study deck page -->
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Study deck page</h3>
      <div class="flex flex-col gap-2">
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.showReviewActivity" input-id="showReviewActivity" />
          <label for="showReviewActivity" class="text-sm cursor-pointer">
            Show review activity
            <Tooltip content="Display the review activity heatmap on the study decks page." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="form.showReviewForecast" input-id="showReviewForecast" />
          <label for="showReviewForecast" class="text-sm cursor-pointer">
            Show review forecast
            <Tooltip content="Display the 30-day review forecast chart on the study decks page." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
      </div>
    </div>
  </CardWrapper>
</template>

<style scoped>
  .save-chip-enter-active,
  .save-chip-leave-active {
    transition:
      opacity 0.2s ease,
      transform 0.2s ease;
  }
  .save-chip-enter-from,
  .save-chip-leave-to {
    opacity: 0;
    transform: translateY(0.5rem);
  }
</style>
