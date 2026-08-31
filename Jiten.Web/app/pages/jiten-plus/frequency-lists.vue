<script setup lang="ts">
  import { ref, computed, reactive, watch, nextTick, onMounted, onBeforeUnmount } from 'vue';
  import { debounce } from 'perfect-debounce';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import Slider from 'primevue/slider';
  import MultiSelect from 'primevue/multiselect';
  import AutoComplete from 'primevue/autocomplete';
  import type { AutoCompleteCompleteEvent } from 'primevue/autocomplete';
  import Tag from 'primevue/tag';
  import SelectButton from 'primevue/selectbutton';
  import Checkbox from 'primevue/checkbox';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import Dialog from 'primevue/dialog';
  import ProgressSpinner from 'primevue/progressspinner';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { useApiFetch } from '~/composables/useApiFetch';
  import { coverUrl } from '~/utils/coverImage';
  import type { Tag as MediaTag, MediaSuggestion } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getAllGenres } from '~/utils/genreMapper';
  import { extractApiError } from '~/utils/toast';
  import type { TagState } from '~/components/TriStateTag.vue';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Custom Frequency Lists - Jiten' });

  const { $api } = useNuxtApp();
  const config = useRuntimeConfig();
  const toast = useToast();
  const confirm = useConfirm();
  const { isPlus, isFull, isTrial } = useJitenPlus();
  const localiseTitle = useLocaliseTitle();

  // ---- Builder state ------------------------------------------------------

  type Mode = 'filters' | 'handpicked';
  const modeOptions = [
    { label: 'Filters', value: 'filters' as Mode },
    { label: 'Pick decks', value: 'handpicked' as Mode },
  ];
  const mode = ref<Mode>('filters');
  const name = ref('');
  const saveList = ref(false);
  const autoUpdate = ref(false);

  const editingId = ref<number | null>(null);
  const editingOriginalName = ref('');
  const builderEl = ref<HTMLElement | null>(null);

  const currentYear = new Date().getFullYear();
  const mediaTypes = ref<number[]>([]);
  const yearFrom = ref<number | null>(null);
  const yearTo = ref<number | null>(null);
  const difficultyRange = ref<[number, number]>([0, 5]);

  const genreStates = reactive<Record<number, TagState>>({});
  const tagStates = reactive<Record<number, TagState>>({});
  const genreSearch = ref('');
  const tagSearch = ref('');

  const pickedDecks = ref<MediaSuggestion[]>([]);
  const selectedDeck = ref<MediaSuggestion | string | null>(null);
  const deckSuggestions = ref<MediaSuggestion[]>([]);

  const mediaTypeOptions = getListedMediaTypes()
    .map((v) => ({ name: getMediaTypeText(v), id: v }))
    .sort((a, b) => a.name.localeCompare(b.name));

  const genres = getAllGenres();
  const { data: availableTags } = useApiFetch<MediaTag[]>('media-deck/tags', { server: true, lazy: false });

  // Per-facet deck counts for the current selection, keyed by genre/tag id. Populated by the preview call.
  const genreCounts = ref<Record<number, number>>({});
  const tagCounts = ref<Record<number, number>>({});

  const filteredGenres = computed(() => {
    const q = genreSearch.value.toLowerCase();
    return genres.filter((g) => {
      if (!g.label.toLowerCase().includes(q)) return false;
      if ((genreStates[g.value] ?? 'neutral') !== 'neutral') return true; // keep selected chips reachable
      if (previewCount.value === null) return true;
      return (genreCounts.value[g.value] ?? 0) > 0;
    });
  });
  const filteredTags = computed(() => {
    const q = tagSearch.value.toLowerCase();
    return (availableTags.value ?? []).filter((t) => {
      if (!t.name.toLowerCase().includes(q)) return false;
      if ((tagStates[t.tagId] ?? 'neutral') !== 'neutral') return true;
      if (previewCount.value === null) return true;
      return (tagCounts.value[t.tagId] ?? 0) > 0;
    });
  });

  function genreLabel(value: number, label: string): string {
    const c = genreCounts.value[value];
    return previewCount.value !== null && c != null ? `${label} (${c.toLocaleString()})` : label;
  }
  function tagLabel(tagId: number, name: string): string {
    const c = tagCounts.value[tagId];
    return previewCount.value !== null && c != null ? `${name} (${c.toLocaleString()})` : name;
  }

  function toIdList(states: Record<number, TagState>, wanted: TagState): number[] {
    return Object.entries(states)
      .filter(([, s]) => s === wanted)
      .map(([id]) => Number(id));
  }

  function buildDefinition() {
    const [difMin, difMax] = difficultyRange.value;
    return {
      mediaTypes: mediaTypes.value,
      yearFrom: yearFrom.value,
      yearTo: yearTo.value,
      genresInclude: toIdList(genreStates, 'include'),
      genresExclude: toIdList(genreStates, 'exclude'),
      tagsInclude: toIdList(tagStates, 'include'),
      tagsExclude: toIdList(tagStates, 'exclude'),
      difficultyMin: difMin > 0 ? difMin : null,
      difficultyMax: difMax < 5 ? difMax : null,
      deckIds: pickedDecks.value.map((d) => d.deckId),
    };
  }

  // ---- Live preview -------------------------------------------------------

  interface SampleTitle {
    originalTitle: string;
    romajiTitle?: string | null;
    englishTitle?: string | null;
  }

  const previewCount = ref<number | null>(null);
  const previewSample = ref<SampleTitle[]>([]);
  const previewLoading = ref(false);
  const minDecks = 2;

  function csv(values: number[]): string {
    return values.join(',');
  }

  const runPreview = debounce(async () => {
    previewLoading.value = true;
    try {
      const def = buildDefinition();
      const res = await $api<{
        deckCount: number;
        sampleTitles: SampleTitle[];
        minDecks: number;
        genreCounts: Record<number, number>;
        tagCounts: Record<number, number>;
      }>('frequency-lists/preview', {
        query: {
          mode: mode.value,
          mediaTypes: csv(def.mediaTypes),
          yearFrom: def.yearFrom ?? '',
          yearTo: def.yearTo ?? '',
          genresInclude: csv(def.genresInclude),
          genresExclude: csv(def.genresExclude),
          tagsInclude: csv(def.tagsInclude),
          tagsExclude: csv(def.tagsExclude),
          difficultyMin: def.difficultyMin ?? '',
          difficultyMax: def.difficultyMax ?? '',
          deckIds: csv(def.deckIds),
        },
      });
      previewCount.value = res.deckCount;
      previewSample.value = res.sampleTitles ?? [];
      genreCounts.value = res.genreCounts ?? {};
      tagCounts.value = res.tagCounts ?? {};
    } catch {
      previewCount.value = null;
      genreCounts.value = {};
      tagCounts.value = {};
    } finally {
      previewLoading.value = false;
    }
  }, 400);

  watch(
    [mode, mediaTypes, yearFrom, yearTo, difficultyRange, genreStates, tagStates, pickedDecks],
    () => {
      if (isPlus.value) runPreview();
    },
    { deep: true }
  );

  const hasFilters = computed(() => {
    if (mode.value === 'handpicked') return pickedDecks.value.length > 0;
    const [difMin, difMax] = difficultyRange.value;
    return (
      mediaTypes.value.length > 0 ||
      yearFrom.value != null ||
      yearTo.value != null ||
      difMin > 0 ||
      difMax < 5 ||
      Object.values(genreStates).some((s) => s && s !== 'neutral') ||
      Object.values(tagStates).some((s) => s && s !== 'neutral')
    );
  });

  const canGenerate = computed(() => (previewCount.value ?? 0) >= minDecks && name.value.trim().length > 0 && hasFilters.value);

  // ---- Deck picker --------------------------------------------------------

  const searchDecks = debounce(async (query: string) => {
    if (!query || query.length < 1) {
      deckSuggestions.value = [];
      return;
    }
    try {
      const res = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', {
        query: { query, limit: 10 },
      });
      deckSuggestions.value = res.suggestions ?? [];
    } catch {
      deckSuggestions.value = [];
    }
  }, 300);

  function onDeckComplete(e: AutoCompleteCompleteEvent) {
    searchDecks(e.query);
  }

  function onDeckSelect() {
    const deck = selectedDeck.value;
    if (deck && typeof deck !== 'string') {
      if (!pickedDecks.value.some((d) => d.deckId === deck.deckId)) pickedDecks.value = [...pickedDecks.value, deck];
    }
    selectedDeck.value = null;
  }

  function removePickedDeck(deckId: number) {
    pickedDecks.value = pickedDecks.value.filter((d) => d.deckId !== deckId);
  }

  const mediaListPickerVisible = ref(false);
  const pickedDeckIds = computed(() => pickedDecks.value.map((d) => d.deckId));

  function onMediaListAdd(decks: MediaSuggestion[]) {
    const existing = new Set(pickedDeckIds.value);
    pickedDecks.value = [...pickedDecks.value, ...decks.filter((d) => !existing.has(d.deckId))];
  }

  // ---- Saved / generated lists --------------------------------------------

  interface FrequencyListDefinitionDto {
    mediaTypes: number[];
    yearFrom: number | null;
    yearTo: number | null;
    genresInclude: number[];
    genresExclude: number[];
    tagsInclude: number[];
    tagsExclude: number[];
    difficultyMin: number | null;
    difficultyMax: number | null;
    deckIds: number[];
  }

  interface FrequencyListDto {
    id: number;
    name: string;
    mode: string;
    definition: FrequencyListDefinitionDto | null;
    isSaved: boolean;
    autoUpdate: boolean;
    publicSlug: string | null;
    status: 'pending' | 'generating' | 'ready' | 'failed' | 'expired';
    wordCount: number;
    deckCount: number;
    createdAt: string;
    generatedAt: string | null;
    pickedDecks: MediaSuggestion[] | null;
  }

  const lists = ref<FrequencyListDto[]>([]);
  const listsLoading = ref(false);
  const creating = ref(false);
  const busyId = ref<number | null>(null);
  let pollTimer: ReturnType<typeof setInterval> | null = null;

  const maxSavedLists = 25;
  const maxAutoUpdateLists = 3;
  const savedListsUsage = computed(() => lists.value.filter((l) => l.isSaved).length);
  const autoUpdateUsage = computed(() => lists.value.filter((l) => l.autoUpdate).length);

  function usageColor(current: number, max: number) {
    const ratio = current / max;
    if (ratio >= 0.95) return 'text-red-500 dark:text-red-400';
    if (ratio >= 0.8) return 'text-yellow-500 dark:text-yellow-400';
    return 'text-gray-500 dark:text-gray-400';
  }

  function resetBuilder() {
    editingId.value = null;
    editingOriginalName.value = '';
    name.value = '';
    mediaTypes.value = [];
    yearFrom.value = null;
    yearTo.value = null;
    difficultyRange.value = [0, 5];
    for (const k of Object.keys(genreStates)) genreStates[Number(k)] = 'neutral';
    for (const k of Object.keys(tagStates)) tagStates[Number(k)] = 'neutral';
    pickedDecks.value = [];
    saveList.value = false;
    autoUpdate.value = false;
    previewCount.value = null;
    previewSample.value = [];
    genreCounts.value = {};
    tagCounts.value = {};
  }

  async function loadLists() {
    if (!isPlus.value) return;
    listsLoading.value = true;
    try {
      lists.value = await $api<FrequencyListDto[]>('frequency-lists');
    } catch {
      lists.value = [];
    } finally {
      listsLoading.value = false;
    }
    schedulePolling();
  }

  function schedulePolling() {
    const anyPending = lists.value.some((l) => l.status === 'pending' || l.status === 'generating');
    if (anyPending && !pollTimer) {
      pollTimer = setInterval(loadLists, 4000);
    } else if (!anyPending && pollTimer) {
      clearInterval(pollTimer);
      pollTimer = null;
    }
  }

  async function generate() {
    if (!canGenerate.value) return;
    creating.value = true;
    try {
      await $api('frequency-lists', {
        method: 'POST',
        body: {
          name: name.value.trim(),
          mode: mode.value,
          save: isFull.value && saveList.value,
          autoUpdate: isFull.value && saveList.value && autoUpdate.value,
          definition: buildDefinition(),
        },
      });
      toast.add({ severity: 'success', summary: 'Generation started', detail: 'Your list is being built.', life: 4000 });
      resetBuilder();
      await loadLists();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Could not create list', detail: extractApiError(e, 'Please try again.'), life: 6000 });
    } finally {
      creating.value = false;
    }
  }

  async function download(list: FrequencyListDto, format: 'zip' | 'csv') {
    try {
      const blob = await $api<Blob>(`frequency-lists/${list.id}/download`, { query: { format }, responseType: 'blob' });
      const objectUrl = URL.createObjectURL(blob);
      const anchor = document.createElement('a');
      anchor.href = objectUrl;
      anchor.download = `${list.name}.${format}`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      URL.revokeObjectURL(objectUrl);
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Download failed', detail: extractApiError(e, 'The file may not be ready.'), life: 5000 });
    }
  }

  async function regenerate(list: FrequencyListDto) {
    busyId.value = list.id;
    try {
      await $api(`frequency-lists/${list.id}/regenerate`, { method: 'POST' });
      await loadLists();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Regenerate failed', detail: extractApiError(e, 'Please try again.'), life: 5000 });
    } finally {
      busyId.value = null;
    }
  }

  async function save(list: FrequencyListDto) {
    busyId.value = list.id;
    try {
      await $api(`frequency-lists/${list.id}/save`, { method: 'POST' });
      toast.add({ severity: 'success', summary: 'List saved', life: 3000 });
      await loadLists();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Could not save', detail: extractApiError(e, 'Please try again.'), life: 5000 });
    } finally {
      busyId.value = null;
    }
  }

  async function toggleAutoUpdate(list: FrequencyListDto) {
    busyId.value = list.id;
    try {
      await $api(`frequency-lists/${list.id}`, { method: 'PATCH', body: { autoUpdate: !list.autoUpdate } });
      await loadLists();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Could not update', detail: extractApiError(e, 'Please try again.'), life: 5000 });
    } finally {
      busyId.value = null;
    }
  }

  const editingList = computed(() => lists.value.find((l) => l.id === editingId.value) ?? null);

  function loadDefinition(list: FrequencyListDto) {
    const def = list.definition;
    editingId.value = list.id;
    editingOriginalName.value = list.name;
    name.value = list.name;
    mode.value = list.mode === 'handpicked' ? 'handpicked' : 'filters';
    mediaTypes.value = [...(def?.mediaTypes ?? [])];
    yearFrom.value = def?.yearFrom ?? null;
    yearTo.value = def?.yearTo ?? null;
    difficultyRange.value = [def?.difficultyMin ?? 0, def?.difficultyMax ?? 5];

    for (const k of Object.keys(genreStates)) genreStates[Number(k)] = 'neutral';
    for (const k of Object.keys(tagStates)) tagStates[Number(k)] = 'neutral';
    for (const id of def?.genresInclude ?? []) genreStates[id] = 'include';
    for (const id of def?.genresExclude ?? []) genreStates[id] = 'exclude';
    for (const id of def?.tagsInclude ?? []) tagStates[id] = 'include';
    for (const id of def?.tagsExclude ?? []) tagStates[id] = 'exclude';

    pickedDecks.value = [...(list.pickedDecks ?? [])];
    saveList.value = list.isSaved;
    autoUpdate.value = list.autoUpdate;
    previewCount.value = null;
    previewSample.value = [];
    runPreview();

    nextTick(() => builderEl.value?.scrollIntoView({ behavior: 'smooth', block: 'start' }));
  }

  function submit() {
    if (editingId.value == null) {
      generate();
      return;
    }
    const target = editingList.value;
    if (!target?.publicSlug) {
      applyEdit();
      return;
    }
    confirm.require({
      message: `"${target.name}" has a share link. Everyone using it will get the update list the next time they update their dictionary.`,
      header: 'Save changes to a shared list',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Save changes',
      rejectLabel: 'Cancel',
      accept: applyEdit,
    });
  }

  async function applyEdit() {
    const id = editingId.value;
    if (id == null || !canGenerate.value) return;
    creating.value = true;
    try {
      await $api(`frequency-lists/${id}`, {
        method: 'PATCH',
        body: { name: name.value.trim(), mode: mode.value, definition: buildDefinition() },
      });
      toast.add({ severity: 'success', summary: 'Changes saved', detail: 'Your list is being rebuilt.', life: 4000 });
      resetBuilder();
      await loadLists();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Could not save changes', detail: extractApiError(e, 'Please try again.'), life: 6000 });
    } finally {
      creating.value = false;
    }
  }

  const shareVisible = ref(false);
  const shareUrl = ref('');

  async function openShare(list: FrequencyListDto) {
    try {
      let slug = list.publicSlug;
      if (!slug) {
        // Saved lists minted before slug-at-save; the share endpoint mints one on demand.
        busyId.value = list.id;
        const res = await $api<{ slug: string }>(`frequency-lists/${list.id}/share`, { method: 'POST' });
        slug = res.slug;
        await loadLists();
      }
      shareUrl.value = `${config.public.baseURL.replace(/\/+$/, '')}/frequency-lists/shared/${slug}`;
      shareVisible.value = true;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Could not get share link', detail: extractApiError(e, 'Please try again.'), life: 5000 });
    } finally {
      busyId.value = null;
    }
  }

  async function copyShareUrl() {
    try {
      await navigator.clipboard.writeText(shareUrl.value);
      toast.add({ severity: 'success', summary: 'Link copied', life: 2000 });
    } catch {
      toast.add({ severity: 'warn', summary: 'Could not access clipboard', detail: 'Select the link and copy it manually.', life: 4000 });
    }
  }

  const studyListId = ref<number | null>(null);
  const showStudyDialog = ref(false);

  function study(list: FrequencyListDto) {
    studyListId.value = list.id;
    showStudyDialog.value = true;
  }

  function confirmDelete(list: FrequencyListDto) {
    confirm.require({
      message: `Delete "${list.name}"? This removes the generated files too.`,
      header: 'Delete list',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete',
      rejectLabel: 'Cancel',
      acceptClass: 'p-button-danger',
      accept: async () => {
        try {
          await $api(`frequency-lists/${list.id}`, { method: 'DELETE' });
          toast.add({ severity: 'info', summary: 'List deleted', life: 3000 });
          await loadLists();
        } catch (e) {
          toast.add({ severity: 'error', summary: 'Delete failed', detail: extractApiError(e, 'Please try again.'), life: 5000 });
        }
      },
    });
  }

  function statusSeverity(status: string): string {
    switch (status) {
      case 'ready':
        return 'success';
      case 'failed':
        return 'danger';
      case 'generating':
        return 'info';
      case 'expired':
        return 'secondary';
      default:
        return 'warn';
    }
  }

  onMounted(() => {
    watch(
      isPlus,
      (plus) => {
        if (!plus) return;
        loadLists();
        runPreview(); // baseline facet counts for the whole catalogue
      },
      { immediate: true }
    );
  });
  onBeforeUnmount(() => {
    if (pollTimer) clearInterval(pollTimer);
  });
