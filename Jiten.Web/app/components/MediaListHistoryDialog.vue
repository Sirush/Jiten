<script setup lang="ts">
  import { type Deck, type MediaListEntry, type MediaListEntriesResponse, MediaListEntryState, DeckStatus } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { ordinal } from '~/utils/ordinal';
  import {
    ENTRY_MIN_DATE,
    dateToIso,
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
  const deck = shallowRef<Deck>(deckState.value!);

  function setDeck(next: Deck) {
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

  const hasEntryInProgress = computed(() => data.value?.entries.some((e) => e.state === MediaListEntryState.InProgress) ?? false);
  const canResume = computed(() => !hasEntryInProgress.value && data.value?.entries.some((e) => e.isCurrent && e.state === MediaListEntryState.Dropped));
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

  onMounted(load);

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

  function pace(entry: MediaListEntry) {
    const value = entryPace(entry, deck.value.characterCount);
    return value ? `${formatCharacters(value)} characters a day` : null;
  }
</script>

<template>
  <Dialog
    visible
    modal
    :header="words.historyTitle"
    class="w-full"
    style="max-width: 36rem"
    :pt="{ content: { class: 'flex flex-col gap-4' } }"
    @update:visible="!$event && close()"
  >
    <p class="text-sm text-gray-600 dark:text-gray-300" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</p>

    <div v-if="loading" class="flex justify-center py-6"><ProgressSpinner style="width: 2rem; height: 2rem" /></div>

    <div v-else-if="loadFailed" class="flex flex-col items-start gap-2 py-2" role="alert">
      <p class="text-sm text-gray-600 dark:text-gray-300">Could not load your history.</p>
      <Button label="Retry" icon="pi pi-refresh" size="small" severity="secondary" @click="retryLoad" />
    </div>

    <template v-else-if="data">
      <p v-if="data.entries.length === 0" class="text-sm text-gray-600 dark:text-gray-300">
        Nothing recorded yet. Set a status on this title, or add a past {{ words.noun }} below.
      </p>

      <ol class="flex flex-col gap-3">
        <li
          v-for="(entry, index) in data.entries"
          :key="entry.id"
          :ref="(el) => setEntryElement(entry.id, el)"
          class="rounded-lg border p-3 flex flex-col gap-3 transition-colors duration-700"
          :class="
            highlightedEntryId === entry.id
              ? 'border-primary-400 bg-primary-50 dark:border-primary-500 dark:bg-primary-900/20'
              : 'border-gray-200 dark:border-gray-700'
          "
        >
          <div class="flex items-center gap-2">
            <span class="font-semibold text-sm">{{ ordinal(index + 1) }} {{ words.noun }}</span>
            <Tag :value="stateLabel[entry.state]" :severity="stateSeverity[entry.state]" class="!text-xs" />
            <span v-if="entry.isCurrent && data.entries.length > 1" class="text-xs text-gray-500 dark:text-gray-400">Current</span>
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="secondary"
              class="ml-auto"
              :aria-label="`Delete ${ordinal(index + 1)} ${words.noun}`"
              :loading="runningAction === `delete-${entry.id}`"
              :disabled="otherActionRunning(`delete-${entry.id}`)"
              @click="remove(entry, index)"
            />
          </div>

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
