<script setup lang="ts">
  import { type Deck, type MediaSuggestion, type StudyDeckDto, DeckDownloadType, DeckOrder, StudyDeckType, MediaType } from '~/types';
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { debounce } from 'perfect-debounce';

  const props = defineProps<{
    visible: boolean;
    preselectedDeck?: Deck;
    editDeck?: StudyDeckDto;
  }>();

  const emit = defineEmits(['update:visible']);
  const { $api } = useNuxtApp();
  const srsStore = useSrsStore();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const router = useRouter();

  const localVisible = ref(props.visible);
  watch(() => props.visible, (v) => { localVisible.value = v; });
  watch(localVisible, (v) => { emit('update:visible', v); });

  const isEditMode = computed(() => !!props.editDeck);

  type Step = 'type' | 'search' | 'filters' | 'global' | 'static' | 'static-import-preview';

  function initialStep(): Step {
    if (props.editDeck) {
      if (props.editDeck.deckType === StudyDeckType.GlobalDynamic) return 'global';
      if (props.editDeck.deckType === StudyDeckType.StaticWordList) return 'static';
      return 'filters';
    }
    if (props.preselectedDeck) return 'filters';
    return 'type';
  }

  const step = ref<Step>(initialStep());
  const selectedDeck = ref<{ deckId: number; title: string; coverName?: string } | null>(
    props.editDeck?.deckType === StudyDeckType.MediaDeck
      ? { deckId: props.editDeck.deckId!, title: props.editDeck.title, coverName: props.editDeck.coverName }
      : props.preselectedDeck
        ? { deckId: props.preselectedDeck.deckId, title: props.preselectedDeck.originalTitle, coverName: props.preselectedDeck.coverName ?? undefined }
        : null,
  );

  watch(() => props.preselectedDeck, (deck) => {
    if (deck) {
      selectedDeck.value = { deckId: deck.deckId, title: deck.originalTitle, coverName: deck.coverName ?? undefined };
      step.value = 'filters';
    }
  });

  function modeFromDownloadType(dt: number): Mode {
    if (dt === DeckDownloadType.TargetCoverage) return 'target';
    if (dt === DeckDownloadType.OccurrenceCount) return 'occurrence';
    return 'manual';
  }

  watch(() => props.editDeck, (deck) => {
    if (!deck) return;
    if (deck.deckType === StudyDeckType.MediaDeck) {
      selectedDeck.value = { deckId: deck.deckId!, title: deck.title, coverName: deck.coverName };
      downloadMode.value = modeFromDownloadType(deck.downloadType);
      downloadType.value = [DeckDownloadType.Full, DeckDownloadType.TopGlobalFrequency, DeckDownloadType.TopDeckFrequency, DeckDownloadType.TopChronological].includes(deck.downloadType)
        ? deck.downloadType
        : DeckDownloadType.TopGlobalFrequency;
      deckOrder.value = deck.order;
      minFrequency.value = deck.minFrequency;
      maxFrequency.value = deck.maxFrequency;
      targetPercentage.value = deck.targetPercentage ?? 80;
      startFromKnown.value = deck.startFromKnown ?? false;
      if (deck.minOccurrences) {
        occurrenceFilterType.value = 'gte';
        occurrenceThreshold.value = deck.minOccurrences;
      } else if (deck.maxOccurrences) {
        occurrenceFilterType.value = 'lte';
        occurrenceThreshold.value = deck.maxOccurrences;
      }
      mediaPosFilter.value = deck.posFilter ? JSON.parse(deck.posFilter) : [];
      excludeKana.value = deck.excludeKana;
      step.value = 'filters';
    } else if (deck.deckType === StudyDeckType.GlobalDynamic) {
      globalName.value = deck.name;
      globalDescription.value = deck.description ?? '';
      globalOrder.value = deck.order;
      globalMinFreq.value = deck.minGlobalFrequency;
      globalMaxFreq.value = deck.maxGlobalFrequency;
      globalPosFilter.value = deck.posFilter ? JSON.parse(deck.posFilter) : [];
      excludeKana.value = deck.excludeKana;
      step.value = 'global';
    } else if (deck.deckType === StudyDeckType.StaticWordList) {
      staticName.value = deck.name;
      staticDescription.value = deck.description ?? '';
      staticOrder.value = deck.order;
      step.value = 'static';
    }
  });

  // Step: Search (media)
  const searchQuery = ref('');
  const searchResults = ref<MediaSuggestion[]>([]);
  const searching = ref(false);

  const debouncedSearch = debounce(async (query: string) => {
    if (query.length < 2) { searchResults.value = []; return; }
    searching.value = true;
    try {
      const response = await $api<{ suggestions: MediaSuggestion[] }>('media-deck/search-suggestions', {
        query: { query, limit: 10 },
      });
      searchResults.value = response.suggestions ?? [];
    } catch {
      searchResults.value = [];
    } finally {
      searching.value = false;
    }
  }, 300);

  watch(searchQuery, (q) => debouncedSearch(q));

  function selectMediaDeck(suggestion: MediaSuggestion) {
    selectedDeck.value = { deckId: suggestion.deckId, title: suggestion.originalTitle, coverName: suggestion.coverName };
    step.value = 'filters';
  }

  // Media filters
  type Mode = 'manual' | 'target' | 'occurrence';
  const downloadMode = ref<Mode>('manual');
  const downloadType = ref(DeckDownloadType.TopGlobalFrequency);
  const deckOrder = ref(DeckOrder.DeckFrequency);
  const minFrequency = ref(0);
  const maxFrequency = ref(30000);
  const targetPercentage = ref(80);
  const startFromKnown = ref(false);
  const occurrenceFilterType = ref<'gte' | 'lte'>('gte');
  const occurrenceThreshold = ref(10);
  const mediaPosFilter = ref<string[]>([]);
  const excludeKana = ref(false);
  const adding = ref(false);
  const previewCount = ref<{ total: number; unlearned: number } | null>(null);
  const isCountLoading = ref(false);

  watch(downloadMode, (mode) => {
    if (isEditMode.value) return;
    deckOrder.value = mode === 'manual' ? DeckOrder.GlobalFrequency : DeckOrder.DeckFrequency;
  });

  const computedDownloadType = computed(() => {
    if (downloadMode.value === 'target') return DeckDownloadType.TargetCoverage;
    if (downloadMode.value === 'occurrence') return DeckDownloadType.OccurrenceCount;
    return downloadType.value;
  });

  let countRequestId = 0;
  const fetchPreviewCount = async () => {
    if (!selectedDeck.value || step.value !== 'filters') return;
    const reqId = ++countRequestId;
    isCountLoading.value = true;
    try {
      const response = await $api<{ total: number; unlearned: number }>('srs/study-decks/preview-count', {
        method: 'POST',
        body: {
          deckId: selectedDeck.value.deckId,
          downloadType: computedDownloadType.value,
          order: deckOrder.value,
          minFrequency: minFrequency.value,
          maxFrequency: maxFrequency.value,
          targetPercentage: downloadMode.value === 'target' ? targetPercentage.value : undefined,
          startFromKnown: downloadMode.value === 'target' ? startFromKnown.value : undefined,
          minOccurrences: downloadMode.value === 'occurrence' && occurrenceFilterType.value === 'gte' ? occurrenceThreshold.value : undefined,
          maxOccurrences: downloadMode.value === 'occurrence' && occurrenceFilterType.value === 'lte' ? occurrenceThreshold.value : undefined,
          posFilter: mediaPosFilter.value.length > 0 ? JSON.stringify(mediaPosFilter.value) : undefined,
          excludeKana: excludeKana.value,
        },
      });
      if (reqId === countRequestId && response && typeof response.total === 'number') {
        previewCount.value = response;
      }
    } catch {
      if (reqId === countRequestId) previewCount.value = null;
    } finally {
      if (reqId === countRequestId) isCountLoading.value = false;
    }
  };
  const fetchPreviewCountDebounced = debounce(fetchPreviewCount, 500);

  watch(
    [
      step, () => selectedDeck.value?.deckId,
      computedDownloadType, downloadType, deckOrder,
      minFrequency, maxFrequency, targetPercentage, startFromKnown,
      occurrenceFilterType, occurrenceThreshold, mediaPosFilter, excludeKana,
    ],
    () => {
      if (step.value !== 'filters' || !selectedDeck.value) {
        previewCount.value = null;
        return;
      }
      fetchPreviewCountDebounced();
    },
  );

  // Global Dynamic fields
  const globalName = ref('');
  const globalDescription = ref('');
  const globalOrder = ref(DeckOrder.GlobalFrequency);
  const globalMinFreq = ref<number | undefined>(1);
  const globalMaxFreq = ref<number | undefined>(10000);
  const globalPosFilter = ref<string[]>([]);

  const posOptions = [
    { label: 'Ichidan verb', value: 'v1' },
    { label: 'Godan verb', value: 'v5' },
    { label: 'Suru verb', value: 'vs' },
    { label: 'Transitive verb', value: 'vt' },
    { label: 'Intransitive verb', value: 'vi' },
    { label: 'i-adjective', value: 'adj-i' },
    { label: 'na-adjective', value: 'adj-na' },
    { label: 'Noun', value: 'n' },
    { label: 'Adverb', value: 'adv' },
    { label: 'Expression', value: 'exp' },
    { label: 'Particle', value: 'prt' },
    { label: 'Auxiliary', value: 'aux' },
    { label: 'Pronoun', value: 'pn' },
    { label: 'Conjunction', value: 'conj' },
    { label: 'Interjection', value: 'int' },
    { label: 'Counter', value: 'ctr' },
    { label: 'Suffix', value: 'suf' },
    { label: 'Prefix', value: 'pref' },
  ];

  // Static Word List fields
  const staticName = ref('');
  const staticDescription = ref('');
  const staticOrder = ref(DeckOrder.ImportOrder);

  // Static import fields
  const importParseFullText = ref(false);
  const importFile = ref<File | null>(null);
  const importUploading = ref(false);
  const importCommitting = ref(false);
  const importPreviewToken = ref('');
  const importMatched = ref<{ wordId: number; readingIndex: number; text: string; reading: string }[]>([]);
  const importUnmatched = ref<string[]>([]);
  const importTotalLines = ref(0);
  const importExcludedWordIds = ref(new Set<number>());

  const importIncludedCount = computed(() => importMatched.value.length - importExcludedWordIds.value.size);

  function toggleImportExclude(wordId: number) {
    const s = new Set(importExcludedWordIds.value);
    if (s.has(wordId)) s.delete(wordId);
    else s.add(wordId);
    importExcludedWordIds.value = s;
  }

  const importIsFullTextOnly = computed(() => importFile.value ? isFullTextOnlyFile(importFile.value.name) : false);

  function onImportFileSelect(event: any) {
    const files = event.target?.files || event.files;
    if (files && files.length > 0) {
      importFile.value = files[0];
      if (importIsFullTextOnly.value) importParseFullText.value = true;
    }
  }

  async function uploadAndPreview() {
    if (!importFile.value) return;
    importUploading.value = true;
    try {
      const useFullText = importParseFullText.value || importIsFullTextOnly.value;
      const result = await srsStore.importPreview(importFile.value, useFullText);
      importPreviewToken.value = result.previewToken;
      importMatched.value = result.matched;
      importUnmatched.value = result.unmatched;
      importTotalLines.value = result.totalLines;
      if (!staticName.value) {
        staticName.value = importFile.value.name.replace(/\.[^.]+$/, '');
      }
      step.value = 'static-import-preview';
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Upload failed', detail: String(error?.data || error?.message || 'Unknown error'), life: 5000 });
    } finally {
      importUploading.value = false;
    }
  }

  async function commitImport() {
    if (!staticName.value.trim()) {
      toast.add({ severity: 'warn', summary: 'Name required', life: 3000 });
      return;
    }
    importCommitting.value = true;
    try {
      const excluded = importExcludedWordIds.value.size > 0 ? [...importExcludedWordIds.value] : undefined;
      const result = await srsStore.importCommit(importPreviewToken.value, staticName.value, staticDescription.value || undefined, excluded);
      toast.add({ severity: 'success', summary: `Imported ${importIncludedCount.value} words`, life: 3000 });
      localVisible.value = false;
      const createdId = result.userStudyDeckId;
      resetForm();
      router.push(`/srs/decks/${createdId}/vocabulary`);
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Import failed', detail: String(error?.data || error?.message || 'Unknown error'), life: 5000 });
    } finally {
      importCommitting.value = false;
    }
  }

  const dialogHeader = computed(() => {
    if (isEditMode.value) return 'Edit Study Deck';
    if (step.value === 'type') return 'Add Study Deck';
    if (step.value === 'search') return 'Add Media Deck';
    if (step.value === 'filters') return 'Configure Media Deck';
    if (step.value === 'global') return isEditMode.value ? 'Edit Global Frequency Deck' : 'Add Global Frequency Deck';
    if (step.value === 'static-import-preview') return 'Import Preview';
    if (step.value === 'static') return isEditMode.value ? 'Edit Word List' : 'Create Word List';
    return 'Add Study Deck';
  });

  async function addDeck() {
    adding.value = true;
    try {
      if (step.value === 'filters' && selectedDeck.value) {
        const filterPayload = {
          deckType: StudyDeckType.MediaDeck,
          downloadType: computedDownloadType.value,
          order: deckOrder.value,
          minFrequency: minFrequency.value,
          maxFrequency: maxFrequency.value,
          targetPercentage: downloadMode.value === 'target' ? targetPercentage.value : undefined,
          startFromKnown: downloadMode.value === 'target' ? startFromKnown.value : undefined,
          minOccurrences: downloadMode.value === 'occurrence' && occurrenceFilterType.value === 'gte' ? occurrenceThreshold.value : undefined,
          maxOccurrences: downloadMode.value === 'occurrence' && occurrenceFilterType.value === 'lte' ? occurrenceThreshold.value : undefined,
          posFilter: mediaPosFilter.value.length > 0 ? JSON.stringify(mediaPosFilter.value) : undefined,
          excludeKana: excludeKana.value,
        };

        if (isEditMode.value && props.editDeck) {
          await srsStore.updateStudyDeck(props.editDeck.userStudyDeckId, filterPayload);
          toast.add({ severity: 'success', summary: 'Deck filters updated', life: 3000 });
        } else {
          await srsStore.addStudyDeck({ deckId: selectedDeck.value.deckId, ...filterPayload });
          toast.add({ severity: 'success', summary: 'Deck added to study list', life: 3000 });
        }
      } else if (step.value === 'global') {
        const payload = {
          deckType: StudyDeckType.GlobalDynamic,
          name: globalName.value,
          description: globalDescription.value || undefined,
          downloadType: DeckDownloadType.Full,
          order: globalOrder.value,
          minFrequency: 0,
          maxFrequency: 0,
          minGlobalFrequency: globalMinFreq.value,
          maxGlobalFrequency: globalMaxFreq.value,
          posFilter: globalPosFilter.value.length > 0 ? JSON.stringify(globalPosFilter.value) : undefined,
          excludeKana: excludeKana.value,
        };

        if (isEditMode.value && props.editDeck) {
          await srsStore.updateStudyDeck(props.editDeck.userStudyDeckId, payload);
          toast.add({ severity: 'success', summary: 'Deck updated', life: 3000 });
        } else {
          await srsStore.addStudyDeck(payload);
          toast.add({ severity: 'success', summary: 'Global frequency deck created', life: 3000 });
        }
      } else if (step.value === 'static') {
        const payload = {
          deckType: StudyDeckType.StaticWordList,
          name: staticName.value,
          description: staticDescription.value || undefined,
          downloadType: DeckDownloadType.Full,
          order: staticOrder.value,
          minFrequency: 0,
          maxFrequency: 0,
          excludeKana: false,
        };

        if (isEditMode.value && props.editDeck) {
          await srsStore.updateStudyDeck(props.editDeck.userStudyDeckId, payload);
          toast.add({ severity: 'success', summary: 'Word list updated', life: 3000 });
        } else {
          const result = await srsStore.addStudyDeck(payload);
          toast.add({ severity: 'success', summary: 'Word list created', life: 3000 });
          localVisible.value = false;
          const createdId = result.userStudyDeckId;
          resetForm();
          router.push(`/srs/decks/${createdId}/vocabulary`);
          return;
        }
      }
      localVisible.value = false;
      resetForm();
    } catch (error: any) {
      const message = error?.data?.message || error?.data || 'Failed to save deck';
      toast.add({ severity: 'error', summary: 'Error', detail: String(message), life: 5000 });
    } finally {
      adding.value = false;
    }
  }

  function resetForm() {
    step.value = initialStep();
    searchQuery.value = '';
    searchResults.value = [];
    if (!props.preselectedDeck && !props.editDeck) selectedDeck.value = null;
    downloadMode.value = 'manual';
    downloadType.value = DeckDownloadType.TopGlobalFrequency;
    deckOrder.value = DeckOrder.DeckFrequency;
    minFrequency.value = 0;
    maxFrequency.value = 30000;
    targetPercentage.value = 80;
    startFromKnown.value = false;
    occurrenceFilterType.value = 'gte';
    occurrenceThreshold.value = 10;
    mediaPosFilter.value = [];
    excludeKana.value = false;
    previewCount.value = null;
    globalName.value = '';
    globalDescription.value = '';
    globalOrder.value = DeckOrder.GlobalFrequency;
    globalMinFreq.value = 1;
    globalMaxFreq.value = 10000;
    globalPosFilter.value = [];
    staticName.value = '';
    staticDescription.value = '';
    staticOrder.value = DeckOrder.ImportOrder;
    importFile.value = null;
    importParseFullText.value = false;
    importPreviewToken.value = '';
    importMatched.value = [];
    importUnmatched.value = [];
    importTotalLines.value = 0;
    importExcludedWordIds.value = new Set();
  }

  function goBack() {
    if (props.preselectedDeck) {
      localVisible.value = false;
    } else if (step.value === 'filters') {
      step.value = 'search';
      selectedDeck.value = null;
    } else if (step.value === 'static-import-preview') {
      step.value = 'static';
    } else {
      step.value = 'type';
    }
  }

  const downloadTypeOptions = [
    { label: 'Full', value: DeckDownloadType.Full },
    { label: 'Top Global Frequency', value: DeckDownloadType.TopGlobalFrequency },
    { label: 'Top Deck Frequency', value: DeckDownloadType.TopDeckFrequency },
    { label: 'Top Chronological', value: DeckDownloadType.TopChronological },
  ];

  const orderOptions = [
    { label: 'Chronological', value: DeckOrder.Chronological },
    { label: 'Global Frequency', value: DeckOrder.GlobalFrequency },
    { label: 'Deck Frequency', value: DeckOrder.DeckFrequency },
    { label: 'Random', value: DeckOrder.Random },
  ];

  const globalOrderOptions = [
    { label: 'Global Frequency', value: DeckOrder.GlobalFrequency },
    { label: 'Random', value: DeckOrder.Random },
  ];

  const staticOrderOptions = [
    { label: 'Import Order', value: DeckOrder.ImportOrder },
    { label: 'Global Frequency', value: DeckOrder.GlobalFrequency },
    { label: 'Random', value: DeckOrder.Random },
  ];

  const modeOptions = [
    { label: 'Manual Range', value: 'manual' },
    { label: 'Target Coverage', value: 'target' },
    { label: 'Occurrence Count', value: 'occurrence' },
  ];
