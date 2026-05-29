<script setup lang="ts">
  import { type Deck, type StudyDeckDto, StudyDeckType, DeckDownloadType, DeckFormat, DeckOrder, MediaType } from '~/types';
  import { SelectButton, Select, Slider, InputNumber, Checkbox, Dialog, Button, ProgressSpinner } from 'primevue';
  import { debounce } from 'perfect-debounce';
  import { useAuthStore } from '~/stores/authStore';
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { computed, onMounted, ref, watch } from 'vue';
  const props = defineProps<{
    deck?: Deck;
    studyDeck?: StudyDeckDto;
    mediaList?: { apiBase: string; title: string; totalWords: number; hasExampleSentences: boolean };
    visible: boolean;
  }>();

  const isStudyDeckMode = computed(() => !!props.studyDeck);
  const isMediaListMode = computed(() => !!props.mediaList);
  const isMediaStudyDeck = computed(() => props.studyDeck?.deckType === StudyDeckType.MediaDeck);
  const isNonMediaStudyDeck = computed(() => isStudyDeckMode.value && !isMediaStudyDeck.value);
  const apiBase = computed(() =>
    isMediaListMode.value
      ? props.mediaList!.apiBase
      : isStudyDeckMode.value
        ? `srs/study-decks/${props.studyDeck!.userStudyDeckId}`
        : `media-deck/${props.deck!.deckId}`
  );
  const wordCount = computed(() =>
    isMediaListMode.value
      ? props.mediaList!.totalWords
      : isStudyDeckMode.value
        ? props.studyDeck!.totalWords
        : props.deck!.uniqueWordCount
  );
  const baseFileName = computed(() => {
    if (isMediaListMode.value) return props.mediaList!.title.substring(0, 40);
    if (isStudyDeckMode.value) {
      return isMediaStudyDeck.value
        ? localiseTitle({ originalTitle: props.studyDeck!.title, romajiTitle: props.studyDeck!.romajiTitle, englishTitle: props.studyDeck!.englishTitle }).substring(0, 30)
        : props.studyDeck!.name.substring(0, 30);
    }
    return localiseTitle(props.deck!).substring(0, 30);
  });

  const emit = defineEmits(['update:visible']);
  const { $api } = useNuxtApp();
  const authStore = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const confirm = useConfirm();
  const toast = useToast();
  const { hasCustomDictionaries, loadDictionaries } = useYomitanDictionary();
  const { processApkg, processCsv } = useClientApkg();

  const localVisible = ref(props.visible);
  const downloading = ref(false);
  const downloadStatusMessage = ref('');

  const useCustomDefinitions = ref(false);

  onMounted(async () => {
    await loadDictionaries();
    useCustomDefinitions.value = hasCustomDictionaries.value;
  });

  watch(hasCustomDictionaries, (has) => {
    useCustomDefinitions.value = has;
  });

  const showCustomDefinitions = computed(() => (format.value === DeckFormat.Anki || format.value === DeckFormat.Csv) && !isLearn.value);

  type Mode = 'manual' | 'target' | 'occurrence';
  const downloadMode = ref<Mode>('manual');
  const targetPercentage = ref(80);
  const startFromKnown = ref(true);
  const minTargetPercentage = computed(() => {
    if (!startFromKnown.value) return 0;
    const coverage = props.deck?.coverage ?? 0;
    return Math.ceil(coverage * 10) / 10;
  });

  // --- Options ---
  const deckOrders = getEnumOptions(DeckOrder, getDeckOrderText);
  let downloadTypes = getEnumOptions(DeckDownloadType, getDownloadTypeText);
  downloadTypes = downloadTypes.filter((d) => d.value != DeckDownloadType.TargetCoverage);

  const formatOptions = computed(() => [
    {
      value: DeckFormat.Anki,
      label: 'Anki',
      desc: 'Anki deck (.apkg)',
      icon: 'pi pi-clone',
      longDesc: `Generates an .apkg file using the <a href="https://github.com/donkuri/lapis/tree/main" target="_blank" class="text-primary hover:underline font-medium">Lapis template</a>, for use with Anki.`,
      disabled: false,
    },
    {
      value: DeckFormat.Txt,
      label: 'Text',
      desc: 'Vocabulary List (.txt)',
      icon: 'pi pi-file',
      longDesc: `Plain text file, one word per line, vocabulary only.`,
      disabled: false,
    },
    {
      value: DeckFormat.TxtRepeated,
      label: 'Text (Rep)',
      desc: 'Repeated vocab (.txt)',
      icon: 'pi pi-copy',
      longDesc: `Plain text format, one word per line, vocabulary only. The vocabulary is repeated for each occurrence to handle frequency on some websites.`,
      disabled: false,
    },
    {
      value: DeckFormat.Csv,
      label: 'CSV',
      desc: 'Spreadsheet',
      icon: 'pi pi-table',
      longDesc: `The vocabulary list with more data, such as the furigana, pitch, definitions, example sentence, etc.`,
      disabled: false,
    },
    {
      value: DeckFormat.Yomitan,
      label: 'Yomitan',
      desc: 'Occurrences dic (.zip)',
      icon: 'pi pi-book',
      longDesc: `A zip file importable as a Yomitan dictionary. It displays the specific number of occurrences of each word within this media source.`,
      disabled: isNonMediaStudyDeck.value,
    },
    {
      value: DeckFormat.Learn,
      label: 'Learn',
      desc: authStore.isAuthenticated ? 'Bulk vocabulary update' : 'Bulk vocabulary update (Login required)',
      icon: 'pi pi-graduation-cap',
      longDesc: `Mark the selected vocabulary as <b>mastered</b> or <b>blacklisted</b> in your vocabulary tracker. No file is downloaded, the words are applied directly to your account. Both of those options count towards your coverage after you trigger a manual refresh.`,
      disabled: !authStore.isAuthenticated || isStudyDeckMode.value,
    },
  ]);

  const learnStateOptions = [
    { label: 'Mastered (never forget)', value: 'mastered' },
    { label: 'Blacklisted (ignore)', value: 'blacklisted' },
  ];
  const learnState = ref<'mastered' | 'blacklisted'>('mastered');

  const isLearn = computed(() => format.value === DeckFormat.Learn);
  const isOccurrences = computed(() => format.value === DeckFormat.Yomitan);
  const showStrategyAndOptions = computed(() => !isOccurrences.value && !isNonMediaStudyDeck.value);
  const hasExampleSentences = computed(() => {
    if (isMediaListMode.value) return props.mediaList!.hasExampleSentences;
    const mt = props.deck?.mediaType ?? props.studyDeck?.mediaType;
    return mt === MediaType.Novel || mt === MediaType.NonFiction || mt === MediaType.VideoGame || mt === MediaType.VisualNovel || mt === MediaType.WebNovel;
  });

  const modeOptions = computed(() => [
    { label: 'Manual', value: 'manual', icon: 'pi pi-sliders-h' },
    { label: 'Occurrences', value: 'occurrence', icon: 'pi pi-hashtag' },
    {
      label: authStore.isAuthenticated ? 'Coverage %' : 'Coverage % (Login req.)',
      value: 'target',
      icon: 'pi pi-chart-pie',
      disabled: !authStore.isAuthenticated,
    },
  ]);

  // Models
  const format = defineModel<DeckFormat>('deckFormat', { default: DeckFormat.Anki });
  const downloadType = defineModel<DeckDownloadType>('downloadType', { default: DeckDownloadType.TopDeckFrequency });
  const deckOrder = defineModel<DeckOrder>('deckOrder', { default: DeckOrder.DeckFrequency });
  const frequencyRange = defineModel<number[]>('frequencyRange');

  // Exclusions
  const excludeKana = ref(false);
  const excludeMatureMasteredBlacklisted = ref(false);
  const excludeAllTrackedWords = ref(false);
  const excludeExampleSentences = ref(false);

  // Stats
  const currentSliderMax = ref(wordCount.value);
  const debouncedCurrentCardAmount = ref(0);
  const accurateCardAmount = ref<number | null>(null);
  const isFrequencyCountLoading = ref(false);
  const isOccurrenceCountLoading = ref(false);
  const isAccurateCountLoading = ref(false);
  const minCountSpinnerMs = 200;
  let frequencyCountLoadingStartedAt = 0;
  let occurrenceCountLoadingStartedAt = 0;
  let accurateCountLoadingStartedAt = 0;

  // Occurrence Count mode
  const occurrenceFilterType = ref<'gte' | 'lte'>('gte');
  const occurrenceThreshold = ref(10);
  const occurrenceCount = ref(0);

  // Computed for current selection details
  const currentFormatDetails = computed(() => {
    return formatOptions.value.find((f) => f.value === format.value) || formatOptions.value[0];
  });

  const targetPercentageCardCount = computed(() => {
    return Math.floor(wordCount.value * (targetPercentage.value / 100));
  });

  const requiresAccurateCardAmount = computed(() => {
    if (downloadMode.value === 'target') return true;
    if (excludeKana.value) return true;
    if (authStore.isAuthenticated && (excludeMatureMasteredBlacklisted.value || excludeAllTrackedWords.value)) return true;
    return false;
  });

  const fallbackCardAmount = computed(() => {
    if (downloadMode.value === 'target') return targetPercentageCardCount.value;
    if (downloadMode.value === 'occurrence') return occurrenceCount.value;

    if (downloadType.value == DeckDownloadType.Full) {
      return wordCount.value;
    } else if (downloadType.value == DeckDownloadType.TopDeckFrequency || downloadType.value == DeckDownloadType.TopChronological) {
      return (frequencyRange.value?.[1] ?? 0) - (frequencyRange.value?.[0] ?? 0);
    } else if (downloadType.value == DeckDownloadType.TopGlobalFrequency) {
      return debouncedCurrentCardAmount.value;
    }
    return 0;
  });

  const currentCardAmount = computed(() => {
    if (isOccurrences.value) return wordCount.value;
    if (requiresAccurateCardAmount.value) return accurateCardAmount.value ?? fallbackCardAmount.value;
    return fallbackCardAmount.value;
  });

  const isCountLoading = computed(() => isFrequencyCountLoading.value || isOccurrenceCountLoading.value || isAccurateCountLoading.value);

  // --- Lifecycle & Watches ---
  onMounted(() => {
    if (!frequencyRange.value) {
      frequencyRange.value = [0, Math.min(wordCount.value, 5000)];
    }

    if (localVisible.value && requiresAccurateCardAmount.value) {
      fetchAccurateCardAmount();
    }
  });

  watch(
    () => props.visible,
    (newVal) => (localVisible.value = newVal)
  );
  watch(localVisible, (newVal) => emit('update:visible', newVal));

  watch(
    () => downloadType.value,
    (newVal) => {
      if (newVal == DeckDownloadType.Full) {
        frequencyRange.value = [0, wordCount.value];
        currentSliderMax.value = wordCount.value;
      } else if (newVal == DeckDownloadType.TopDeckFrequency || newVal == DeckDownloadType.TopChronological) {
        frequencyRange.value = [0, Math.min(wordCount.value, 5000)];
        currentSliderMax.value = wordCount.value;
      } else if (newVal == DeckDownloadType.TopGlobalFrequency) {
        frequencyRange.value = [1, Math.min(200000, 30000)];
        currentSliderMax.value = 200000;
        updateDebounced();
      }
    }
  );

  watch(
    () => frequencyRange.value,
    () => {
      if (downloadType.value == DeckDownloadType.TopGlobalFrequency) updateDebounced();
    }
  );

  watch(
    () => authStore.isAuthenticated,
    (isAuth) => {
      if (!isAuth && downloadMode.value === 'target') {
        downloadMode.value = 'manual';
      }
      if (!isAuth && format.value === DeckFormat.Learn) {
        format.value = DeckFormat.Anki;
      }
    }
  );

  watch(
    () => format.value,
    (newFormat) => {
      if (newFormat === DeckFormat.Learn) {
        excludeMatureMasteredBlacklisted.value = true;
      }
    }
  );

  watch(startFromKnown, (val) => {
    if (!val && downloadMode.value === 'target' && authStore.isAuthenticated) {
      excludeMatureMasteredBlacklisted.value = true;
      excludeAllTrackedWords.value = true;
    }
  });

  watch(
    minTargetPercentage,
    (minVal) => {
      if (targetPercentage.value < minVal) {
        targetPercentage.value = minVal;
      }
    },
    { immediate: true }
  );

  let frequencyRequestId = 0;
  const updateDebounced = debounce(async () => {
    if (!localVisible.value && !props.visible) return;
    if (requiresAccurateCardAmount.value) return;
    if (!frequencyRange.value) return;
    const reqId = ++frequencyRequestId;
    frequencyCountLoadingStartedAt = Date.now();
    isFrequencyCountLoading.value = true;

    try {
      const response = await $api<number>(`${apiBase.value}/vocabulary-count-frequency`, {
        query: { minFrequency: frequencyRange.value[0], maxFrequency: frequencyRange.value[1] },
      });
      if (reqId === frequencyRequestId && typeof response === 'number') {
        debouncedCurrentCardAmount.value = response;
      }
    } catch (err) {
      console.error('Failed to fetch frequency count:', err);
    } finally {
      if (reqId === frequencyRequestId) {
        const elapsed = Date.now() - frequencyCountLoadingStartedAt;
        const remaining = minCountSpinnerMs - elapsed;
        if (remaining > 0) {
          setTimeout(() => {
            if (reqId === frequencyRequestId) isFrequencyCountLoading.value = false;
          }, remaining);
        } else {
          isFrequencyCountLoading.value = false;
        }
      }
    }
  }, 500);

  let occurrenceRequestId = 0;
  async function fetchOccurrenceCount() {
    if (!localVisible.value && !props.visible) return;
    if (requiresAccurateCardAmount.value) return;
    const reqId = ++occurrenceRequestId;
    const query: Record<string, number> = {};

    const threshold = Number(occurrenceThreshold.value);
    if (!Number.isFinite(threshold) || threshold < 1) return;

    if (occurrenceFilterType.value === 'gte') query.minOccurrences = threshold;
    else query.maxOccurrences = threshold;

    occurrenceCountLoadingStartedAt = Date.now();
    isOccurrenceCountLoading.value = true;
    try {
      const response = await $api<number>(`${apiBase.value}/vocabulary-count-occurrences`, { query });
      if (reqId === occurrenceRequestId && typeof response === 'number') {
        occurrenceCount.value = response;
      }
    } catch (err) {
      console.error('Failed to fetch occurrence count:', err);
    } finally {
      if (reqId === occurrenceRequestId) {
        const elapsed = Date.now() - occurrenceCountLoadingStartedAt;
        const remaining = minCountSpinnerMs - elapsed;
        if (remaining > 0) {
          setTimeout(() => {
            if (reqId === occurrenceRequestId) isOccurrenceCountLoading.value = false;
          }, remaining);
        } else {
          isOccurrenceCountLoading.value = false;
        }
      }
    }
  }
  const fetchOccurrenceCountDebounced = debounce(fetchOccurrenceCount, 500);

  watch([occurrenceFilterType, occurrenceThreshold], () => fetchOccurrenceCountDebounced());
  watch(downloadMode, (mode) => {
    if (mode === 'occurrence') fetchOccurrenceCount();
  });
  watch(() => props.visible, (visible) => {
    if (visible && downloadMode.value === 'occurrence') fetchOccurrenceCount();
  });

  watch(
    [
      () => localVisible.value,
      requiresAccurateCardAmount,
      downloadMode,
      downloadType,
      deckOrder,
      () => frequencyRange.value?.[0],
      () => frequencyRange.value?.[1],
      occurrenceFilterType,
      occurrenceThreshold,
      targetPercentage,
      startFromKnown,
      excludeKana,
      excludeMatureMasteredBlacklisted,
      excludeAllTrackedWords,
    ],
    () => {
      if (!localVisible.value) {
        accurateCardAmount.value = null;
        isAccurateCountLoading.value = false;
        return;
      }

      if (!requiresAccurateCardAmount.value) {
        accurateCardAmount.value = null;
        isAccurateCountLoading.value = false;
        return;
      }

      fetchAccurateCardAmountDebounced();
    }
  );

  // --- Helpers ---
  function buildFilterPayload() {
    let payload: any = {
      excludeKana: excludeKana.value,
      excludeMatureMasteredBlacklisted: excludeMatureMasteredBlacklisted.value,
      excludeAllTrackedWords: excludeAllTrackedWords.value,
    };

    if (downloadMode.value === 'target') {
      payload = {
        ...payload,
        downloadType: DeckDownloadType.TargetCoverage,
        targetPercentage: targetPercentage.value,
        startFromKnown: startFromKnown.value,
        order: deckOrder.value,
      };
    } else if (downloadMode.value === 'occurrence') {
      payload = {
        ...payload,
        downloadType: DeckDownloadType.OccurrenceCount,
        order: deckOrder.value,
        minOccurrences: occurrenceFilterType.value === 'gte' ? occurrenceThreshold.value : undefined,
        maxOccurrences: occurrenceFilterType.value === 'lte' ? occurrenceThreshold.value : undefined,
      };
    } else {
      payload = {
        ...payload,
        downloadType: downloadType.value,
        order: deckOrder.value,
        minFrequency: frequencyRange.value![0],
        maxFrequency: frequencyRange.value![1],
      };
    }

    return payload;
  }

  function buildCountPayload() {
    const payload = buildFilterPayload();
    return {
      format: format.value === DeckFormat.Learn ? DeckFormat.Anki : format.value,
      downloadType: payload.downloadType,
      order: payload.order,
      minFrequency: payload.minFrequency ?? 0,
      maxFrequency: payload.maxFrequency ?? 0,
      excludeKana: payload.excludeKana,
      excludeMatureMasteredBlacklisted: payload.excludeMatureMasteredBlacklisted,
      excludeAllTrackedWords: payload.excludeAllTrackedWords,
      excludeExampleSentences: excludeExampleSentences.value,
      targetPercentage: payload.targetPercentage,
      startFromKnown: payload.startFromKnown,
      minOccurrences: payload.minOccurrences,
      maxOccurrences: payload.maxOccurrences,
    };
  }

  let accurateCountRequestId = 0;
  const fetchAccurateCardAmount = async (force = false) => {
    if (!force) {
      if (!localVisible.value) return;
      if (!requiresAccurateCardAmount.value) return;
    }

    const reqId = ++accurateCountRequestId;
    accurateCountLoadingStartedAt = Date.now();
    isAccurateCountLoading.value = true;
    try {
      const url = `${apiBase.value}/vocabulary-count`;
      const response = await $api<number>(url, {
        method: 'POST',
        body: buildCountPayload(),
        headers: { 'Content-Type': 'application/json' },
      });

      if (reqId === accurateCountRequestId) {
        accurateCardAmount.value = response ?? 0;
      }
    } catch (err) {
      console.error('Failed to fetch accurate card count:', err);
    } finally {
      if (reqId === accurateCountRequestId) {
        const elapsed = Date.now() - accurateCountLoadingStartedAt;
        const remaining = minCountSpinnerMs - elapsed;
        if (remaining > 0) {
          setTimeout(() => {
            if (reqId === accurateCountRequestId) isAccurateCountLoading.value = false;
          }, remaining);
        } else {
          isAccurateCountLoading.value = false;
        }
      }
    }
  };
  const fetchAccurateCardAmountDebounced = debounce(fetchAccurateCardAmount, 500);

  // --- Actions ---
  const downloadFile = async () => {
    try {
      downloading.value = true;
      downloadStatusMessage.value = 'Preparing download...';
      const url = `${apiBase.value}/download`;

      const payload = {
        ...buildFilterPayload(),
        format: format.value,
        excludeExampleSentences: excludeExampleSentences.value,
      };

      const response = await $api<File>(url, {
        method: 'POST',
        body: payload,
        headers: { 'Content-Type': 'application/json' },
        responseType: 'blob',
      });

      if (response) {
        let finalBlob: Blob = response as unknown as Blob;

        if (useCustomDefinitions.value && format.value === DeckFormat.Anki) {
          downloadStatusMessage.value = 'Applying custom definitions...';
          try {
            finalBlob = await processApkg(finalBlob, (progress) => {
              if (progress.phase === 'unzipping') downloadStatusMessage.value = 'Unpacking deck...';
              else if (progress.phase === 'loading') downloadStatusMessage.value = 'Loading database...';
              else if (progress.phase === 'processing') downloadStatusMessage.value = `Replacing definitions (${progress.current}/${progress.total})...`;
              else if (progress.phase === 'zipping') downloadStatusMessage.value = 'Repacking deck...';
            });
          } catch (err) {
            console.error('Failed to apply custom definitions:', err);
            toast.add({
              severity: 'warn',
              summary: 'Custom Definitions Failed',
              detail: 'Downloading with default JMDict definitions instead.',
              life: 5000,
            });
          }
        } else if (useCustomDefinitions.value && format.value === DeckFormat.Csv) {
          downloadStatusMessage.value = 'Adding definitions from your dictionaries...';
          try {
            finalBlob = await processCsv(finalBlob, (progress) => {
              if (progress.phase === 'parsing') downloadStatusMessage.value = 'Parsing CSV...';
              else if (progress.phase === 'processing') downloadStatusMessage.value = `Adding definitions (${progress.current}/${progress.total})...`;
              else if (progress.phase === 'building') downloadStatusMessage.value = 'Building CSV...';
            });
          } catch (err) {
            console.error('Failed to apply custom definitions:', err);
            toast.add({
              severity: 'warn',
              summary: 'Custom Definitions Failed',
              detail: 'Downloading with default JMDict definitions instead.',
              life: 5000,
            });
          }
        }

        localVisible.value = false;
        const blobUrl = window.URL.createObjectURL(finalBlob);
        const link = document.createElement('a');
        link.href = blobUrl;

        const extMap: Record<string, string> = {
          [DeckFormat.Anki]: 'apkg',
          [DeckFormat.Csv]: 'csv',
          [DeckFormat.Yomitan]: 'zip',
          [DeckFormat.Txt]: 'txt',
          [DeckFormat.TxtRepeated]: 'txt',
        };

        link.setAttribute('download', `${baseFileName.value}.${extMap[format.value]}`);
        document.body.appendChild(link);
        link.click();
        link.remove();
        URL.revokeObjectURL(blobUrl);
      }
    } catch (err) {
      console.error('Error:', err);
    } finally {
      downloading.value = false;
      downloadStatusMessage.value = '';
    }
  };

  const applyLearn = async () => {
    const stateLabel = learnState.value === 'mastered' ? 'mastered' : 'blacklisted';
    if (requiresAccurateCardAmount.value) {
      await fetchAccurateCardAmount(true);
    }

    const count = currentCardAmount.value;
    confirm.require({
      message: `This will mark approximately ${count} words as ${stateLabel}. Continue?`,
      header: 'Confirm Vocabulary Update',
      icon: learnState.value === 'blacklisted' ? 'pi pi-exclamation-triangle' : 'pi pi-check-circle',
      acceptClass: learnState.value === 'blacklisted' ? 'p-button-danger' : 'p-button-primary',
      accept: async () => {
        try {
          downloading.value = true;
          const url = `${apiBase.value}/learn`;

          const payload = {
            ...buildFilterPayload(),
            vocabularyState: learnState.value,
          };

          const response = await $api<{ applied: number; state: string }>(url, {
            method: 'POST',
            body: payload,
            headers: { 'Content-Type': 'application/json' },
          });

          localVisible.value = false;
          toast.add({
            severity: 'success',
            summary: 'Vocabulary Updated',
            detail: `${response?.applied ?? 0} words marked as ${stateLabel}.`,
            life: 5000,
          });
        } catch (err) {
          console.error('Error:', err);
          toast.add({
            severity: 'error',
            summary: 'Error',
            detail: 'Failed to apply vocabulary changes. Please try again.',
            life: 5000,
          });
        } finally {
          downloading.value = false;
        }
      },
    });
  };

  const onAction = async () => {
    if (isLearn.value) {
      await applyLearn();
    } else {
      downloadFile();
    }
  };
