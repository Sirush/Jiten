<script setup lang="ts">
  import { KnownState, type ExampleSentence, type ExampleSentencesByDifficultyResponse, type MediaType, type UserExampleSentenceDto, type Word } from '~/types';
  import { formatPercentageApprox } from '~/utils/formatPercentageApprox';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { stripRubyMarkup } from '~/utils/stripRubyMarkup';
  import ExampleSentenceEntry from '~/components/ExampleSentenceEntry.vue';
  import CustomExampleSentenceEntry from '~/components/CustomExampleSentenceEntry.vue';
  import Button from 'primevue/button';
  import Select from 'primevue/select';
  import { useJitenStore } from '~/stores/jitenStore';

  const props = defineProps({
    wordId: {
      type: Number,
      required: true,
    },
    readingIndex: {
      type: Number,
      required: true,
    },
    showRedirect: {
      type: Boolean,
      required: false,
    },
    conjugations: {
      type: Array as PropType<string[]>,
      default: () => [],
      required: false,
    },
  });

  const emit = defineEmits(['mainReadingTextChanged', 'readingSelected']);
  const { $api } = useNuxtApp();

  const store = useJitenStore();
  const authStore = useAuthStore();
  const convertToRuby = useConvertToRuby();

  const currentWordId = ref(props.wordId);
  const currentReadingIndex = ref(props.readingIndex);
  const infoUrl = computed(() => `vocabulary/${currentWordId.value}/${currentReadingIndex.value}/info`);
  const mediaFreqUrl = computed(() => `vocabulary/${props.wordId}/${currentReadingIndex.value}/media-frequency`);
  const knownStateUrl = computed(() => `vocabulary/${props.wordId}/${currentReadingIndex.value}/known-state`);

  const { data: response, refresh: refreshInfo } = useApiFetch<Word>(infoUrl, { watch: false });
  const { data: mediaFrequency, status: mediaFreqStatus, refresh: refreshMediaFrequency } = useApiFetch<Record<string, number>>(mediaFreqUrl, { lazy: true, watch: false });
  const { data: fetchedKnownStates, refresh: refreshKnownStates } = useApiFetch<KnownState[]>(knownStateUrl, { lazy: true, watch: false });

  const word = ref<Word | null | undefined>(response.value);
  watch(response, (value) => {
    if (value && value.mainReading.readingIndex === currentReadingIndex.value) {
      word.value = value;
    }
  });

  const knownStatesOverride = computed(() => fetchedKnownStates.value ?? word.value?.knownStates ?? undefined);

  const { resolvedGroups } = useDictionaryDefinitions(
    computed(() => word.value?.mainReading?.text),
    computed(() => word.value?.definitions),
  );

  const sortedReadings = computed(() => {
    return word.value?.alternativeReadings.sort((a, b) => b.frequencyPercentage - a.frequencyPercentage) || [];
  });

  const mediaAmountUrl = 'media-deck/decks-count';
  const { data: mediaAmountResponse } = useApiFetch<Record<MediaType, number>>(mediaAmountUrl);

  const totalMediaCount = computed(() => {
    if (!mediaAmountResponse.value) return 0;
    return Object.values(mediaAmountResponse.value).reduce((sum, count) => sum + count, 0);
  });

  const selectedMediaType = ref<MediaType | null>(null);
  const mediaAccordionValue = ref('0');
  const selectMediaType = (type: number | string | null) => {
    if (type != null) {
      selectedMediaType.value = Number(type) as MediaType;
    } else {
      selectedMediaType.value = null;
    }
    mediaAccordionValue.value = '1';
    exampleSentences.value = [];
    canLoadExampleSentences.value = true;
    nextBandMin.value = 0;
    nextBandMax.value = bandSize;
    loadExampleSentences();
  };

  const isTransitioning = ref(false);
  let switchSeq = 0;

  const switchReadingOrWord = async () => {
    const seq = ++switchSeq;
    isTransitioning.value = true;

    await Promise.all([refreshInfo(), refreshKnownStates(), refreshMediaFrequency()]);

    if (seq !== switchSeq) return;

    isTransitioning.value = false;
    selectedMediaType.value = null;
    mediaAccordionValue.value = '0';

    loadCustomSentences();

    exampleSentences.value = [];
    canLoadExampleSentences.value = true;
    nextBandMin.value = 0;
    nextBandMax.value = bandSize;
    loadExampleSentences();
  };

  const selectReading = async (index: number) => {
    emit('readingSelected', index);
    currentReadingIndex.value = index;
    await switchReadingOrWord();
  };

  watch(
    [() => props.wordId, () => props.readingIndex],
    async ([newWordId, newReadingIndex]) => {
      if (currentWordId.value === newWordId && currentReadingIndex.value === newReadingIndex) return;
      currentWordId.value = newWordId;
      currentReadingIndex.value = newReadingIndex;
      await switchReadingOrWord();
    },
    { immediate: false }
  );

  watch(
    () => word.value?.mainReading.text,
    (newText) => {
      emit('mainReadingTextChanged', newText);
    },
    { immediate: true }
  );

  const conjugationString = computed(() => {
    let conjugations = [...props.conjugations];
    conjugations = conjugations.filter((conj) => !conj.startsWith('(')).filter((conj) => conj != '');
    conjugations.reverse();

    if (conjugations.length == 0) return null;

    return conjugations.join(' ; ');
  });

  type SortMode = 'random' | 'easiest' | 'hardest';
  const sortModeOptions = [
    { label: 'Random', value: 'random' as SortMode, icon: 'pi pi-sync' },
    { label: 'Easiest first', value: 'easiest' as SortMode, icon: 'pi pi-arrow-up' },
    { label: 'Hardest first', value: 'hardest' as SortMode, icon: 'pi pi-arrow-down' },
  ];
  const selectedSortMode = ref<SortMode>('random');
  const bandSize = 0.5;

  const customSentences = ref<UserExampleSentenceDto[]>([]);

  async function loadCustomSentences() {
    if (!authStore.isAuthenticated) return;
    try {
      customSentences.value = await $api<UserExampleSentenceDto[]>(
        `user/example-sentences/${props.wordId}/${currentReadingIndex.value}`,
      );
    } catch {
      customSentences.value = [];
    }
  }

  const exampleSentences = ref<ExampleSentence[]>([]);
  const canLoadExampleSentences = ref(true);
  const isLoadingExampleSentences = ref(true);

  const nextBandMin = ref(0);
  const nextBandMax = ref(bandSize);

  const switchSortMode = () => {
    exampleSentences.value = [];
    canLoadExampleSentences.value = true;
    if (selectedSortMode.value === 'hardest') {
      nextBandMin.value = 999;
      nextBandMax.value = 999 + bandSize;
    } else {
      nextBandMin.value = 0;
      nextBandMax.value = bandSize;
    }
    loadExampleSentences();
  };

  onMounted(() => {
    loadCustomSentences();
    loadExampleSentences();
  });

  async function loadExampleSentences() {
    if (selectedSortMode.value === 'random') {
      await getRandomExampleSentences();
    } else {
      await getExampleSentencesByDifficulty();
    }
  }

  async function getRandomExampleSentences() {
    isLoadingExampleSentences.value = true;
    let url = `vocabulary/${props.wordId}/${currentReadingIndex.value}/random-example-sentences`;

    if (selectedMediaType.value != null) {
      url += '/' + selectedMediaType.value;
    }

    const alreadyLoaded = exampleSentences.value.map((sentence) => sentence.sourceDeck.deckId);
    const results = await $api<ExampleSentence[]>(url, {
      method: 'POST',
      body: alreadyLoaded,
    });

    isLoadingExampleSentences.value = false;

    if (results.length == 0) {
      canLoadExampleSentences.value = false;
      return;
    }

    exampleSentences.value.push(...results);
  }

  async function getExampleSentencesByDifficulty() {
    isLoadingExampleSentences.value = true;
    const descending = selectedSortMode.value === 'hardest';
    let url = `vocabulary/${props.wordId}/${currentReadingIndex.value}/example-sentences-by-difficulty`;

    if (selectedMediaType.value != null) {
      url += '/' + selectedMediaType.value;
    }

    const alreadyLoaded = exampleSentences.value.map((sentence) => sentence.sourceDeck.deckId);
    const results = await $api<ExampleSentencesByDifficultyResponse>(
      `${url}?minDifficulty=${nextBandMin.value}&maxDifficulty=${nextBandMax.value}&descending=${descending}`,
      { method: 'POST', body: alreadyLoaded },
    );

    isLoadingExampleSentences.value = false;

    if (results.sentences.length > 0) {
      exampleSentences.value.push(...results.sentences);
    }

    if (descending) {
      nextBandMax.value = results.searchedBandMin;
      nextBandMin.value = nextBandMax.value - bandSize;
      if (nextBandMax.value <= results.minDifficulty) {
        canLoadExampleSentences.value = false;
      }
    } else {
      nextBandMin.value = results.searchedBandMax;
      nextBandMax.value = nextBandMin.value + bandSize;
      if (nextBandMin.value > results.maxDifficulty) {
        canLoadExampleSentences.value = false;
      }
    }
  }
