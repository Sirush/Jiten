<script setup lang="ts">
  import { type MediaListDeck, type MediaListEntry, type MediaListEntriesResponse, MediaListEntryState, DeckStatus } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { ordinal } from '~/utils/ordinal';
  import { isMainEntry } from '~/utils/displayDeck';
  import {
    ENTRY_MIN_DATE,
    dateToIso,
    entryDateFact,
    entryMaxDate,
    entryPace,
    formatCharacters,
    isoToDate,
    isoToday,
    mediaWords,
    progressFieldLabel,
    unitWord,
  } from '~/utils/mediaListEntry';

  const deckState = useHistoryDialogDeck();
  const deck = shallowRef<MediaListDeck>(deckState.value!);

  function setDeck(next: MediaListDeck) {
    deck.value = next;
    if (deckState.value?.deckId === next.deckId) deckState.value = next;
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const localiseTitle = useLocaliseTitle();
  const { postDeckStatus, publishEntries, offerUndo } = useMediaListApi();
  const { followUpCompletion } = useCompletionFollowUp();
  const { state: saveState, enqueue } = useDeckSaveQueue(deck.value.deckId);
  const deckDetails = useDeckDetails();
  const focusField = useEntryDialogFocus();

  const data = ref<MediaListEntriesResponse | null>(null);
  const loading = ref(true);
  const loadFailed = ref(false);
  const runningAction = ref<string | null>(null);

  const words = computed(() => mediaWords(deck.value.mediaType));
  const units = computed(() => unitWord(deck.value.mediaType).toLowerCase());
  const seriesCountNote = computed(() => `While a series is in progress, its own count only applies when it's higher than its ${units.value} add up to.`);
  const maxDate = entryMaxDate();

  const stateLabel: Record<MediaListEntryState, string> = {
    [MediaListEntryState.InProgress]: 'In progress',
    [MediaListEntryState.Completed]: 'Completed',
    [MediaListEntryState.Dropped]: 'Dropped',
  };
  const stateSeverity: Record<MediaListEntryState, string> = {
    [MediaListEntryState.InProgress]: 'warn',
    [MediaListEntryState.Completed]: 'success',
    [MediaListEntryState.Dropped]: 'danger',
  };

  const isPausedEntry = (entry: MediaListEntry) =>
    data.value?.status === DeckStatus.Paused && entry.isCurrent && entry.state === MediaListEntryState.InProgress;
  const entryLabel = (entry: MediaListEntry) => (isPausedEntry(entry) ? 'Paused' : stateLabel[entry.state]);
  const entrySeverity = (entry: MediaListEntry) => (isPausedEntry(entry) ? 'info' : stateSeverity[entry.state]);

  const hasEntryInProgress = computed(() => data.value?.entries.some((e) => e.state === MediaListEntryState.InProgress) ?? false);
  const canResume = computed(
    () =>
      data.value?.status === DeckStatus.Paused ||
      (!hasEntryInProgress.value && data.value?.entries.some((e) => e.isCurrent && e.state === MediaListEntryState.Dropped))
  );
  const startLabel = computed(() => {
    if (canResume.value) return 'Start over';
    return data.value?.entries.some((e) => e.state === MediaListEntryState.Completed) ? words.value.again : words.value.start;
  });

  function close() {
    if (deckState.value?.deckId === deck.value.deckId) deckState.value = null;
  }

  function load() {
    return enqueue(
      async () => {
        try {
          data.value = await $api<MediaListEntriesResponse>(`user/media-list/entries/${deck.value.deckId}`);
          loadFailed.value = false;
          return data.value.summary;
        } catch (e) {
          if (!data.value) loadFailed.value = true;
          else toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not load your history'), life: 5000 });
          return false;
        } finally {
          loading.value = false;
        }
      },
      { quiet: true }
    );
  }

  const NOTE_LIMIT = 500;
  const NOTE_SAVE_DELAY_MS = 800;
  const SCORE_SAVE_DELAY_MS = 500;

  const hasDetails = computed(() => isMainEntry(deck.value));
  const draft = useDeckDetailsDraft(() => deck.value, hasDetails.value ? knownDeckDetails(deck.value) : null);
  const { details, score, failed: detailsFailed } = draft;
  const note = ref(details.value?.note ?? '');
  const scoreInput = ref();
  const noteInput = ref();

  /** A library row already carries its details, so only other decks fetch them. */
  async function loadDetails() {
    if (!hasDetails.value || details.value) return;
    await draft.load();
    if (details.value) note.value = details.value.note ?? '';
  }

  const shown = ref(false);
  const detailsReady = ref(false);

  /** Waits for the dialog's show, which moves focus to its first control. */
  watch([shown, detailsReady], async ([isShown, ready]) => {
    const field = focusField.value;
    if (!isShown || !ready || !field) return;
    focusField.value = null;
    await nextTick();
    if (field === 'score') scoreInput.value?.focus();
    else noteInput.value?.$el?.focus();
  });

  onMounted(async () => {
    await Promise.all([load(), loadDetails()]);
    detailsReady.value = true;
  });

  let scoreTimer: ReturnType<typeof setTimeout> | undefined;
  watch(score, (value) => {
    clearTimeout(scoreTimer);
    if (!details.value || value === details.value.score) return;
    scoreTimer = setTimeout(() => draft.save({ score: value }), SCORE_SAVE_DELAY_MS);
  });

  const noteTooLong = computed(() => note.value.trim().length > NOTE_LIMIT);
  let noteTimer: ReturnType<typeof setTimeout> | undefined;

  function flushNote() {
    clearTimeout(noteTimer);
    const value = note.value.trim();
    if (!details.value || noteTooLong.value || value === (details.value.note ?? '')) return;
    void draft.save({ note: value });
  }

  watch(note, () => {
    clearTimeout(noteTimer);
    noteTimer = setTimeout(flushNote, NOTE_SAVE_DELAY_MS);
  });

  onBeforeUnmount(() => {
    clearTimeout(scoreTimer);
    if (details.value && score.value !== details.value.score) void draft.save({ score: score.value });
    flushNote();
  });

  const pinning = ref(false);
  /** A hidden title can't be pinned, but one pinned before it was hidden can still be unpinned. */
  const pinBlocked = computed(() => !!details.value?.isPrivate && !details.value.isPinned);

  async function setPinned(pinned: boolean) {
    pinning.value = true;
    try {
      await deckDetails.setPinned(deck.value, localiseTitle(deck.value), pinned);
    } finally {
      pinning.value = false;
    }
  }

  onMediaListChange((change) => {
    if (change.deckId === deck.value.deckId && 'listEntry' in change && change.listEntry !== data.value?.summary) void load();
  });

  function retryLoad() {
    loading.value = true;
    loadFailed.value = false;
    void load();
  }

  function accept(response: MediaListEntriesResponse) {
    data.value = response;
    setDeck({ ...deck.value, status: response.status, listEntry: response.summary });
    publishEntries(response, deck.value);
  }

  function run(request: () => Promise<MediaListEntriesResponse>) {
    return enqueue(async () => {
      try {
        const response = await request();
        accept(response);
        return response.summary;
      } catch (e) {
        toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not save the change'), life: 5000 });
        void load();
        return false;
      }
    });
  }

  async function action(name: string, request: () => Promise<MediaListEntriesResponse>) {
    runningAction.value = name;
    try {
      return await run(request);
    } finally {
      runningAction.value = null;
    }
  }

  const otherActionRunning = (name: string) => runningAction.value != null && runningAction.value !== name;

  type EntryPatch = Partial<Pick<MediaListEntry, 'startedOn' | 'finishedOn' | 'charactersRead'>>;

  function putEntry(entry: MediaListEntry, patch: EntryPatch) {
    const body = { startedOn: entry.startedOn, finishedOn: entry.finishedOn, charactersRead: entry.charactersRead, ...patch };
    return $api<MediaListEntriesResponse>(`user/media-list/entries/${deck.value.deckId}/${entry.id}`, { method: 'PUT', body });
  }

  const completePromptFor = ref<number | null>(null);
  const pickingFinishFor = ref<number | null>(null);

  const reachesEnd = (entry: MediaListEntry, characters: number | null | undefined) =>
    entry.state === MediaListEntryState.InProgress && entry.isCurrent && deck.value.characterCount > 0 && (characters ?? 0) >= deck.value.characterCount;

  async function save(entry: MediaListEntry, patch: EntryPatch) {
    const saved = await run(() => putEntry(data.value?.entries.find((e) => e.id === entry.id) ?? entry, patch));
    if (saved && 'charactersRead' in patch) completePromptFor.value = reachesEnd(entry, patch.charactersRead) ? entry.id : null;
    return saved;
  }

  async function markCompleted(date: string | null) {
    completePromptFor.value = null;
    pickingFinishFor.value = null;
    const current = deck.value;
    runningAction.value = 'complete';
    try {
      const completion = await postDeckStatus(current, DeckStatus.Completed, { date });
      if (!completion) return;
      await load();
      if (data.value) setDeck({ ...current, status: data.value.status, listEntry: data.value.summary });
      await followUpCompletion(current, completion, date, { beforePrompt: close });
    } finally {
      runningAction.value = null;
    }
  }

  const toDate = (iso: string | null) => (iso ? isoToDate(iso) : null);
  const fromDate = (value: Date | Date[] | (Date | null)[] | null | undefined) => (value instanceof Date ? dateToIso(value) : null);

  async function startPass(newEntry: boolean) {
    const current = deck.value;
    if (data.value?.status === DeckStatus.Ongoing) {
      await action('again', () =>
        $api<MediaListEntriesResponse>(`user/media-list/entries/${current.deckId}`, {
          method: 'POST',
          body: { state: MediaListEntryState.InProgress, startedOn: isoToday() },
        })
      );
      return;
    }

    runningAction.value = newEntry ? 'again' : 'resume';
    try {
      if (!(await postDeckStatus(current, DeckStatus.Ongoing, { newEntry }))) return;
      await load();
      if (data.value) setDeck({ ...current, status: data.value.status, listEntry: data.value.summary });
    } finally {
      runningAction.value = null;
    }
  }

  const pickingPastFinish = ref(false);
  const highlightedEntryId = ref<number | null>(null);
  const entryElements = new Map<number, HTMLElement>();
  let highlightTimer: ReturnType<typeof setTimeout> | undefined;
  onBeforeUnmount(() => clearTimeout(highlightTimer));

  // The finish date comes first so the new entry sorts into its place instead of jumping to the top as undated.
  async function addPastRead(finishedOn: string | null) {
    pickingPastFinish.value = false;
    const before = new Set(data.value?.entries.map((e) => e.id));
    const statusBefore = data.value?.status;
    const saved = await action('past', () =>
      $api<MediaListEntriesResponse>(`user/media-list/entries/${deck.value.deckId}`, {
        method: 'POST',
        body: { state: MediaListEntryState.Completed, finishedOn },
      })
    );

    if (saved && data.value && statusBefore !== DeckStatus.Completed && data.value.status === DeckStatus.Completed)
      void followUpCompletion(deck.value, data.value, finishedOn, { beforePrompt: close });
    const added = saved ? data.value?.entries.find((e) => !before.has(e.id)) : undefined;
    if (!added) return;
    highlightedEntryId.value = added.id;
    toggledEntries.value = new Map(toggledEntries.value).set(added.id, true);
    await nextTick();
    entryElements.get(added.id)?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    clearTimeout(highlightTimer);
    highlightTimer = setTimeout(() => (highlightedEntryId.value = null), 2500);
  }

  function setEntryElement(id: number, el: unknown) {
    if (el instanceof HTMLElement) entryElements.set(id, el);
    else entryElements.delete(id);
  }

  function removeMessage(entry: MediaListEntry, index: number) {
    const question = `Delete your ${ordinal(index + 1)} ${words.value.noun} from the history?`;
    const entries = data.value?.entries ?? [];
    const lastCompletion =
      data.value?.status === DeckStatus.Completed && entry.isCurrent && !entries.some((e) => e.id !== entry.id && e.state === MediaListEntryState.Completed);
    if (!lastCompletion) return `${question} Your status will stay as it is.`;
    return entries.length > 1
      ? `${question} It's your only completed ${words.value.noun}, so the title will no longer be marked as Completed.`
      : `${question} It's the only one, so the title will be removed from your list.`;
  }

  function remove(entry: MediaListEntry, index: number) {
    confirm.require({
      message: removeMessage(entry, index),
      header: 'Delete from history',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      rejectProps: { severity: 'secondary' },
      accept: async () => {
        const deleted = await action(`delete-${entry.id}`, () =>
          $api<MediaListEntriesResponse>(`user/media-list/entries/${deck.value.deckId}/${entry.id}`, { method: 'DELETE' })
        );
        if (deleted) offerUndo('Deleted from your history', data.value?.previous);
      },
    });
  }

  const toggledEntries = ref(new Map<number, boolean>());

  function isExpanded(entry: MediaListEntry) {
    const toggled = toggledEntries.value.get(entry.id);
    if (toggled !== undefined) return toggled;
    return entry.state === MediaListEntryState.InProgress || data.value?.entries.length === 1;
  }

  function toggleEntry(entry: MediaListEntry) {
    toggledEntries.value = new Map(toggledEntries.value).set(entry.id, !isExpanded(entry));
  }

  const entrySummary = (entry: MediaListEntry) => entryDateFact(entry, { droppedVerb: 'Stopped', undated: true });

  function pace(entry: MediaListEntry) {
    const value = entryPace(entry, deck.value.characterCount);
    return value ? `${formatCharacters(value)} characters a day` : null;
  }
</script>

<template>
  <Dialog
    visible
    modal
    class="w-full"
    style="max-width: 36rem"
    :pt="{ content: { class: 'flex flex-col gap-4' } }"
    @update:visible="!$event && close()"
    @show="shown = true"
  >
    <template #header>
      <span class="p-dialog-title" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</span>
    </template>

    <div v-if="loading" class="flex justify-center py-6"><ProgressSpinner style="width: 2rem; height: 2rem" /></div>

    <div v-else-if="loadFailed" class="flex flex-col items-start gap-2 py-2" role="alert">
      <p class="text-sm text-gray-600 dark:text-gray-300">Could not load this entry.</p>
      <Button label="Retry" icon="pi pi-refresh" size="small" severity="secondary" @click="retryLoad" />
    </div>

    <template v-else-if="data">
      <div v-if="hasDetails" class="flex flex-col gap-4">
        <div v-if="detailsFailed" class="flex items-center gap-2 text-sm text-gray-600 dark:text-gray-300" role="alert">
          Couldn't load your score and note.
          <Button label="Retry" size="small" text @click="loadDetails" />
        </div>

        <div class="flex flex-col gap-1.5">
          <span id="entry-score-label" class="text-sm font-medium">Enjoyment score</span>
          <EntryScoreInput ref="scoreInput" v-model="score" labelled-by="entry-score-label" :disabled="!details" />
        </div>

        <div class="flex flex-col gap-1.5">
          <div class="flex items-baseline justify-between">
            <label for="entry-note" class="text-sm font-medium">Private note</label>
            <span class="text-xs tabular-nums" :class="noteTooLong ? 'text-red-600 dark:text-red-400' : 'text-gray-500 dark:text-gray-400'">
              {{ note.trim().length }} / {{ NOTE_LIMIT }}
            </span>
          </div>
          <Textarea
            id="entry-note"
            ref="noteInput"
            v-model="note"
            rows="2"
            auto-resize
            :disabled="!details"
            :invalid="noteTooLong"
            placeholder="Only you can see this"
            @blur="flushNote"
          />
        </div>

        <div class="flex flex-wrap gap-x-6 gap-y-2">
          <label class="flex items-center gap-2 text-sm cursor-pointer">
            <ToggleSwitch :model-value="details?.isPrivate ?? false" :disabled="!details" @update:model-value="draft.save({ isPrivate: $event })" />
            Hide from profile
          </label>
          <label class="flex items-center gap-2 text-sm" :class="pinBlocked ? 'text-gray-500 dark:text-gray-400' : 'cursor-pointer'">
            <ToggleSwitch :model-value="details?.isPinned ?? false" :disabled="!details || pinBlocked || pinning" @update:model-value="setPinned" />
            {{ pinBlocked ? "Pin to profile (hidden titles can't be pinned)" : 'Pin to profile' }}
          </label>
        </div>
      </div>

      <div class="flex flex-col gap-3" :class="hasDetails ? 'border-t border-gray-200 dark:border-gray-700 pt-4' : ''">
        <h3 class="text-sm font-semibold">{{ words.historyTitle }}</h3>

        <p v-if="data.entries.length === 0" class="text-sm text-gray-600 dark:text-gray-300">
          Nothing recorded yet. Set a status on this title, or add a past {{ words.noun }} below.
        </p>

        <ol class="flex flex-col gap-2">
          <li
            v-for="(entry, index) in data.entries"
            :key="entry.id"
            :ref="(el) => setEntryElement(entry.id, el)"
            class="rounded-lg border transition-colors duration-700"
            :class="
              highlightedEntryId === entry.id
                ? 'border-primary-400 bg-primary-50 dark:border-primary-500 dark:bg-primary-900/20'
                : 'border-gray-200 dark:border-gray-700'
            "
          >
            <div class="flex items-center gap-1 pr-1">
              <button
                type="button"
                class="flex flex-1 min-w-0 items-center gap-2 px-3 py-2 text-left rounded-lg cursor-pointer hover:bg-gray-50 dark:hover:bg-gray-800/60 transition-colors"
                :aria-expanded="isExpanded(entry)"
                :aria-controls="`entry-${entry.id}`"
                @click="toggleEntry(entry)"
              >
                <i :class="['pi text-[10px] text-gray-500 dark:text-gray-400', isExpanded(entry) ? 'pi-chevron-down' : 'pi-chevron-right']" aria-hidden="true" />
                <span class="font-semibold text-sm whitespace-nowrap">{{ ordinal(index + 1) }} {{ words.noun }}</span>
                <Tag :value="entryLabel(entry)" :severity="entrySeverity(entry)" class="!text-xs" />
                <span v-if="entrySummary(entry)" class="truncate text-xs text-gray-600 dark:text-gray-300">{{ entrySummary(entry) }}</span>
                <span v-if="entry.isCurrent && data.entries.length > 1" class="ml-auto text-xs text-gray-500 dark:text-gray-400 whitespace-nowrap">Current</span>
              </button>
              <Button
                v-if="isExpanded(entry)"
                icon="pi pi-trash"
                text
                rounded
                size="small"
                severity="secondary"
                :aria-label="`Delete ${ordinal(index + 1)} ${words.noun}`"
                :loading="runningAction === `delete-${entry.id}`"
                :disabled="otherActionRunning(`delete-${entry.id}`)"
                @click="remove(entry, index)"
              />
            </div>

            <div v-if="isExpanded(entry)" :id="`entry-${entry.id}`" class="flex flex-col gap-3 px-3 pb-3">
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-3">
                <label class="flex flex-col gap-1 text-xs text-gray-600 dark:text-gray-300">
                  Started
                  <DatePicker
                    :model-value="toDate(entry.startedOn)"
                    date-format="yy-mm-dd"
                    show-icon
                    show-button-bar
                    fluid
                    size="small"
                    placeholder="Unknown"
                    :manual-input="false"
                    :max-date="toDate(entry.finishedOn) ?? maxDate"
                    :min-date="ENTRY_MIN_DATE"
                    @update:model-value="save(entry, { startedOn: fromDate($event) })"
                  />
                </label>
                <label v-if="entry.state !== MediaListEntryState.InProgress" class="flex flex-col gap-1 text-xs text-gray-600 dark:text-gray-300">
                  {{ entry.state === MediaListEntryState.Dropped ? 'Stopped' : 'Finished' }}
                  <DatePicker
                    :model-value="toDate(entry.finishedOn)"
                    date-format="yy-mm-dd"
                    show-icon
                    show-button-bar
                    fluid
                    size="small"
                    placeholder="Unknown"
                    :manual-input="false"
                    :max-date="maxDate"
                    :min-date="toDate(entry.startedOn) ?? ENTRY_MIN_DATE"
                    @update:model-value="save(entry, { finishedOn: fromDate($event) })"
                  />
                </label>
                <EntryCharactersField
                  class="sm:col-span-2"
                  :model-value="entry.charactersRead"
                  :deck-characters="deck.characterCount"
                  :completed="entry.state === MediaListEntryState.Completed"
                  :label="entry.state === MediaListEntryState.Completed ? undefined : progressFieldLabel(deck.mediaType)"
                  :fallback="entry.isCurrent ? data.summary?.volumeCharacters : null"
                  :fallback-from="units"
                  @commit="save(entry, { charactersRead: $event })"
                />
              </div>

              <div v-if="pickingFinishFor === entry.id" class="rounded-md border border-gray-200 dark:border-gray-700 p-1 bg-surface-0 dark:bg-surface-900">
                <FinishDateChoice
                  label="When did you finish?"
                  :release-date="deck.releaseDate"
                  :start-date="entry.startedOn"
                  back-label="Cancel"
                  closes
                  @choose="markCompleted"
                  @back="pickingFinishFor = null"
                />
              </div>
              <ReachedEndPrompt
                v-else-if="completePromptFor === entry.id"
                message="Reached 100%. Mark it as completed?"
                :loading="runningAction === 'complete'"
                @dismiss="completePromptFor = null"
                @complete="pickingFinishFor = entry.id"
              />

              <div v-if="pace(entry) || entry.coverageAtFinish != null" class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-gray-600 dark:text-gray-300">
                <span v-if="pace(entry)">{{ pace(entry) }}</span>
                <span v-if="entry.coverageAtFinish != null">Coverage when finished: {{ Math.round(entry.coverageAtFinish) }}%</span>
              </div>
            </div>
          </li>
        </ol>

        <p class="text-xs text-gray-500 dark:text-gray-400">
          You can set a count manually that goes higher than 100%.
          <template v-if="deck.childrenDeckCount > 0">{{ seriesCountNote }}</template>
        </p>

        <div v-if="pickingPastFinish" class="rounded-md border border-gray-200 dark:border-gray-700 p-1 self-end">
          <FinishDateChoice
            :label="`When did you finish that ${words.noun}?`"
            :release-date="deck.releaseDate"
            back-label="Cancel"
            closes
            @choose="addPastRead"
            @back="pickingPastFinish = false"
          />
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-2 pt-1">
        <SaveStateNote :state="saveState" class="mr-auto" />
        <Button
          :label="`Add a past ${words.noun}`"
          icon="pi pi-history"
          size="small"
          severity="secondary"
          :loading="runningAction === 'past'"
          :disabled="otherActionRunning('past')"
          :aria-expanded="pickingPastFinish"
          @click="pickingPastFinish = !pickingPastFinish"
        />
        <Button
          v-if="canResume"
          label="Resume"
          icon="pi pi-play"
          size="small"
          severity="secondary"
          :loading="runningAction === 'resume'"
          :disabled="otherActionRunning('resume')"
          @click="startPass(false)"
        />
        <Button
          v-if="!hasEntryInProgress"
          :label="startLabel"
          icon="pi pi-replay"
          size="small"
          :loading="runningAction === 'again'"
          :disabled="otherActionRunning('again')"
          @click="startPass(true)"
        />
      </div>
    </template>
  </Dialog>
</template>
