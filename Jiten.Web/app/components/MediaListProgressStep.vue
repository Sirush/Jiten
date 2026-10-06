<script setup lang="ts">
  import { type Deck, type DeckStatus, type MediaListEntrySummary, type MediaListEntriesResponse, type MediaListSnapshot, MediaListEntryState } from '~/types';
  import { useToast } from 'primevue/usetoast';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import {
    ENTRY_MIN_DATE,
    canTrackUnits,
    dateToIso,
    entryMaxDate,
    formatReadDate,
    isoToDate,
    isoToday,
    mediaWords,
    progressFieldLabel,
    unitProgressLabel,
    unitWord,
  } from '~/utils/mediaListEntry';

  const props = defineProps<{
    deck: Deck;
    showBack?: boolean;
  }>();

  const emit = defineEmits<{
    back: [];
    done: [];
    complete: [];
    openHistory: [];
  }>();

  interface UnitsResponse {
    status: DeckStatus;
    listEntry: MediaListEntrySummary | null;
    allChildrenCompleted: boolean;
    volumes: { deckId: number; status: DeckStatus; listEntry: MediaListEntrySummary | null }[];
    previous: MediaListSnapshot[];
  }

  const { $api } = useNuxtApp();
  const toast = useToast();
  const { publishEntries, offerUndo } = useMediaListApi();

  const { state: saveState, enqueue, latest } = useDeckSaveQueue(props.deck.deckId);
  const pickingDate = ref(false);
  const reachedEnd = ref<'units' | 'characters' | null>(null);
  const maxDate = entryMaxDate();

  const entry = computed(() => props.deck.listEntry ?? null);
  const words = computed(() => mediaWords(props.deck.mediaType));
  const tracksUnits = computed(() => canTrackUnits(props.deck) && !!entry.value?.unitCount);
  const unitCount = computed(() => entry.value?.unitCount ?? 0);
  const units = computed(() => unitWord(props.deck.mediaType).toLowerCase());
  const unitsDraft = ref<number>(entry.value?.completedUnits ?? 0);
  let unitsTimer: ReturnType<typeof setTimeout> | undefined;
  let savingUnits = false;
  watch(
    () => entry.value?.completedUnits,
    (value) => {
      if (!savingUnits && unitsTimer === undefined) unitsDraft.value = value ?? 0;
    }
  );

  function save(patch: { startedOn?: string | null; charactersRead?: number | null }) {
    return enqueue(async () => {
      const current = latest() ?? entry.value;
      try {
        const response =
          current?.entryId != null
            ? await $api<MediaListEntriesResponse>(`user/media-list/entries/${props.deck.deckId}/${current.entryId}`, { method: 'PUT', body: patch })
            : await $api<MediaListEntriesResponse>(`user/media-list/entries/${props.deck.deckId}`, {
                method: 'POST',
                body: { ...patch, state: MediaListEntryState.InProgress },
              });
        publishEntries(response, props.deck);
        return response.summary;
      } catch (e) {
        toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not save your progress'), life: 5000 });
        return false;
      }
    });
  }

  const startDate = computed(() => (entry.value?.startedOn ? isoToDate(entry.value.startedOn) : null));

  async function pickStart(value: Date | Date[] | (Date | null)[] | null | undefined) {
    if (value != null && !(value instanceof Date)) return;
    pickingDate.value = false;
    await save({ startedOn: value ? dateToIso(value) : null });
  }

  async function saveCharacters(value: number | null) {
    const saved = await save({ charactersRead: value });
    reachedEnd.value = saved && props.deck.characterCount > 0 && (value ?? 0) >= props.deck.characterCount ? 'characters' : null;
    return saved;
  }

  function onUnits(value: number | null) {
    unitsDraft.value = Math.min(Math.max(value ?? 0, 0), unitCount.value);
    clearTimeout(unitsTimer);
    unitsTimer = setTimeout(saveUnits, 400);
  }
  onBeforeUnmount(() => {
    if (unitsTimer !== undefined) saveUnits();
  });

  async function saveUnits() {
    clearTimeout(unitsTimer);
    unitsTimer = undefined;
    if (savingUnits) return;
    const completed = unitsDraft.value;
    if (completed === ((latest() ?? entry.value)?.completedUnits ?? 0)) return;
    savingUnits = true;
    await enqueue(async () => {
      try {
        const response = await $api<UnitsResponse>(`user/media-list/units/${props.deck.deckId}`, {
          method: 'POST',
          body: { completed, date: isoToday() },
        });
        for (const volume of response.volumes) publishMediaListChange(volume);
        publishMediaListChange({ deckId: props.deck.deckId, status: response.status, listEntry: response.listEntry });
        offerUndo(`${unitProgressLabel(props.deck.mediaType)}: ${completed} of ${unitCount.value}`, response.previous);
        reachedEnd.value = response.allChildrenCompleted && unitsDraft.value === completed ? 'units' : null;
        return response.listEntry;
      } catch (e) {
        unitsDraft.value = entry.value?.completedUnits ?? 0;
        toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not save your progress'), life: 5000 });
        return false;
      } finally {
        savingUnits = false;
      }
    });
    if (unitsTimer === undefined && unitsDraft.value !== completed) await saveUnits();
  }

  const reachedEndMessage = computed(() =>
    reachedEnd.value === 'units' ? `All ${unitCount.value} ${units.value} done. Mark it as completed?` : 'Reached 100%. Mark it as completed?'
  );

  const root = ref<HTMLElement>();
  onMounted(() => root.value?.focus({ preventScroll: true }));

  const linkClass = 'text-xs text-primary-600 dark:text-primary-300 hover:underline cursor-pointer disabled:opacity-50';