</script>

<template>
  <Card class="p-4 transition-opacity duration-300 ease-in-out" :class="{ 'opacity-60': isTransitioning }">
    <template v-if="word" #content>
      <div class="flex flex-col justify-between md:flex-row">
        <div class="flex flex-col gap-4 max-w-2xl">
          <div class="flex justify-between">
            <div>
              <div v-if="conjugationString != null" class="text-gray-500 text-xs font-noto-sans">(Conjugation: {{ conjugationString }})</div>
              <div class="flex items-center gap-2">
                <NuxtLink v-if="showRedirect" :to="`/vocabulary/${wordId}/${currentReadingIndex}`">
                  <div class="text-3xl font-noto-sans" lang="ja" v-html="convertToRuby(word.mainReading.text)" />
                </NuxtLink>
                <div v-if="!showRedirect" class="text-3xl font-noto-sans" lang="ja" v-html="convertToRuby(word.mainReading.text)" />
                <TtsButton :text="stripRubyMarkup(word.mainReading.text)" :word-id="wordId" :reading-index="currentReadingIndex" size="md" />
              </div>
            </div>
            <div class="flex flex-col md:flex-row items-end md:hidden">
              <div class="text-gray-500 dark:text-gray-300 text-right">Rank #{{ word.mainReading.frequencyRank.toLocaleString() }}</div>
              <VocabularyStatus :word="word" :known-states-override="knownStatesOverride" />
            </div>
          </div>

          <div>
            <h1 class="text-gray-500 dark:text-gray-300 text-sm">Meanings</h1>
            <div class="pl-2">
              <ClientOnly>
                <VocabularyDictionaryDefinitions :resolved-groups="resolvedGroups" :is-compact="false" :current-reading-index="currentReadingIndex" :readings="word.alternativeReadings" />
                <template #fallback>
                  <VocabularyDefinitions :definitions="word.definitions" :is-compact="false" :current-reading-index="currentReadingIndex" :readings="word.alternativeReadings" />
                </template>
              </ClientOnly>
            </div>
          </div>

          <div>
            <h1 class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm">Forms</h1>
            <div class="pl-2 flex flex-row flex-wrap gap-8">
              <span v-for="reading in sortedReadings" :key="reading.readingIndex">
                <div :class="reading.readingIndex === currentReadingIndex ? 'font-bold !text-purple-500' : ' text-blue-500'">
                  <div class="text-center font-noto-sans cursor-pointer hover:underline" lang="ja" @click="selectReading(reading.readingIndex)">
                    {{ reading.text }}
                    <div class="text-xs">({{ formatPercentageApprox(reading.frequencyPercentage) }})</div>
                  </div>
                </div>
              </span>
            </div>
          </div>

          <ClientOnly>
            <div v-if="word.pitchAccents && word.pitchAccents.length > 0" :key="`pitch-${wordId}-${currentReadingIndex}`">
              <h1 class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm">Pitch accents</h1>
              <div class="pl-2 flex flex-row flex-wrap gap-8">
                <span v-for="pitchAccent in word.pitchAccents" :key="pitchAccent">
                  <div>
                    <LazyPitchDiagram :reading="word.mainReading.text" :pitch-accent="pitchAccent" />
                  </div>
                </span>
              </div>
            </div>
          </ClientOnly>

          <KanjiBreakdown :key="`${wordId}-${currentReadingIndex}`" :word-id="wordId" :reading-index="currentReadingIndex" />
        </div>

        <div class="md:min-w-64">
          <div class="text-gray-500 dark:text-gray-300 text-right hidden md:block">
            <VocabularyStatus :word="word" :known-states-override="knownStatesOverride" />
            Rank #{{ word.mainReading.frequencyRank }}
          </div>
          <div class="md:text-right pt-4 cursor-pointer" @click="selectMediaType(null)">
            Appears in <b>{{ word.mainReading.usedInMediaAmount }} media</b>
            ({{ totalMediaCount > 0 ? ((word.mainReading.usedInMediaAmount / totalMediaCount) * 100).toFixed(0) : '0' }}%)
          </div>
          <ClientOnly>
            <div v-if="mediaFreqStatus === 'pending' && !mediaFrequency && word.mainReading.usedInMediaAmount > 0" class="space-y-2 mt-2">
              <div v-for="i in 3" :key="i" class="h-4 w-48 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
            </div>
            <table v-else-if="mediaFrequency && Object.keys(mediaFrequency).length > 0">
              <thead>
                <tr>
                  <th />
                  <th class="text-gray-500 dark:text-gray-300 text-sm pl-4">Amount</th>
                  <th class="text-gray-500 dark:text-gray-300 text-sm pl-4">% of total</th>
                </tr>
              </thead>
              <tr
                v-for="(amount, mediaType) in mediaFrequency"
                :key="mediaType"
                class="cursor-pointer hover:bg-primary-100 dark:hover:bg-primary-900/30 transition-colors"
                @click="selectMediaType(mediaType)"
              >
                <th class="text-right p-0.5 !font-bold">{{ getMediaTypeText(Number(mediaType)) }}</th>
                <th class="text-right p-0.5">{{ amount }}</th>
                <th class="text-right p-0.5">{{ mediaAmountResponse ? ((amount / mediaAmountResponse[mediaType as MediaType]) * 100).toFixed(0) : '0' }}%</th>
              </tr>
            </table>
          </ClientOnly>
        </div>
      </div>

      <WordComposition v-if="word?.composedOf?.length" :components="word.composedOf" />

      <WordUsedIn
        v-if="word?.usedInTotal"
        :key="`usedin-${word.wordId}-${currentReadingIndex}`"
        :word-id="word.wordId"
        :reading-index="currentReadingIndex"
        :initial-items="word.usedIn ?? []"
        :total="word.usedInTotal"
        :highlight="word.mainReading.text"
      />

      <ClientOnly>
        <div v-if="exampleSentences.length > 0 || customSentences.length > 0 || isLoadingExampleSentences || authStore.isAuthenticated">
          <Accordion value="1" lazy>
            <AccordionPanel value="1">
              <AccordionHeader>
                <div class="flex items-center gap-1 cursor-pointer w-full">
                  <span>Example sentences</span>
                  <NuxtLink
                    v-if="authStore.isAuthenticated"
                    :to="`/vocabulary/${props.wordId}/${currentReadingIndex}/custom-sentences`"
                    class="text-surface-400 hover:text-primary-500 transition-colors ml-1"
                    title="Edit custom sentences"
                    @click.stop
                  >
                    <i class="pi pi-pencil text-sm" />
                  </NuxtLink>
                  <Select
                    v-if="exampleSentences.length > 0 || isLoadingExampleSentences"
                    v-model="selectedSortMode"
                    :options="sortModeOptions"
                    option-label="label"
                    option-value="value"
                    variant="filled"
                    class="sort-mode-select"
                    @click.stop
                    @change="switchSortMode()"
                  >
                    <template #value="{ value }">
                      <div class="flex items-center gap-1.5 text-gray-500 dark:text-gray-400">
                        <i :class="sortModeOptions.find(o => o.value === value)?.icon" class="text-xs" />
                        <span>{{ sortModeOptions.find(o => o.value === value)?.label }}</span>
                      </div>
                    </template>
                    <template #option="{ option }">
                      <div class="flex items-center gap-2">
                        <i :class="option.icon" />
                        <span>{{ option.label }}</span>
                      </div>
                    </template>
                  </Select>
                </div>
              </AccordionHeader>
              <AccordionContent>
                <div v-if="exampleSentences.length > 0" class="text-xs pb-2">
                  Quotations belong to their original creators and are presented here for educational purposes only, as per the
                  <NuxtLink :to="`/terms`" target="_blank" class="hover:underline text-primary-600"> terms of service.</NuxtLink>
                </div>
                <template v-if="customSentences.length > 0">
                  <CustomExampleSentenceEntry v-for="sentence in customSentences" :key="`custom-${sentence.userExampleSentenceId}`" :sentence="sentence" />
                  <div v-if="exampleSentences.length > 0" class="border-b border-surface-200 dark:border-surface-700 my-2" />
                </template>
                <template v-if="exampleSentences.length > 0">
                  <ExampleSentenceEntry v-for="(exampleSentence, index) in exampleSentences" :key="index" :example-sentence="exampleSentence" :show-source="true" :word-id="props.wordId" :reading-index="currentReadingIndex" @favourited="loadCustomSentences()" />
                </template>
                <template v-else-if="isLoadingExampleSentences">
                  <div v-for="i in 3" :key="i" class="flex flex-col mb-2">
                    <div class="border-l-4 border-surface-300 dark:border-surface-600 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r">
                      <div class="h-5 w-3/4 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
                    </div>
                    <div class="flex items-center mb-2 ml-4 mt-1">
                      <div class="h-3 w-48 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
                    </div>
                  </div>
                </template>
                <template v-else-if="customSentences.length === 0">
                  <div class="text-sm text-surface-400 py-2">
                    No example sentences for this word.
                    <NuxtLink v-if="authStore.isAuthenticated" :to="`/vocabulary/${props.wordId}/${currentReadingIndex}/custom-sentences`" class="text-primary-500 hover:underline">Add a custom one</NuxtLink>
                  </div>
                </template>
                <Button v-if="exampleSentences.length > 0" @click="loadExampleSentences()" :disabled="!canLoadExampleSentences">Load more</Button>
              </AccordionContent>
            </AccordionPanel>
          </Accordion>
        </div>
      </ClientOnly>

      <Accordion v-if="word.mainReading.usedInMediaAmount > 0" :value="mediaAccordionValue" lazy>
        <AccordionPanel value="1">
          <AccordionHeader>
            <div class="cursor-pointer">
              View the <b>{{ word.mainReading.usedInMediaAmount }}</b> media it appears in
            </div>
          </AccordionHeader>
          <AccordionContent>
            <MediaList :word="word" :default-media-type="selectedMediaType" />
          </AccordionContent>
        </AccordionPanel>
      </Accordion>
    </template>
  </Card>
</template>

<style scoped>
  th {
    font-weight: normal;
  }

  :deep(.sort-mode-select) {
    background: transparent !important;
    border: none !important;
    box-shadow: none !important;
    padding: 0 !important;
    min-width: auto !important;
    font-size: 0.875rem;
  }

  :deep(.sort-mode-select .p-select-label) {
    padding: 0.125rem 0.25rem !important;
  }

  :deep(.sort-mode-select .p-select-dropdown) {
    width: 1.25rem;
    color: var(--p-text-muted-color);
  }

  :deep(.sort-mode-select:hover) {
    background: var(--p-surface-100) !important;
    border-radius: 0.375rem;
  }

  :deep(.dark .sort-mode-select:hover) {
    background: var(--p-surface-800) !important;
  }
</style>
