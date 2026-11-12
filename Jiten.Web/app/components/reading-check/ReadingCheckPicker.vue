<script setup lang="ts">
  import { debounce } from 'perfect-debounce';
  import type { MediaSuggestion } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { mediaWords, unitWord } from '~/utils/mediaListEntry';
  import { toggled } from '~/utils/toggled';
  import { READING_MIN_CANDIDATES, READING_MIN_UNITS, type ReadingCount, type ReadingWork } from '~/types/readingCheck';

  const emit = defineEmits<{ start: [deckIds: number[]] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  const works = ref<ReadingWork[] | null>(null);
  const selected = ref<Record<string, Set<number>>>({});
  const expanded = ref<Set<string>>(new Set());
  const COLLAPSED_ROWS = 8;
  const showAllRows = ref(false);
  const visibleWorks = computed(() => (showAllRows.value ? (works.value ?? []) : (works.value ?? []).slice(0, COLLAPSED_ROWS)));
  const count = ref<ReadingCount | null>(null);
  const counting = ref(false);
  const countFailed = ref(false);
  const loadFailed = ref(false);

  const searchText = ref('');
  const { suggestions, fetchSuggestions, clearSuggestions } = useMediaSuggestions({ limit: 8 });

  async function load() {
    try {
      const rows = await $api<ReadingWork[]>('reading-check/works');
      works.value = rows;
      const next: Record<string, Set<number>> = {};
      for (const row of rows) next[row.key] = new Set(row.units.filter((u) => u.ticked).map((u) => u.deckId));
      selected.value = next;
    } catch (error) {
      works.value = [];
      loadFailed.value = true;
      toast.add({ severity: 'error', detail: extractApiError(error, 'Could not load your media list.'), life: 5000 });
    }
  }

  const selectedDeckIds = computed(() => Object.values(selected.value).flatMap((s) => [...s]));

  let countRequest = 0;
  const refreshCount = debounce(async () => {
    const request = ++countRequest;
    countFailed.value = false;
    if (selectedDeckIds.value.length === 0) {
      count.value = { units: 0, candidates: 0 };
      counting.value = false;
      return;
    }
    try {
      const result = await $api<ReadingCount>('reading-check/count', { method: 'POST', body: { deckIds: selectedDeckIds.value } });
      if (request === countRequest) count.value = result;
    } catch {
      if (request === countRequest) {
        count.value = null;
        countFailed.value = true;
      }
    } finally {
      if (request === countRequest) counting.value = false;
    }
  }, 500);

  watch(
    selectedDeckIds,
    () => {
      counting.value = true;
      refreshCount();
    },
    { deep: true }
  );

  const startHint = computed(() => {
    if (counting.value || !count.value) return null;
    if (count.value.units < READING_MIN_UNITS) return `Add at least ${READING_MIN_UNITS} volumes or titles to start.`;
    if (count.value.candidates < READING_MIN_CANDIDATES) return 'Add more volumes or titles for a reliable result.';
    return null;
  });

  const canStart = computed(() => !!count.value && !counting.value && !startHint.value);

  function rowMeta(row: ReadingWork): string {
    const type = getMediaTypeText(row.mediaType);
    return row.units.length === 1 && row.units[0]!.inProgress ? `${type}, in progress. Only finished works count.` : type;
  }

  function selectedCount(row: ReadingWork) {
    return selected.value[row.key]?.size ?? 0;
  }

  function isTicked(row: ReadingWork, deckId: number) {
    return selected.value[row.key]?.has(deckId) ?? false;
  }

  function setUnits(row: ReadingWork, ids: number[]) {
    selected.value = { ...selected.value, [row.key]: new Set(ids) };
  }

  function toggleUnit(row: ReadingWork, deckId: number) {
    setUnits(row, [...toggled(selected.value[row.key], deckId)]);
  }

  function toggleRow(row: ReadingWork) {
    setUnits(row, selectedCount(row) > 0 ? [] : row.units.filter((u) => !u.inProgress).map((u) => u.deckId));
  }

  function firstN(row: ReadingWork): number {
    const set = selected.value[row.key] ?? new Set();
    let n = 0;
    for (const unit of row.units) {
      if (!set.has(unit.deckId)) break;
      n++;
    }
    return n;
  }

  function setFirstN(row: ReadingWork, n: number | null) {
    const value = Math.max(0, Math.min(row.units.length, Math.round(n ?? 0)));
    setUnits(
      row,
      row.units.slice(0, value).map((u) => u.deckId)
    );
  }

  function units(row: ReadingWork) {
    return { many: unitWord(row.mediaType).toLowerCase(), done: mediaWords(row.mediaType).past.toLowerCase() };
  }

  function rangeLabel(row: ReadingWork): string {
    const set = selected.value[row.key] ?? new Set();
    const orders = row.units.flatMap((u, i) => (set.has(u.deckId) ? [i + 1] : []));
    const noun = units(row);
    const capital = unitWord(row.mediaType);
    if (orders.length === 0) return `None of ${row.units.length} ${noun.many} ${noun.done}`;
    const contiguous = orders.every((o, i) => i === 0 || o === orders[i - 1]! + 1);
    const span = contiguous ? (orders.length === 1 ? `${orders[0]}` : `${orders[0]}-${orders.at(-1)}`) : `${orders.length}`;
    return contiguous ? `${capital} ${noun.done}: ${span} of ${row.units.length}` : `${span} of ${row.units.length} ${noun.many} ${noun.done}`;
  }

  async function addWork(deck: MediaSuggestion) {
    searchText.value = '';
    clearSuggestions();
    try {
      const row = await $api<ReadingWork>(`reading-check/works/${deck.deckId}`);
      const existing = works.value?.find((w) => w.key === row.key);
      if (existing) {
        const set = new Set(selected.value[row.key] ?? []);
        row.units.filter((u) => u.ticked).forEach((u) => set.add(u.deckId));
        setUnits(existing, [...set]);
      } else {
        works.value = [row, ...(works.value ?? [])];
        setUnits(
          row,
          row.units.filter((u) => u.ticked).map((u) => u.deckId)
        );
        if (row.units.length > 1 && !row.units.some((u) => u.ticked)) expanded.value = new Set(expanded.value).add(row.key);
      }
    } catch (error) {
      toast.add({ severity: 'error', detail: extractApiError(error, 'Could not add this title.'), life: 5000 });
    }
  }

  function toggleExpanded(key: string) {
    expanded.value = toggled(expanded.value, key);
  }

  function start() {
    if (canStart.value) emit('start', selectedDeckIds.value);
  }

  onMounted(async () => {
    await load();
    refreshCount();
  });
</script>

<template>
  <div class="flex flex-col gap-4">
    <template v-if="works === null">
      <Skeleton v-for="i in 3" :key="i" height="3.5rem" />
    </template>

    <div
      v-else-if="works.length === 0"
      class="flex flex-col items-center gap-1.5 px-6 py-6 rounded-xl border border-dashed border-gray-300 dark:border-neutral-600 text-center"
    >
      <Icon name="material-symbols:menu-book-outline-rounded" size="1.75em" class="text-gray-400" />
      <span class="font-semibold">{{ loadFailed ? "Your media list didn't load" : 'Nothing finished on your media list yet' }}</span>
      <span class="text-sm text-gray-600 dark:text-gray-300"
        >Search for the volumes and titles you finished, including ones you read before joining Jiten.</span
      >
    </div>

    <div v-else class="flex flex-col">
      <span class="text-[11px] font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400 pb-1.5">
        {{ works.some((w) => w.fromMediaList) ? 'From your media list' : 'Your selection' }}
      </span>
      <div v-for="row in visibleWorks" :key="row.key" class="border-t border-gray-100 dark:border-neutral-800 py-2.5 flex flex-col gap-2">
        <div class="flex items-center gap-3">
          <Checkbox
            :model-value="selectedCount(row) > 0"
            binary
            :input-id="`work-${row.key}`"
            :indeterminate="selectedCount(row) > 0 && selectedCount(row) < row.units.length && row.units.length > 1"
            @update:model-value="toggleRow(row)"
          />
          <img :src="coverUrl(row.coverName, 'xs')" alt="" class="w-7 h-10 rounded-sm object-cover shrink-0 bg-gray-200 dark:bg-neutral-800" loading="lazy" />
          <label :for="`work-${row.key}`" class="flex-1 min-w-0 flex flex-col cursor-pointer">
            <span v-bind="japaneseTextAttrs(localiseTitle(row))" class="font-semibold truncate">{{ localiseTitle(row) }}</span>
            <span class="text-xs text-gray-600 dark:text-gray-300">{{ rowMeta(row) }}</span>
            <span v-if="row.units.length > 1" class="sm:hidden text-xs text-gray-700 dark:text-gray-300">{{ rangeLabel(row) }}</span>
          </label>
          <template v-if="row.units.length > 1">
            <span class="hidden sm:inline text-[13px] text-gray-700 dark:text-gray-300 whitespace-nowrap">{{ rangeLabel(row) }}</span>
            <Button
              :label="expanded.has(row.key) ? 'Done' : 'Edit'"
              size="small"
              text
              :aria-expanded="expanded.has(row.key)"
              @click="toggleExpanded(row.key)"
            />
          </template>
        </div>
        <div v-if="row.units.length > 1 && expanded.has(row.key)" class="ml-8 flex flex-col gap-3 p-3 rounded-lg bg-gray-50 dark:bg-neutral-800/60">
          <div class="flex flex-wrap items-center gap-2 text-sm text-gray-700 dark:text-gray-300">
            <span>First</span>
            <div class="w-32">
              <InputNumber
                :model-value="firstN(row)"
                :min="0"
                :max="row.units.length"
                show-buttons
                button-layout="horizontal"
                size="small"
                fluid
                :input-style="{ textAlign: 'center', minWidth: '2.5rem' }"
                :aria-label="`${units(row).many} ${units(row).done} from the start of ${localiseTitle(row)}`"
                @update:model-value="setFirstN(row, $event)"
              />
            </div>
            <span>of {{ row.units.length }} {{ units(row).many }}</span>
          </div>
          <div class="grid grid-cols-1 sm:grid-cols-2 gap-x-6 gap-y-1">
            <label v-for="(unit, i) in row.units" :key="unit.deckId" class="flex items-center gap-2.5 text-sm min-h-8 cursor-pointer">
              <Checkbox :model-value="isTicked(row, unit.deckId)" binary @update:model-value="toggleUnit(row, unit.deckId)" />
              <span v-bind="japaneseTextAttrs(unit.title)" class="truncate">{{ i + 1 }}. {{ unit.title }}</span>
              <span v-if="unit.inProgress" class="text-xs text-orange-800 dark:text-orange-300 shrink-0">In progress</span>
            </label>
          </div>
          <span v-if="row.units.some((u) => u.inProgress)" class="text-xs text-gray-600 dark:text-gray-300">
            Only finished {{ units(row).many }} count. Ones in progress stay unticked.
          </span>
        </div>
      </div>
    </div>

    <Button
      v-if="works && works.length > COLLAPSED_ROWS"
      :label="showAllRows ? 'Show fewer' : `Show all ${works.length} titles`"
      link
      class="self-start !p-0 !text-sm -mt-2"
      @click="showAllRows = !showAllRows"
    />

    <AutoComplete
      v-model="searchText"
      :suggestions="suggestions"
      option-label="originalTitle"
      placeholder="Search for a title you finished"
      fluid
      :pt="{ pcInputText: { root: { 'aria-label': 'Search for a title you finished' } } }"
      @complete="fetchSuggestions($event.query.trim())"
      @option-select="addWork($event.value)"
    >
      <template #option="{ option }">
        <div class="flex items-center gap-2.5">
          <img :src="coverUrl(option.coverName, 'xs')" alt="" class="w-6 h-8 rounded-sm object-cover shrink-0" loading="lazy" />
          <span v-bind="japaneseTextAttrs(localiseTitle(option))" class="truncate">{{ localiseTitle(option) }}</span>
          <span class="text-xs text-gray-500 shrink-0">{{ getMediaTypeText(option.mediaType) }}</span>
        </div>
      </template>
    </AutoComplete>

    <div class="flex flex-col sm:flex-row sm:items-center gap-3 pt-1">
      <p class="flex-1 text-sm text-gray-700 dark:text-gray-300" aria-live="polite">
        <template v-if="startHint">{{ startHint }}</template>
        <template v-else-if="count && count.candidates > 0">
          <span class="font-bold text-gray-900 dark:text-white tabular-nums">{{ count.candidates.toLocaleString() }} words</span>
          you met at least 5 times across {{ count.units }} {{ count.units === 1 ? 'volume or work' : 'volumes and works' }}
        </template>
        <template v-else-if="counting">Counting words</template>
        <template v-else-if="countFailed">The word count didn't load. Change your selection to try again.</template>
        <template v-else>{{ selectedDeckIds.length }} selected</template>
      </p>
      <Button label="Start" :disabled="!canStart" :loading="counting" @click="start" />
    </div>
  </div>
</template>
