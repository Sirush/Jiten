<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{ inline?: boolean }>();

  const srsStore = useSrsStore();
  const toast = useToast();

  const form = reactive({ ...srsStore.studySettings });
  const saving = ref(false);
  const loaded = ref(false);

  onMounted(async () => {
    await srsStore.fetchSettings();
    Object.assign(form, srsStore.studySettings);
    exampleSentenceSource.value = form.exampleSentenceMediaTypes == null ? [null] : [...form.exampleSentenceMediaTypes];
    if (!form.timezone) applyDetectedTimezone();
    loaded.value = true;
    tickInterval = setInterval(() => { nowMinute.value = Date.now(); }, 60_000);
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

  const exampleSentenceSortingOptions = [
    { label: 'Random', value: 'Random' },
    { label: 'Easiest', value: 'EasiestFirst' },
    { label: 'Hardest', value: 'HardestFirst' },
  ];

  const exampleSentenceSourceOptions = [
    { label: 'All', value: null as number | null },
    { label: 'VN', value: 7 },
    { label: 'Novel', value: 4 },
    { label: 'WN', value: 8 },
    { label: 'VG', value: 6 },
    { label: 'NF', value: 5 },
  ];

  // null in the array represents "All"
  const exampleSentenceSource = ref<(number | null)[]>(
    form.exampleSentenceMediaTypes == null ? [null] : [...form.exampleSentenceMediaTypes]
  );

  function onExampleSourceChange(val: (number | null)[]) {
    const hadAll = form.exampleSentenceMediaTypes === null;
    const nowHasAll = val.includes(null);
    if (!hadAll && nowHasAll) {
      form.exampleSentenceMediaTypes = null;
      exampleSentenceSource.value = [null];
    } else {
      const specific = val.filter((v): v is number => v !== null);
      form.exampleSentenceMediaTypes = specific.length > 0 ? specific : null;
      exampleSentenceSource.value = specific.length > 0 ? specific : [null];
    }
  }

  function getUtcOffsetMinutes(zone: string, date?: Date): number {
    const parts = new Intl.DateTimeFormat('en-US', { timeZone: zone, hour: 'numeric', hour12: false, timeZoneName: 'shortOffset' })
      .formatToParts(date ?? new Date());
    const offsetStr = parts.find(p => p.type === 'timeZoneName')?.value ?? 'GMT';
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
  onUnmounted(() => clearInterval(tickInterval));

  const dayStartOptions = computed(() => {
    void nowMinute.value;
    const now = new Date();
    const zones = Intl.supportedValuesOf('timeZone')
      .filter(z => !z.startsWith('Etc/'));
    const items = zones.map(zone => {
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
      if (dayStartOptions.value.some(o => o.value === zone)) form.timezone = zone;
    } catch { /* no-op */ }
  }

  async function save() {
    saving.value = true;
    try {
      await srsStore.updateSettings({ ...form });
      toast.add({ severity: 'success', summary: 'Study settings saved', life: 2000 });
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to save settings', life: 3000 });
    } finally {
      saving.value = false;
    }
  }

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
</script>

<template>
  <CardWrapper :card="!props.inline">
    <div v-if="!loaded" class="flex justify-center py-4">
      <ProgressSpinner style="width: 24px; height: 24px" />
    </div>
    <div v-else class="flex flex-col gap-4">
      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Session limits</h3>
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
            <Tooltip content="Number of cards loaded at a time during a study session. Smaller batches keep sessions focused, larger batches reduce loading pauses." placement="top">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
          <InputNumber v-model="form.batchSize" :min="1" :max="999" :show-buttons="!props.inline" class="w-full [&_input]:w-full" />
        </div>
      </div>

      <div class="flex items-center gap-2">
        <ToggleSwitch v-model="form.countFailedReviews" input-id="countFailedReviews" />
        <label for="countFailedReviews" class="text-sm cursor-pointer">
          Count failed reviews toward daily limit
          <Tooltip content="When enabled, every review counts toward your daily limit, including repeated reviews of cards you got wrong. When disabled, only unique cards count, so failing a card multiple times won't eat into your daily budget." placement="top">
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
      </div>

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

      <Divider />

      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Scheduling</h3>
      <div>
        <label class="block text-sm font-medium mb-1">
          Card interleaving
          <Tooltip content="Controls how new cards and reviews are mixed.<br>**Mixed** — shuffles new and review cards together.<br>**New first** — shows all new cards before reviews.<br>**Reviews first** — clears your review backlog before introducing new cards." placement="top">
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton v-model="form.interleaving" :options="interleavingOptions" option-label="label" option-value="value" :allow-empty="false" class="flex-wrap" />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">
          New card gathering
          <Tooltip content="How new cards are picked when you have multiple decks.<br>**Top deck** — draws all new cards from your highest-priority deck first before moving to the next.<br>**All decks equally** — rotates between decks so you get new cards from each one.<br>**Cross-deck frequency** — draw words by total occurrence count across all your study decks, picking the words you'll see the most first." placement="top">
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton v-model="form.newCardGathering" :options="newCardGatheringOptions" option-label="label" option-value="value" :allow-empty="false" class="flex-wrap" />
      </div>

      <div>
        <label class="block text-sm font-medium mb-1">
          Review cards from
          <Tooltip content="Which words to include in your reviews.<br>**All tracked** — reviews every word you've ever studied, even if it's no longer in an active deck.<br>**Study decks only** — only reviews words that belong to your current study decks." placement="top">
            <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
          </Tooltip>
        </label>
        <SelectButton v-model="form.reviewFrom" :options="reviewFromOptions" option-label="label" option-value="value" :allow-empty="false" class="flex-wrap" />
      </div>

      <Divider />

      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Card appearance</h3>
      <div>
        <label class="block text-sm font-medium mb-2">Card front</label>
        <div class="flex flex-col gap-2">
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showCardStatus" input-id="showCardStatus" />
            <label for="showCardStatus" class="text-sm cursor-pointer">
              Show card status
              <Tooltip content="Display the card status (New, Review, Again) at the top of the card." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showFuriganaOnFront" input-id="showFuriganaOnFront" />
            <label for="showFuriganaOnFront" class="text-sm cursor-pointer">
              Show furigana
              <Tooltip content="Display furigana (reading hints) above kanji on the card front." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div v-if="form.showFuriganaOnFront" class="flex items-center gap-2 ml-6">
            <ToggleSwitch v-model="form.furiganaOnFrontNewOnly" input-id="furiganaOnFrontNewOnly" />
            <label for="furiganaOnFrontNewOnly" class="text-sm cursor-pointer">
              New cards only
              <Tooltip content="Only show furigana on cards you haven't seen before. Review cards will show plain kanji." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
        </div>
      </div>

      <div>
        <label class="block text-sm font-medium mb-2">Card back</label>
        <div class="flex flex-col gap-2">
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showPitchAccent" input-id="showPitchAccent" />
            <label for="showPitchAccent" class="text-sm cursor-pointer">
              Pitch accent
              <Tooltip content="Show the pitch accent pattern on the card back." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div>
            <label class="text-sm mb-1 block">
              Example sentence
              <Tooltip content="Show an example sentence from the media where the word appears.<br>**Hidden** — no sentence shown.<br>**Front** — sentence visible before you flip the card (sentence card).<br>**Back** — sentence shown only after you flip." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
            <SelectButton v-model="form.exampleSentencePosition" :options="exampleSentenceOptions" option-label="label" option-value="value" :allow-empty="false" />
            <div v-if="form.exampleSentencePosition !== 'Hidden'" class="flex items-center gap-2 mt-2">
              <ToggleSwitch v-model="form.blurExampleSentence" input-id="blurExampleSentence" />
              <label for="blurExampleSentence" class="text-sm cursor-pointer">
                Blur until clicked
                <Tooltip content="Example sentence is blurred by default. Click it to reveal." placement="right">
                  <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                </Tooltip>
              </label>
            </div>
            <div v-if="form.exampleSentencePosition !== 'Hidden'" class="mt-2">
              <label class="text-sm mb-1 block">
                Sorting
                <Tooltip content="**Random** — a random example sentence each time.<br>**Easiest** — prefer simpler sentences.<br>**Hardest** — prefer more complex sentences." placement="right">
                  <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                </Tooltip>
              </label>
              <SelectButton v-model="form.exampleSentenceSorting" :options="exampleSentenceSortingOptions" option-label="label" option-value="value" :allow-empty="false" />
            </div>
            <div v-if="form.exampleSentencePosition !== 'Hidden'" class="mt-2">
              <label class="text-sm mb-1 block">
                Example Sentence Media
                <Tooltip content="Only show example sentences of these types.<br>**VN** — Visual Novel<br>**WN** — Web Novel<br>**VG** — Video Games<br>**NF** — Non-Fiction" placement="right">
                  <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
                </Tooltip>
              </label>
              <SelectButton :model-value="exampleSentenceSource" :options="exampleSentenceSourceOptions" option-label="label" option-value="value" :allow-empty="false" :multiple="true" class="flex-wrap" @update:model-value="onExampleSourceChange" />
            </div>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showFrequencyRank" input-id="showFrequencyRank" />
            <label for="showFrequencyRank" class="text-sm cursor-pointer">
              Frequency rank
              <Tooltip content="Show how common the word is in Japanese, based on overall word frequency data." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showKanjiBreakdown" input-id="showKanjiBreakdown" />
            <label for="showKanjiBreakdown" class="text-sm cursor-pointer">
              Kanji breakdown
              <Tooltip content="Show the individual kanji that make up the word along with their usual meaning." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showWordComposition" input-id="showWordComposition" />
            <label for="showWordComposition" class="text-sm cursor-pointer">
              Word composition
              <Tooltip content="Show the component words that compose this word." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
          <div class="flex items-center gap-2">
            <ToggleSwitch v-model="form.showWordUsedIn" input-id="showWordUsedIn" />
            <label for="showWordUsedIn" class="text-sm cursor-pointer">
              Word used in
              <Tooltip content="Show other words that contain this word." placement="right">
                <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
              </Tooltip>
            </label>
          </div>
        </div>
      </div>

      <Divider />

      <h3 class="text-sm font-semibold text-surface-500 uppercase tracking-wide">Study session UI</h3>
      <div>
        <label class="block text-sm font-medium mb-1">
          Grading buttons
          <Tooltip content="**4 buttons** — Again, Hard, Good, Easy — gives finer control over scheduling.<br>**2 buttons** — Forgot and Remembered — simpler and faster to grade." placement="top">
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
            Auto-play example sentence on flip
            <Tooltip content="Automatically read the example sentence aloud when you flip a card. If both word and sentence are enabled, they play sequentially." placement="right">
              <i class="pi pi-info-circle text-xs text-surface-400 ml-1 cursor-help" />
            </Tooltip>
          </label>
        </div>
      </div>

      <Divider />

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

      <Button label="Save" :loading="saving" class="w-full md:w-auto" @click="save" />
    </div>
  </CardWrapper>
</template>