</script>

<template>
  <Dialog
    v-model:visible="localVisible"
    :header="dialogHeader"
    modal
    :style="{ width: step === 'static-import-preview' ? '600px' : '550px', maxWidth: '95vw', minHeight: '450px' }"
    :pt="{ content: { class: 'p-4' } }"
  >
    <!-- Step: Choose deck type -->
    <div v-if="step === 'type'" class="flex flex-col gap-3">
      <button
        class="flex items-center gap-4 p-4 rounded-lg border border-gray-200 dark:border-gray-700 hover:border-purple-400 dark:hover:border-purple-500 transition-colors text-left cursor-pointer"
        @click="step = 'search'"
      >
        <Icon name="material-symbols:book-2" size="28" class="text-purple-500 shrink-0" />
        <div>
          <div class="font-semibold">Media Deck</div>
          <div class="text-sm text-gray-500">Study vocabulary from anime, novels, games, etc.</div>
        </div>
      </button>
      <button
        class="flex items-center gap-4 p-4 rounded-lg border border-gray-200 dark:border-gray-700 hover:border-blue-400 dark:hover:border-blue-500 transition-colors text-left cursor-pointer"
        @click="step = 'global'"
      >
        <Icon name="material-symbols:language" size="28" class="text-blue-500 shrink-0" />
        <div>
          <div class="font-semibold">Global Frequency</div>
          <div class="text-sm text-gray-500">Study words by overall Japanese frequency rank.</div>
        </div>
      </button>
      <button
        class="flex items-center gap-4 p-4 rounded-lg border border-gray-200 dark:border-gray-700 hover:border-green-400 dark:hover:border-green-500 transition-colors text-left cursor-pointer"
        @click="step = 'static'"
      >
        <Icon name="material-symbols:list-alt" size="28" class="text-green-500 shrink-0" />
        <div>
          <div class="font-semibold">Word List</div>
          <div class="text-sm text-gray-500">Create a custom word list or import from file. Also used for mining with Jiten Reader.</div>
        </div>
      </button>
    </div>

    <!-- Step: Media search -->
    <div v-if="step === 'search'">
      <div class="flex items-center gap-2 mb-4">
        <Button icon="pi pi-arrow-left" severity="secondary" text size="small" @click="goBack" />
        <span class="text-sm text-gray-500">Search for a media deck</span>
      </div>
      <div class="mb-4">
        <InputText v-model="searchQuery" placeholder="Type to search..." class="w-full" autofocus />
      </div>

      <div v-if="searching" class="flex justify-center py-4">
        <ProgressSpinner style="width: 30px; height: 30px" />
      </div>

      <div v-else class="flex flex-col gap-1 max-h-[400px] overflow-y-auto" role="listbox" aria-label="Search results">
        <div
          v-for="result in searchResults"
          :key="result.deckId"
          role="option"
          tabindex="0"
          class="flex items-center gap-3 p-2 rounded cursor-pointer hover:bg-gray-100 dark:hover:bg-gray-700 transition-colors"
          @click="selectMediaDeck(result)"
          @keydown.enter="selectMediaDeck(result)"
        >
          <img
            :src="result.coverName && result.coverName !== 'nocover.jpg' ? result.coverName : '/img/nocover.jpg'"
            :alt="localiseTitle(result)"
            class="w-10 h-14 object-cover rounded shrink-0"
          />
          <div class="flex-1 min-w-0">
            <div class="text-sm font-medium truncate">{{ localiseTitle(result) }}</div>
            <div class="text-xs text-gray-500">{{ getMediaTypeText(result.mediaType) }}</div>
          </div>
        </div>
        <div v-if="searchQuery.length >= 2 && searchResults.length === 0 && !searching" class="text-center text-sm text-gray-500 py-4">
          No results found
        </div>
      </div>
    </div>

    <!-- Step: Media deck filters -->
    <div v-if="step === 'filters' && selectedDeck">
      <div class="flex items-center gap-2 mb-4 pb-3 border-b border-gray-200 dark:border-gray-700">
        <Button v-if="!preselectedDeck && !isEditMode" icon="pi pi-arrow-left" severity="secondary" text size="small" @click="goBack" />
        <span class="font-semibold">{{ selectedDeck.title }}</span>
      </div>

      <div class="mb-4">
        <label class="block text-sm font-medium mb-1">Filter Mode</label>
        <SelectButton v-model="downloadMode" :options="modeOptions" option-label="label" option-value="value" class="w-full" />
      </div>

      <template v-if="downloadMode === 'manual'">
        <div class="mb-3">
          <label class="block text-sm font-medium mb-1">Download Type</label>
          <Select v-model="downloadType" :options="downloadTypeOptions" option-label="label" option-value="value" class="w-full" />
        </div>
        <div v-if="downloadType !== DeckDownloadType.Full" class="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-3">
          <div class="min-w-0">
            <label class="block text-xs mb-1">Min</label>
            <InputNumber v-model="minFrequency" :min="0" class="w-full [&_input]:w-full" />
          </div>
          <div class="min-w-0">
            <label class="block text-xs mb-1">Max</label>
            <InputNumber v-model="maxFrequency" :min="0" class="w-full [&_input]:w-full" />
          </div>
        </div>
      </template>

      <template v-if="downloadMode === 'target'">
        <div class="mb-3">
          <label class="block text-sm font-medium mb-1">Target Coverage: {{ targetPercentage }}%</label>
          <Slider v-model="targetPercentage" :min="1" :max="100" class="w-full" />
        </div>
        <div class="flex items-center gap-2 mb-3">
          <Checkbox v-model="startFromKnown" input-id="srsStartFromKnown" :binary="true" />
          <label for="srsStartFromKnown" class="text-sm cursor-pointer">Start from my current coverage</label>
        </div>
      </template>

      <template v-if="downloadMode === 'occurrence'">
        <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-3">
          <div class="min-w-0">
            <label class="block text-xs mb-1">Filter Type</label>
            <Select
              v-model="occurrenceFilterType"
              :options="[
                { label: 'Over or equal to (≥)', value: 'gte' },
                { label: 'Under or equal to (≤)', value: 'lte' },
              ]"
              option-value="value"
              option-label="label"
              class="w-full"
            />
          </div>
          <div class="min-w-0">
            <label class="block text-xs mb-1">Threshold</label>
            <InputNumber v-model="occurrenceThreshold" :min="1" :useGrouping="false" class="w-full [&_input]:w-full" />
          </div>
        </div>
      </template>

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Only include parts of speech <span class="text-gray-400">(optional)</span></label>
        <MultiSelect
          v-model="mediaPosFilter"
          :options="posOptions"
          option-label="label"
          option-value="value"
          placeholder="All (no filter)"
          class="w-full"
          :max-selected-labels="3"
          display="chip"
        />
      </div>

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Card Order</label>
        <Select v-model="deckOrder" :options="orderOptions" option-label="label" option-value="value" class="w-full" />
      </div>

      <div class="flex flex-col gap-2 mb-4">
        <div class="flex items-center gap-2">
          <Checkbox v-model="excludeKana" input-id="excludeKana" :binary="true" />
          <label for="excludeKana" class="text-sm cursor-pointer">Exclude kana-only words</label>
        </div>
      </div>

      <div class="flex items-center justify-between mb-3">
        <span class="text-sm text-gray-600 dark:text-gray-300 inline-flex items-center gap-2">
          <template v-if="isCountLoading">
            <i class="pi pi-spin pi-spinner text-gray-400 dark:text-gray-500 text-xs" />
            <span>Counting...</span>
          </template>
          <template v-else-if="previewCount !== null">
            <span>~<span class="font-bold text-gray-900 dark:text-gray-100">{{ previewCount.total.toLocaleString() }}</span> words match
              <span v-if="previewCount.unlearned !== previewCount.total" class="text-gray-500">(<span class="font-bold text-gray-900 dark:text-gray-100">{{ previewCount.unlearned.toLocaleString() }}</span> unknown)</span>
            </span>
          </template>
        </span>
      </div>
      <Button :label="isEditMode ? 'Save Changes' : 'Add to Study List'" class="w-full" :loading="adding" @click="addDeck" />
    </div>

    <!-- Step: Global Dynamic form -->
    <div v-if="step === 'global'">
      <div v-if="!isEditMode" class="flex items-center gap-2 mb-4">
        <Button icon="pi pi-arrow-left" severity="secondary" text size="small" @click="goBack" />
      </div>

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Deck Name</label>
        <InputText v-model="globalName" placeholder="e.g. Top 5000 words" class="w-full" :maxlength="200" />
      </div>
      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Description <span class="text-gray-400">(optional)</span></label>
        <Textarea v-model="globalDescription" class="w-full" rows="2" :maxlength="2000" />
      </div>
      <div class="grid grid-cols-1 sm:grid-cols-2 gap-3 mb-3">
        <div class="min-w-0">
          <label class="block text-xs mb-1">Min Frequency Rank</label>
          <InputNumber v-model="globalMinFreq" :min="1" class="w-full [&_input]:w-full" />
        </div>
        <div class="min-w-0">
          <label class="block text-xs mb-1">Max Frequency Rank</label>
          <InputNumber v-model="globalMaxFreq" :min="1" class="w-full [&_input]:w-full" />
        </div>
      </div>

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Only include parts of speech <span class="text-gray-400">(optional)</span></label>
        <MultiSelect
          v-model="globalPosFilter"
          :options="posOptions"
          option-label="label"
          option-value="value"
          placeholder="All (no filter)"
          class="w-full"
          :max-selected-labels="3"
          display="chip"
        />
      </div>

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Card Order</label>
        <Select v-model="globalOrder" :options="globalOrderOptions" option-label="label" option-value="value" class="w-full" />
      </div>

      <div class="flex flex-col gap-2 mb-4">
        <div class="flex items-center gap-2">
          <Checkbox v-model="excludeKana" input-id="gExcludeKana" :binary="true" />
          <label for="gExcludeKana" class="text-sm cursor-pointer">Exclude kana-only words</label>
        </div>
      </div>

      <Button
        :label="isEditMode ? 'Save Changes' : 'Create Deck'"
        class="w-full"
        :loading="adding"
        :disabled="!globalName.trim()"
        @click="addDeck"
      />
    </div>

    <!-- Step: Static Word List form -->
    <div v-if="step === 'static'">
      <div v-if="!isEditMode" class="flex items-center gap-2 mb-4">
        <Button icon="pi pi-arrow-left" severity="secondary" text size="small" @click="goBack" />
      </div>

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">List Name</label>
        <InputText v-model="staticName" placeholder="e.g. JLPT N2 Vocabulary" class="w-full" :maxlength="200" />
      </div>
      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Description <span class="text-gray-400">(optional)</span></label>
        <Textarea v-model="staticDescription" class="w-full" rows="2" :maxlength="2000" />
      </div>
      <div class="mb-4">
        <label class="block text-sm font-medium mb-1">Card Order</label>
        <Select v-model="staticOrder" :options="staticOrderOptions" option-label="label" option-value="value" class="w-full" />
      </div>

      <template v-if="!isEditMode">
        <div class="mb-4">
          <label class="block text-sm font-medium mb-2">Import from file <span class="text-gray-400">(optional)</span></label>
          <input
            type="file"
            :accept="IMPORT_ACCEPT_ATTR"
            class="block w-full text-sm text-gray-500
              file:mr-4 file:py-2 file:px-4
              file:rounded file:border-0
              file:text-sm file:font-semibold
              file:bg-purple-50 file:text-purple-700
              dark:file:bg-purple-900/30 dark:file:text-purple-300
              hover:file:bg-purple-100 dark:hover:file:bg-purple-800/40"
            @change="onImportFileSelect"
          />
          <p class="text-xs text-gray-400 mt-1">
            Supports {{ IMPORT_ACCEPT_EXTENSIONS.join(', ') }}
          </p>
        </div>

        <div v-if="importFile && !importIsFullTextOnly" class="mb-4">
          <label class="block text-sm font-medium mb-1">Input mode</label>
          <SelectButton
            :model-value="importParseFullText ? 'fulltext' : 'wordlist'"
            :options="[{ label: 'Word list', value: 'wordlist' }, { label: 'Full text', value: 'fulltext' }]"
            option-label="label"
            option-value="value"
            @update:model-value="(v: string) => importParseFullText = v === 'fulltext'"
          />
          <p class="text-xs text-gray-400 mt-1">{{ importParseFullText ? 'Extracts all vocabulary from sentences' : 'One word per line' }}</p>
        </div>
        <div v-if="importFile && importIsFullTextOnly" class="text-xs text-gray-400 mb-4">
          All vocabulary will be extracted from this file.
        </div>

        <div class="flex gap-2">
          <Button
            v-if="importFile"
            label="Upload & Preview"
            icon="pi pi-upload"
            class="flex-1"
            :loading="importUploading"
            :disabled="!staticName.trim()"
            @click="uploadAndPreview"
          />
          <Button
            :label="importFile ? 'Create Empty' : 'Create Word List'"
            :severity="importFile ? 'secondary' : undefined"
            :class="importFile ? 'flex-1' : 'w-full'"
            :loading="adding"
            :disabled="!staticName.trim()"
            @click="addDeck"
          />
        </div>
      </template>
      <template v-else>
        <Button
          label="Save Changes"
          class="w-full"
          :loading="adding"
          :disabled="!staticName.trim()"
          @click="addDeck"
        />
      </template>
    </div>

    <!-- Step: Static import preview -->
    <div v-if="step === 'static-import-preview'">
      <div class="flex items-center justify-between mb-4 pb-3 border-b border-gray-200 dark:border-gray-700">
        <Button icon="pi pi-arrow-left" severity="secondary" text size="small" @click="goBack" />
        <span class="text-sm text-gray-500">{{ importTotalLines }} lines parsed</span>
      </div>

      <SrsImportPreview
        :matched="importMatched"
        :unmatched="importUnmatched"
        :excluded-word-ids="importExcludedWordIds"
        @toggle-exclude="toggleImportExclude"
      />

      <div class="mb-3">
        <label class="block text-sm font-medium mb-1">Deck Name</label>
        <InputText v-model="staticName" class="w-full" :maxlength="200" />
      </div>

      <Button
        :label="`Import ${importIncludedCount} words`"
        class="w-full"
        :loading="importCommitting"
        :disabled="importIncludedCount === 0 || !staticName.trim()"
        @click="commitImport"
      />
    </div>
  </Dialog>
</template>