</script>

<template>
  <div class="max-w-5xl mx-auto">
    <div class="flex items-center gap-2 mb-4">
      <h1 class="text-2xl font-semibold">Custom Frequency Lists</h1>
      <JitenPlusBadge />
    </div>

    <!-- Not entitled: upgrade prompt -->
    <Card v-if="!isPlus" class="mb-4">
      <template #content>
        <div class="text-center py-6">
          <Icon name="material-symbols-light:workspace-premium-outline" size="2.5em" class="text-primary mb-2" />
          <p class="mb-4 text-surface-600 dark:text-surface-300">
            Build custom frequency dictionaries from any slice of the catalogue. Filter by media type, genre, tag, year or difficulty, or from a hand-picked set
            of decks. This is a Jiten+ feature.
          </p>
          <Button as="router-link" to="/jiten-plus" label="Learn about Jiten+" severity="primary" />
        </div>
      </template>
    </Card>

    <template v-else>
      <div v-if="isTrial" class="mb-4 rounded-md border border-primary-200 dark:border-primary-800 bg-primary-50 dark:bg-primary-950 p-3 text-sm">
        Generated files are kept for 48 hours — after that a list expires, but its filters stay so you can regenerate it in one click.
        <NuxtLink to="/jiten-plus" class="underline font-medium">Jiten+ Full</NuxtLink>
        keeps lists permanently with auto-update and public share links.
      </div>

      <!-- Builder -->
      <div ref="builderEl" class="scroll-mt-4">
        <Card class="mb-4" :class="editingId !== null ? 'ring-2 ring-primary' : ''">
          <template #title>
            <div class="flex flex-wrap items-center gap-2">
              <template v-if="editingId !== null">
                <Icon name="material-symbols-light:edit-outline" size="1.2em" class="text-primary" />
                <span>
                  Editing
                  <span class="text-primary">{{ editingOriginalName }}</span>
                </span>
              </template>
              <span v-else>Build a list</span>
            </div>
          </template>
          <template #content>
            <div class="flex flex-col gap-4">
              <p v-if="editingId !== null" class="text-sm text-surface-500 dark:text-surface-400 -mt-1">
                Saving rebuilds this list with the new filters but keeps the same share link.
              </p>
              <div class="flex flex-col sm:flex-row gap-3 sm:items-end">
                <div class="flex-1">
                  <label class="block text-sm font-medium mb-1" for="list-name">
                    List name
                    <span class="text-primary">*</span>
                    <span class="text-xs font-normal text-surface-400">(required)</span>
                  </label>
                  <InputText id="list-name" v-model="name" maxlength="100" placeholder="e.g. Slice of Life anime" class="w-full" aria-required="true" />
                </div>
                <SelectButton v-model="mode" :options="modeOptions" option-label="label" option-value="value" :allow-empty="false" />
              </div>

              <!-- Filter mode -->
              <div v-if="mode === 'filters'" class="flex flex-col gap-4">
                <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div>
                    <label class="block text-sm font-medium mb-1">Media types</label>
                    <MultiSelect
                      v-model="mediaTypes"
                      :options="mediaTypeOptions"
                      option-label="name"
                      option-value="id"
                      display="chip"
                      placeholder="Any"
                      class="w-full"
                    />
                  </div>
                  <div>
                    <label class="block text-sm font-medium mb-1">Release year</label>
                    <div class="flex items-center gap-2 min-w-0">
                      <InputNumber v-model="yearFrom" :use-grouping="false" :min="1900" :max="currentYear" placeholder="From" fluid class="w-full min-w-0" />
                      <span class="text-surface-400 shrink-0">–</span>
                      <InputNumber v-model="yearTo" :use-grouping="false" :min="1900" :max="currentYear" placeholder="To" fluid class="w-full min-w-0" />
                    </div>
                  </div>
                </div>

                <div>
                  <label class="block text-sm font-medium mb-1">Difficulty ({{ difficultyRange[0] }} – {{ difficultyRange[1] }})</label>
                  <Slider v-model="difficultyRange" range :min="0" :max="5" :step="0.5" class="mt-2" />
                </div>

                <div class="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div>
                    <label class="block text-sm font-medium mb-1">
                      Genres
                      <span class="text-xs text-surface-400">(click to include / exclude)</span>
                    </label>
                    <InputText v-model="genreSearch" placeholder="Search genres" class="w-full mb-2" />
                    <div class="flex flex-wrap gap-2 max-h-40 overflow-y-auto">
                      <TriStateTag
                        v-for="g in filteredGenres"
                        :key="g.value"
                        :label="genreLabel(g.value, g.label)"
                        :state="genreStates[g.value] ?? 'neutral'"
                        @update:state="(s) => (genreStates[g.value] = s)"
                      />
                    </div>
                  </div>
                  <div>
                    <label class="block text-sm font-medium mb-1">
                      Tags
                      <span class="text-xs text-surface-400">(click to include / exclude)</span>
                    </label>
                    <InputText v-model="tagSearch" placeholder="Search tags" class="w-full mb-2" />
                    <div class="flex flex-wrap gap-2 max-h-40 overflow-y-auto">
                      <TriStateTag
                        v-for="t in filteredTags"
                        :key="t.tagId"
                        :label="tagLabel(t.tagId, t.name)"
                        :state="tagStates[t.tagId] ?? 'neutral'"
                        @update:state="(s) => (tagStates[t.tagId] = s)"
                      />
                    </div>
                  </div>
                </div>
              </div>

              <!-- Hand-picked mode -->
              <div v-else class="flex flex-col gap-3">
                <div>
                  <label class="block text-sm font-medium mb-1">Search decks</label>
                  <AutoComplete
                    v-model="selectedDeck"
                    :suggestions="deckSuggestions"
                    :option-label="localiseTitle"
                    dropdown
                    placeholder="Type a title…"
                    class="w-full"
                    @complete="onDeckComplete"
                    @item-select="onDeckSelect"
                  >
                    <template #option="{ option }">
                      <div class="flex items-center gap-2">
                        <img
                          :src="coverUrl(option.coverName)"
                          alt=""
                          class="w-6 h-8 object-cover rounded"
                          @error="(e) => ((e.target as HTMLImageElement).src = '/img/nocover.jpg')"
                        />
                        <span>{{ localiseTitle(option) }}</span>
                        <Tag :value="getMediaTypeText(option.mediaType)" severity="secondary" class="ml-auto" />
                      </div>
                    </template>
                  </AutoComplete>
                  <Button label="Add from my media list" icon="pi pi-list" size="small" outlined class="mt-2" @click="mediaListPickerVisible = true" />
                </div>
                <Accordion v-if="pickedDecks.length" value="picked">
                  <AccordionPanel value="picked">
                    <AccordionHeader>Selected decks ({{ pickedDecks.length }})</AccordionHeader>
                    <AccordionContent>
                      <div class="flex flex-col gap-2 pt-1">
                        <div
                          v-for="d in pickedDecks"
                          :key="d.deckId"
                          class="flex items-center gap-3 min-w-0 border border-surface-200 dark:border-surface-700 rounded-lg p-2"
                        >
                          <img
                            :src="coverUrl(d.coverName)"
                            alt=""
                            class="h-16 w-11 object-cover rounded shrink-0"
                            @error="(e) => ((e.target as HTMLImageElement).src = '/img/nocover.jpg')"
                          />
                          <div class="flex items-center gap-2 min-w-0 flex-1">
                            <span class="font-medium truncate" :title="localiseTitle(d)">{{ localiseTitle(d) }}</span>
                            <Tag :value="getMediaTypeText(d.mediaType)" severity="secondary" class="shrink-0" />
                          </div>
                          <Tooltip content="Remove">
                            <Button icon="pi pi-times" text rounded size="small" severity="danger" class="shrink-0" @click="removePickedDeck(d.deckId)" />
                          </Tooltip>
                        </div>
                      </div>
                    </AccordionContent>
                  </AccordionPanel>
                </Accordion>
                <p class="text-xs text-surface-400">Pick at least {{ minDecks }} decks.</p>
              </div>

              <!-- Preview + generate -->
              <div class="flex flex-col sm:flex-row sm:items-center gap-3 border-t border-surface-200 dark:border-surface-700 pt-3">
                <div class="flex items-center gap-2 text-sm">
                  <template v-if="previewCount !== null">
                    <span :class="{ 'opacity-50 transition-opacity': previewLoading }">
                      <b :class="previewCount < minDecks ? 'text-red-500' : 'text-primary'">{{ previewCount.toLocaleString() }}</b>
                      decks match
                    </span>
                    <ProgressSpinner v-if="previewLoading" style="width: 1rem; height: 1rem" stroke-width="6" />
                  </template>
                  <ProgressSpinner v-else-if="previewLoading" style="width: 1rem; height: 1rem" stroke-width="6" />
                  <span v-else class="text-surface-400">Adjust filters to preview</span>
                </div>

                <div class="flex flex-wrap items-center gap-3 sm:ml-auto">
                  <template v-if="editingId === null">
                    <div class="flex items-center gap-2" :title="isFull ? '' : 'Requires Jiten+ Full'">
                      <Checkbox v-model="saveList" input-id="save-list" binary :disabled="!isFull" />
                      <label for="save-list" class="text-sm" :class="{ 'text-surface-400': !isFull }">Keep saved</label>
                      <JitenPlusBadge v-if="!isFull" tier="full" />
                    </div>
                    <div v-if="saveList && isFull" class="flex items-center gap-2">
                      <Checkbox v-model="autoUpdate" input-id="auto-update" binary />
                      <label for="auto-update" class="text-sm">Auto-update</label>
                    </div>
                  </template>
                  <Button v-if="editingId !== null" label="Cancel" outlined severity="secondary" @click="resetBuilder" />
                  <Button
                    :label="editingId !== null ? 'Save changes' : 'Generate'"
                    :icon="editingId !== null ? 'pi pi-check' : 'pi pi-bolt'"
                    :loading="creating"
                    :disabled="!canGenerate"
                    @click="submit"
                  />
                </div>
              </div>
              <p v-if="!hasFilters" class="text-xs text-surface-400 -mt-2">
                {{ mode === 'handpicked' ? `Pick at least ${minDecks} decks to generate a list.` : 'Select at least one filter to generate a list.' }}
              </p>
              <p v-else-if="previewCount !== null && previewCount < minDecks" class="text-xs text-red-500 -mt-2">
                A list needs at least {{ minDecks }} matching decks.
              </p>
              <div v-if="previewSample.length" class="text-xs text-surface-400">e.g. {{ previewSample.slice(0, 5).map(localiseTitle).join(', ') }}</div>
            </div>
          </template>
        </Card>
      </div>

      <!-- Existing lists -->
      <Card>
        <template #title>
          <div class="flex items-center gap-2">
            Your lists
            <Button icon="pi pi-refresh" text rounded size="small" :loading="listsLoading" @click="loadLists" />
          </div>
        </template>
        <template #content>
          <div v-if="isFull && lists.length" class="flex items-center gap-1 mb-3 text-xs">
            <Tooltip
              content="Saved lists are kept permanently. Auto-update refreshes a saved list whenever site-wide frequencies are recomputed. These limits are subject to change."
              placement="bottom"
            >
              <span class="flex items-center gap-3">
                <span :class="usageColor(savedListsUsage, maxSavedLists)">
                  <span class="font-semibold tabular-nums">{{ savedListsUsage }}</span>
                  <span class="opacity-60">/{{ maxSavedLists }} saved lists</span>
                </span>
                <span :class="usageColor(autoUpdateUsage, maxAutoUpdateLists)">
                  <span class="font-semibold tabular-nums">{{ autoUpdateUsage }}</span>
                  <span class="opacity-60">/{{ maxAutoUpdateLists }} auto-updating</span>
                </span>
              </span>
            </Tooltip>
          </div>
          <div v-if="!lists.length" class="text-surface-400 text-sm py-4">You haven't generated any lists yet.</div>
          <template v-else>
            <!-- Mobile: stacked cards -->
            <div class="flex flex-col gap-3 sm:hidden">
              <div v-for="list in lists" :key="list.id" class="border border-surface-200 dark:border-surface-700 rounded-lg p-3 flex flex-col gap-2">
                <div class="flex items-start justify-between gap-2">
                  <span class="font-medium break-words min-w-0">{{ list.name }}</span>
                  <Tag :value="list.status" :severity="statusSeverity(list.status)" class="shrink-0" />
                </div>
                <div class="flex flex-wrap gap-x-3 gap-y-1 text-xs text-surface-500 dark:text-surface-400">
                  <span>{{ list.deckCount.toLocaleString() }} decks</span>
                  <span>{{ list.wordCount.toLocaleString() }} words</span>
                  <span v-if="list.status === 'expired'">regenerate to rebuild</span>
                  <span v-else-if="!list.isSaved">temporary</span>
                </div>
                <div class="flex flex-wrap gap-2 pt-1">
                  <Button label="Yomitan" icon="pi pi-download" size="small" outlined :disabled="list.status !== 'ready'" @click="download(list, 'zip')" />
                  <Button label="CSV" icon="pi pi-file" size="small" outlined :disabled="list.status !== 'ready'" @click="download(list, 'csv')" />
                  <Button label="Regenerate" icon="pi pi-sync" size="small" outlined :loading="busyId === list.id" @click="regenerate(list)" />
                  <Button
                    label="Edit"
                    icon="pi pi-pencil"
                    size="small"
                    outlined
                    :severity="editingId === list.id ? 'success' : undefined"
                    @click="loadDefinition(list)"
                  />
                  <Button v-if="!list.isSaved && isFull" label="Keep saved" icon="pi pi-bookmark" size="small" outlined @click="save(list)" />
                  <Button v-if="list.isSaved" label="Study" icon="pi pi-graduation-cap" size="small" outlined @click="study(list)" />
                  <template v-if="list.isSaved && isFull">
                    <Button
                      :label="list.autoUpdate ? 'Auto-update on' : 'Auto-update off'"
                      icon="pi pi-clock"
                      size="small"
                      outlined
                      :severity="list.autoUpdate ? 'success' : 'secondary'"
                      @click="toggleAutoUpdate(list)"
                    />
                    <Button label="Share" icon="pi pi-share-alt" size="small" outlined :loading="busyId === list.id" @click="openShare(list)" />
                  </template>
                  <Button label="Delete" icon="pi pi-trash" size="small" outlined severity="danger" @click="confirmDelete(list)" />
                </div>
              </div>
            </div>
            <!-- Desktop: table -->
            <div class="hidden sm:block">
              <DataTable :value="lists" class="p-datatable-sm" responsive-layout="scroll">
                <Column field="name" header="Name" />
                <Column header="Status">
                  <template #body="{ data }">
                    <Tag :value="data.status" :severity="statusSeverity(data.status)" />
                    <span v-if="data.status === 'expired'" class="ml-2 text-xs text-surface-400">regenerate to rebuild</span>
                    <span v-else-if="!data.isSaved" class="ml-2 text-xs text-surface-400">temporary</span>
                  </template>
                </Column>
                <Column header="Decks">
                  <template #body="{ data }">{{ data.deckCount.toLocaleString() }}</template>
                </Column>
                <Column header="Words">
                  <template #body="{ data }">{{ data.wordCount.toLocaleString() }}</template>
                </Column>
                <Column header="Actions">
                  <template #body="{ data }">
                    <div class="flex flex-wrap items-center gap-1">
                      <Tooltip content="Download Yomitan">
                        <Button icon="pi pi-download" text size="small" :disabled="data.status !== 'ready'" @click="download(data, 'zip')" />
                      </Tooltip>
                      <Tooltip content="Download CSV">
                        <Button icon="pi pi-file" text size="small" :disabled="data.status !== 'ready'" @click="download(data, 'csv')" />
                      </Tooltip>
                      <Tooltip content="Regenerate">
                        <Button icon="pi pi-sync" text size="small" :loading="busyId === data.id" @click="regenerate(data)" />
                      </Tooltip>
                      <Tooltip content="Edit name and filters">
                        <Button icon="pi pi-pencil" text size="small" :severity="editingId === data.id ? 'success' : undefined" @click="loadDefinition(data)" />
                      </Tooltip>
                      <Tooltip v-if="!data.isSaved && isFull" content="Keep saved">
                        <Button icon="pi pi-bookmark" text size="small" @click="save(data)" />
                      </Tooltip>
                      <Tooltip v-if="data.isSaved" content="Study this list">
                        <Button icon="pi pi-graduation-cap" text size="small" @click="study(data)" />
                      </Tooltip>
                      <template v-if="data.isSaved && isFull">
                        <Tooltip :content="data.autoUpdate ? 'Auto-update on' : 'Auto-update off'">
                          <Button icon="pi pi-clock" text size="small" :severity="data.autoUpdate ? 'success' : 'secondary'" @click="toggleAutoUpdate(data)" />
                        </Tooltip>
                        <Tooltip content="Share link">
                          <Button icon="pi pi-share-alt" text size="small" :loading="busyId === data.id" @click="openShare(data)" />
                        </Tooltip>
                      </template>
                      <Tooltip content="Delete">
                        <Button icon="pi pi-trash" text size="small" severity="danger" @click="confirmDelete(data)" />
                      </Tooltip>
                    </div>
                  </template>
                </Column>
              </DataTable>
            </div>
          </template>
        </template>
      </Card>

      <Dialog v-model:visible="shareVisible" modal header="Share link" :draggable="false" class="w-[28rem] max-w-[calc(100vw-2rem)]">
        <p class="text-sm text-surface-500 dark:text-surface-400 mb-3">
          Anyone with this link can download the list as a Yomitan dictionary. Append
          <code>?format=csv</code>
          for the CSV version.
        </p>
        <div class="flex items-center gap-2">
          <InputText :model-value="shareUrl" readonly class="w-full text-sm" @focus="(e) => (e.target as HTMLInputElement).select()" />
          <Tooltip content="Copy">
            <Button icon="pi pi-copy" outlined aria-label="Copy link" @click="copyShareUrl" />
          </Tooltip>
        </div>
      </Dialog>

      <MediaListDeckPickerDialog v-model:visible="mediaListPickerVisible" :picked-ids="pickedDeckIds" @add="onMediaListAdd" />

      <SrsAddDeckDialog v-if="studyListId !== null" v-model:visible="showStudyDialog" :preselected-frequency-list-id="studyListId" />
    </template>
  </div>
</template>