</script>

<template>
  <div ref="root" class="flex flex-col gap-3 w-80 max-w-[calc(100vw-2rem)] p-2 outline-none" role="group" aria-label="Log progress" tabindex="-1">
    <div class="flex items-center gap-1 -ml-1 -mt-1">
      <button
        v-if="showBack"
        type="button"
        class="p-1.5 rounded hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors cursor-pointer"
        aria-label="Back to statuses"
        @click="emit('back')"
      >
        <i class="pi pi-arrow-left text-xs" />
      </button>
      <span class="text-sm font-semibold">Log progress</span>
      <SaveStateNote :state="saveState" class="ml-auto" />
    </div>

    <div class="flex items-baseline justify-between gap-3 text-sm">
      <span class="text-xs text-gray-600 dark:text-gray-300">Started</span>
      <span class="flex items-baseline gap-2">
        <span class="tabular-nums">{{ entry?.startedOn ? formatReadDate(entry.startedOn) : 'Unknown' }}</span>
        <button type="button" :class="linkClass" :aria-expanded="pickingDate" @click="pickingDate = !pickingDate">
          {{ pickingDate ? 'Cancel' : 'Change' }}
        </button>
      </span>
    </div>
    <DatePicker
      v-if="pickingDate"
      :model-value="startDate"
      inline
      show-button-bar
      :max-date="maxDate"
      :min-date="ENTRY_MIN_DATE"
      class="w-full"
      @update:model-value="pickStart"
    />

    <div v-if="tracksUnits" class="flex items-center justify-between gap-3">
      <label :for="`units-${deck.deckId}`" class="text-xs text-gray-600 dark:text-gray-300">{{ unitProgressLabel(deck.mediaType) }}</label>
      <span class="flex items-center gap-2 text-sm tabular-nums">
        <InputNumber
          :model-value="unitsDraft"
          :input-id="`units-${deck.deckId}`"
          :min="0"
          :max="unitCount"
          show-buttons
          button-layout="horizontal"
          increment-button-icon="pi pi-plus"
          decrement-button-icon="pi pi-minus"
          size="small"
          input-class="w-12 text-center tabular-nums"
          @update:model-value="onUnits"
        />
        <span class="text-gray-600 dark:text-gray-300">of {{ unitCount }}</span>
      </span>
    </div>

    <EntryCharactersField
      :model-value="entry?.charactersRead ?? null"
      :deck-characters="deck.characterCount"
      :completed="false"
      :label="progressFieldLabel(deck.mediaType)"
      :fallback="entry?.volumeCharacters"
      :fallback-from="units"
      @commit="saveCharacters"
    />

    <ReachedEndPrompt v-if="reachedEnd" :message="reachedEndMessage" @dismiss="reachedEnd = null" @complete="emit('complete')" />

    <div class="flex items-center justify-between gap-3 pt-1 border-t border-gray-200 dark:border-gray-700">
      <button type="button" class="flex items-center gap-1.5 pt-2" :class="linkClass" @click="emit('openHistory')">
        <i class="pi pi-history text-[10px]" aria-hidden="true" />{{ words.historyTitle }}
      </button>
      <Button label="Done" size="small" class="mt-2" @click="emit('done')" />
    </div>
  </div>
</template>