</script>

<template>
  <Dialog v-model:visible="localVisible" modal :header="isLearn ? 'Learn Vocabulary' : 'Download Deck'" class="w-[95vw] sm:w-[90vw] md:w-[42rem]" :pt="{ content: { class: 'p-0' } }">
    <div class="flex flex-col h-full">
      <!-- SCROLLABLE CONTENT AREA -->
      <div class="p-5 overflow-y-auto max-h-[70vh] flex flex-col gap-6">
        <!-- 1. FORMAT SELECTION -->
        <section>
          <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">Format</div>

          <!-- Grid -->
          <div class="grid grid-cols-2 sm:grid-cols-3 gap-3">
            <div
              v-for="opt in formatOptions"
              :key="opt.value"
              @click="!opt.disabled && (format = opt.value)"
              class="border rounded-lg p-3 transition-all duration-200 flex flex-col gap-1 items-start relative"
              :class="[
                opt.disabled
                  ? 'opacity-50 cursor-not-allowed bg-gray-100 dark:bg-gray-900 border-gray-200 dark:border-gray-700'
                  : format === opt.value
                    ? 'bg-primary-50 dark:bg-gray-600 border-primary dark:border-gray-700 ring-1 ring-primary cursor-pointer hover:border-gray-400 hover:dark:border-gray-500 hover:shadow-sm'
                    : 'bg-white dark:bg-gray-800 border-gray-200 dark:border-gray-700 cursor-pointer hover:border-gray-400 hover:dark:border-gray-500 hover:shadow-sm',
              ]"
            >
              <div class="flex items-center gap-2 w-full">
                <i :class="[opt.icon, !opt.disabled && format === opt.value ? 'text-primary' : 'text-gray-400 dark:text-gray-500']" class="text-lg"></i>
                <span class="font-semibold text-sm" :class="!opt.disabled && format === opt.value ? 'text-primary-900 dark:text-primary-300' : 'text-gray-700 dark:text-gray-300'">{{ opt.label }}</span>
              </div>
              <span class="text-[10px] leading-tight text-gray-500 dark:text-gray-400">{{ opt.desc }}</span>
              <!-- Active Badge -->
              <i v-if="!opt.disabled && format === opt.value" class="pi pi-check-circle text-primary absolute top-2 right-2 text-sm"></i>
            </div>
          </div>

          <!-- Description Box (Fixed Min-Height to prevent shift) -->
          <div class="mt-3 bg-gray-50 dark:bg-gray-900 border border-gray-200 dark:border-gray-700 rounded-md p-3 text-sm text-gray-600 dark:text-gray-300 min-h-[4.5rem] flex items-center">
            <p v-html="sanitiseHtml(currentFormatDetails.longDesc)" class="leading-relaxed"></p>
          </div>
        </section>

        <template v-if="showStrategyAndOptions">
          <!-- 2. STRATEGY -->
          <section>
            <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">{{ isLearn ? 'Learn Strategy' : 'Download Strategy' }}</div>
            <div class="hidden sm:block">
              <SelectButton
                v-model="downloadMode"
                :options="modeOptions"
                option-value="value"
                option-label="label"
                option-disabled="disabled"
                class="w-full"
                :pt="{ button: { class: 'flex-1 text-sm py-2 whitespace-nowrap' } }"
              />
            </div>
            <Select
              v-model="downloadMode"
              :options="modeOptions"
              option-value="value"
              option-label="label"
              option-disabled="disabled"
              class="w-full sm:!hidden text-sm"
              size="small"
            />

            <!-- MODE A: TARGET PERCENTAGE -->
            <div v-if="downloadMode === 'target'" class="mt-4 bg-gray-50 dark:bg-gray-900 rounded-xl p-5 border border-dashed border-gray-300 dark:border-gray-600">
              <div class="flex justify-between items-end mb-4">
                <div class="flex flex-col">
                  <span class="font-bold text-gray-800 dark:text-gray-200 text-lg">Deck Coverage</span>
                  <span class="text-xs text-gray-500 dark:text-gray-400">Select the least amount of words to reach the desired coverage.</span>
                </div>
                <div class="text-2xl font-black text-primary">{{ targetPercentage }}%</div>
              </div>
              <Slider v-model="targetPercentage" :step="0.1" :min="minTargetPercentage" :max="100" class="w-full" />
              <div class="flex items-start gap-3 mt-4">
                <Checkbox v-model="startFromKnown" input-id="startFromKnown" :binary="true" class="mt-1" />
                <div>
                  <label for="startFromKnown" class="text-sm font-medium text-gray-800 dark:text-gray-200 cursor-pointer">Start from my current coverage</label>
                  <div class="text-xs text-gray-500 dark:text-gray-400">
                    {{ startFromKnown
                      ? 'Only the words you still need to learn to reach this coverage.'
                      : 'All words required for this coverage, as if starting from zero.' }}
                  </div>
                </div>
              </div>
              <div class="flex flex-col gap-1 mt-4">
                <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Then Sort By</label>
                <Select v-model="deckOrder" :options="deckOrders" option-value="value" option-label="label" class="w-full text-sm" size="small" />
              </div>
            </div>

            <!-- MODE C: OCCURRENCE COUNT -->
            <div v-else-if="downloadMode === 'occurrence'" class="mt-4 bg-gray-50 dark:bg-gray-900 rounded-xl p-5 border border-dashed border-gray-300 dark:border-gray-600">
              <div class="flex flex-col gap-4">
                <div class="flex flex-col">
                  <span class="font-bold text-gray-800 dark:text-gray-200 text-lg">Occurrence Count</span>
                  <span class="text-xs text-gray-500 dark:text-gray-400">Filter words by how many times they appear in this deck.</span>
                </div>
                <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                  <div class="flex flex-col gap-1">
                    <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Filter Type</label>
                    <Select
                      v-model="occurrenceFilterType"
                      :options="[
                        { label: 'Over or equal to (≥)', value: 'gte' },
                        { label: 'Under or equal to (≤)', value: 'lte' },
                      ]"
                      option-value="value"
                      option-label="label"
                      class="w-full text-sm"
                      size="small"
                    />
                  </div>
                  <div class="flex flex-col gap-1">
                    <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Threshold</label>
                    <InputNumber v-model="occurrenceThreshold" :min="1" :useGrouping="false" class="w-full text-sm" size="small" />
                  </div>
                </div>
                <div class="flex flex-col gap-1">
                  <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Then Sort By</label>
                  <Select v-model="deckOrder" :options="deckOrders" option-value="value" option-label="label" class="w-full text-sm" size="small" />
                </div>
              </div>
            </div>

            <!-- MODE B: MANUAL CONTROL -->
            <div v-else class="mt-4 flex flex-col gap-4">
              <div class="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div class="flex flex-col gap-1">
                  <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Filter By</label>
                  <Select v-model="downloadType" :options="downloadTypes" option-value="value" option-label="label" class="w-full text-sm" size="small" />
                </div>
                <div class="flex flex-col gap-1">
                  <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Then Sort By</label>
                  <Select v-model="deckOrder" :options="deckOrders" option-value="value" option-label="label" class="w-full text-sm" size="small" />
                </div>
              </div>

              <div v-if="downloadType != DeckDownloadType.Full" class="bg-gray-50 dark:bg-gray-900 rounded-lg p-4 border border-gray-200 dark:border-gray-700">
                <div class="flex justify-between items-center mb-3">
                  <div class="flex flex-col">
                    <span class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase">Frequency Range</span>
                    <span class="text-[10px] text-gray-400 dark:text-gray-500">Select start and end points</span>
                  </div>
                  <div class="flex items-center gap-2">
                    <InputNumber
                      :model-value="frequencyRange?.[0] ?? 0"
                      @update:model-value="(v) => (frequencyRange = [v ?? 0, frequencyRange?.[1] ?? 0])"
                      inputClass="text-center p-1 text-xs w-16 h-7"
                      :min="0"
                      :max="currentSliderMax"
                      :useGrouping="false"
                    />
                    <span class="text-gray-400 dark:text-gray-500">-</span>
                    <InputNumber
                      :model-value="frequencyRange?.[1] ?? 0"
                      @update:model-value="(v) => (frequencyRange = [frequencyRange?.[0] ?? 0, v ?? 0])"
                      inputClass="text-center p-1 text-xs w-16 h-7"
                      :min="0"
                      :max="currentSliderMax"
                      :useGrouping="false"
                    />
                  </div>
                </div>
                <Slider v-model="frequencyRange" range :min="0" :max="currentSliderMax" class="w-full" />
              </div>
            </div>
          </section>

          <!-- 3. OPTIONS -->
          <section>
            <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">Options</div>
            <div class="flex flex-col gap-0">
              <div
                v-if="downloadMode !== 'target' || !startFromKnown"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 transition-colors cursor-pointer"
                @click="excludeKana = !excludeKana"
              >
                <Checkbox v-model="excludeKana" binary class="mt-1" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude Kana-only Words</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">Removes words that have no Kanji (e.g., こころ, それでも, ...).</div>
                </div>
              </div>

              <div
                v-if="!isLearn"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent transition-colors"
                :class="hasExampleSentences
                  ? 'hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 cursor-pointer'
                  : 'opacity-50 cursor-not-allowed'"
                @click="hasExampleSentences && (excludeExampleSentences = !excludeExampleSentences)"
              >
                <Checkbox v-model="excludeExampleSentences" binary class="mt-1" :disabled="!hasExampleSentences" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude Example Sentences</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">
                    <template v-if="hasExampleSentences">Remove example sentences.</template>
                    <template v-else>Example sentences are not available for this media type.</template>
                  </div>
                </div>
              </div>

              <div
                v-if="(downloadMode !== 'target' || !startFromKnown) && authStore.isAuthenticated"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 transition-colors cursor-pointer"
                @click="excludeMatureMasteredBlacklisted = !excludeMatureMasteredBlacklisted"
              >
                <Checkbox v-model="excludeMatureMasteredBlacklisted" binary class="mt-1" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">
                    Exclude Mature, Mastered & Blacklisted Vocabulary
                    <span v-if="isLearn" class="text-gray-500 dark:text-gray-400 font-normal">(overwrite their state)</span>
                  </div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">Removes words that are mature (21+ day review interval), mastered, or blacklisted.</div>
                </div>
              </div>

              <div
                v-if="(downloadMode !== 'target' || !startFromKnown) && authStore.isAuthenticated"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 transition-colors cursor-pointer"
                @click="excludeAllTrackedWords = !excludeAllTrackedWords"
              >
                <Checkbox v-model="excludeAllTrackedWords" binary class="mt-1" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude All Tracked Vocabulary</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">Removes all words in your vocabulary list, regardless of their status.</div>
                </div>
              </div>

              <!-- Custom Definitions -->
              <div
                v-if="showCustomDefinitions"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent transition-colors"
                :class="hasCustomDictionaries
                  ? 'hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 cursor-pointer'
                  : 'opacity-60'"
                @click="hasCustomDictionaries && (useCustomDefinitions = !useCustomDefinitions)"
              >
                <Checkbox v-model="useCustomDefinitions" binary class="mt-1" :disabled="!hasCustomDictionaries" @click.stop />
                <div class="flex-1">
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Use Custom Dictionaries</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">
                    <template v-if="hasCustomDictionaries">
                      Add the definitions from your custom dictionaries.
                    </template>
                    <template v-else>
                      No custom dictionaries imported.
                    </template>
                    <NuxtLink to="/settings/dictionaries" class="text-primary hover:underline" @click.stop>Manage dictionaries</NuxtLink>
                  </div>
                </div>
              </div>

              <!-- Learn: Vocabulary State selector -->
              <div v-if="isLearn" class="flex flex-col gap-1 p-3">
                <label class="text-xs text-gray-500 dark:text-gray-400 font-medium">Vocabulary State</label>
                <Select v-model="learnState" :options="learnStateOptions" option-value="value" option-label="label" class="w-full text-sm" size="small" />
              </div>
            </div>
          </section>
        </template>

        <template v-if="isNonMediaStudyDeck && !isOccurrences">
          <section>
            <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest mb-3">Options</div>
            <div class="flex flex-col gap-0">
              <div
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 transition-colors cursor-pointer"
                @click="excludeKana = !excludeKana"
              >
                <Checkbox v-model="excludeKana" binary class="mt-1" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude Kana-only Words</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">Removes words that have no Kanji (e.g., こころ, それでも, ...).</div>
                </div>
              </div>
              <div
                v-if="!isLearn"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent transition-colors"
                :class="hasExampleSentences
                  ? 'hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 cursor-pointer'
                  : 'opacity-50 cursor-not-allowed'"
                @click="hasExampleSentences && (excludeExampleSentences = !excludeExampleSentences)"
              >
                <Checkbox v-model="excludeExampleSentences" binary class="mt-1" :disabled="!hasExampleSentences" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude Example Sentences</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">
                    <template v-if="hasExampleSentences">Remove example sentences.</template>
                    <template v-else>Example sentences are not available for this media type.</template>
                  </div>
                </div>
              </div>
              <div
                v-if="authStore.isAuthenticated"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 transition-colors cursor-pointer"
                @click="excludeMatureMasteredBlacklisted = !excludeMatureMasteredBlacklisted"
              >
                <Checkbox v-model="excludeMatureMasteredBlacklisted" binary class="mt-1" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude Mature, Mastered & Blacklisted Vocabulary</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">Removes words that are mature (21+ day review interval), mastered, or blacklisted.</div>
                </div>
              </div>
              <div
                v-if="authStore.isAuthenticated"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 transition-colors cursor-pointer"
                @click="excludeAllTrackedWords = !excludeAllTrackedWords"
              >
                <Checkbox v-model="excludeAllTrackedWords" binary class="mt-1" @click.stop />
                <div>
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Exclude All Tracked Vocabulary</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">Removes all words in your vocabulary list, regardless of their status.</div>
                </div>
              </div>
              <div
                v-if="showCustomDefinitions"
                class="flex items-start gap-3 p-3 rounded-lg border border-transparent transition-colors"
                :class="hasCustomDictionaries
                  ? 'hover:bg-gray-50 hover:dark:bg-gray-800 hover:border-gray-200 hover:dark:border-gray-700 cursor-pointer'
                  : 'opacity-60'"
                @click="hasCustomDictionaries && (useCustomDefinitions = !useCustomDefinitions)"
              >
                <Checkbox v-model="useCustomDefinitions" binary class="mt-1" :disabled="!hasCustomDictionaries" @click.stop />
                <div class="flex-1">
                  <div class="text-sm font-medium text-gray-800 dark:text-gray-200">Use Custom Dictionaries</div>
                  <div class="text-xs text-gray-500 dark:text-gray-400">
                    <template v-if="hasCustomDictionaries">
                      Add the definitions from your custom dictionaries.
                    </template>
                    <template v-else>
                      No custom dictionaries imported.
                    </template>
                    <NuxtLink to="/settings/dictionaries" class="text-primary hover:underline" @click.stop>Manage dictionaries</NuxtLink>
                  </div>
                </div>
              </div>
            </div>
          </section>
        </template>
      </div>

      <!-- FOOTER -->
      <div class="bg-gray-50 dark:bg-gray-900 border-t border-gray-200 dark:border-gray-700 p-4 flex flex-col sm:flex-row justify-between items-center gap-4 shrink-0">
        <div class="text-sm text-gray-600 dark:text-gray-300">
          <span class="inline-flex items-center gap-2">
            <span>Result:{{ isOccurrences ? '' : ' approx' }}</span>
            <span class="font-bold text-gray-900 dark:text-gray-100">{{ currentCardAmount }}</span>
            <i v-if="isCountLoading" class="pi pi-spin pi-spinner text-gray-400 dark:text-gray-500 text-xs" />
            <span>{{ format === DeckFormat.Anki ? 'cards' : 'words' }}</span>
          </span>
        </div>
        <Button
          :label="isLearn ? 'Apply to Vocabulary' : 'Download Deck'"
          :icon="isLearn ? 'pi pi-check' : 'pi pi-download'"
          @click="onAction()"
          :loading="downloading"
          class="w-full sm:w-auto"
        />
      </div>
    </div>
  </Dialog>

  <div v-if="downloading" class="fixed inset-0 z-[9999] flex flex-col items-center justify-center bg-black/60 backdrop-blur-sm text-white">
    <ProgressSpinner style="width: 50px; height: 50px" stroke-width="6" />
    <div class="mt-4 font-medium text-lg">{{ isLearn ? 'Applying vocabulary changes...' : downloadStatusMessage || 'Preparing download...' }}</div>
  </div>
</template>

<style scoped>
  :deep(.p-inputnumber-input) {
    padding: 0.25rem 0.5rem;
    font-size: 0.875rem;
  }
</style>
