<script setup lang="ts">
  import { DeckStatus, KnownState, type ExampleSentence, type MediaType, type ReadableSentenceSort, type ReadableSentencesCursor, type ReadableSentencesResponse } from '~/types';
  import ExampleSentenceEntry from '~/components/ExampleSentenceEntry.vue';

  const props = defineProps<{
    wordId: number;
    readingIndex: number;
    atLimit: boolean;
    savedTexts: string[];
    knownStates?: KnownState[];
  }>();

  const emit = defineEmits<{ favourited: [] }>();

  const { $api } = useNuxtApp();
  const { hasFeature } = useJitenPlus();
  const unlocked = computed(() => hasFeature('readable-sentences'));

  // Same rule as the server's i+1 check: a word counts as known from its first successful review.
  const knownStatesForReading = [KnownState.Young, KnownState.Mature, KnownState.Mastered, KnownState.Redundant, KnownState.Blacklisted];
  const wordKnown = computed(() => props.knownStates?.some((s) => knownStatesForReading.includes(s)) ?? false);
  const offset = computed(() => (wordKnown.value ? 0 : 1));
  const iPlus = (otherUnknown: number) => `i+${otherUnknown + offset.value}`;

  const toleranceOptions = computed(() => [0, 1, 2].map((value) => ({ label: iPlus(value), value })));

  const sortOptions: { label: string; value: ReadableSentenceSort }[] = [
    { label: 'Random', value: 'Random' },
    { label: 'Easiest first', value: 'EasiestFirst' },
    { label: 'Hardest first', value: 'HardestFirst' },
  ];

  const mediaTypeOptions = computed(() =>
    getListedMediaTypes().map((mediaType) => ({ label: getMediaTypeText(mediaType), value: mediaType }))
  );

  const unknown = ref(0);
  const mediaTypes = ref<MediaType[]>([]);
  const sort = ref<ReadableSentenceSort>('Random');
  const statuses = ref<DeckStatus[]>([]);

  const statusOptions = [
    { label: 'Planning', value: DeckStatus.Planning },
    { label: 'Ongoing', value: DeckStatus.Ongoing },
    { label: 'Paused', value: DeckStatus.Paused },
    { label: 'Completed', value: DeckStatus.Completed },
  ];

  const sentences = ref<ExampleSentence[]>([]);
  const next = ref<ReadableSentencesCursor | null>(null);
  const finished = ref(false);
  const loading = ref(false);
  const error = ref<string | null>(null);

  // A page can come back empty while unchecked sentences remain; a few are fetched in a row before asking.
  const AUTO_CONTINUE_PAGES = 4;

  let seed = newSeed();
  let generation = 0;

  function newSeed() {
    return Math.floor(Math.random() * 2 ** 31);
  }

  async function fetchPages() {
    const current = generation;
    loading.value = true;
    error.value = null;
    try {
      for (let page = 0; page < AUTO_CONTINUE_PAGES; page++) {
        const response = await $api<ReadableSentencesResponse>(`vocabulary/${props.wordId}/${props.readingIndex}/readable-sentences`, {
          method: 'POST',
          body: {
            unknown: unknown.value,
            mediaTypes: mediaTypes.value,
            statuses: statuses.value,
            sort: sort.value,
            seed,
            cursor: next.value,
          },
        });
        if (current !== generation) return;

        sentences.value.push(...response.sentences);
        next.value = response.next;
        finished.value = response.next === null;
        if (response.sentences.length > 0 || finished.value) break;
      }
    } catch (e) {
      if (current !== generation) return;
      error.value = apiStatusCode(e) === 429 ? 'Too many searches. Try again in a minute.' : "Couldn't load sentences.";
    } finally {
      if (current === generation) loading.value = false;
    }
  }

  function restart() {
    generation++;
    seed = newSeed();
    sentences.value = [];
    next.value = null;
    finished.value = false;
    fetchPages();
  }

  const emptyMessage = computed(() => {
    if (statuses.value.length > 0) return 'None of your media with those statuses has a sentence like that yet.';
    if (unknown.value === 0) return `No sentence with this word is fully readable for you yet. Try ${iPlus(1)}.`;
    return `No sentences with this word have exactly ${unknown.value === 1 ? 'one other word' : 'two other words'} you don't know.`;
  });

  onMounted(() => {
    if (unlocked.value) restart();
  });

  watch(unlocked, (value) => {
    if (value && sentences.value.length === 0 && !loading.value) restart();
  });

  watch(
    () => `${props.wordId}-${props.readingIndex}`,
    () => {
      if (unlocked.value) restart();
    }
  );
</script>

<template>
  <p class="text-sm text-surface-600 dark:text-surface-300 mb-3">
    <template v-if="wordKnown">
      You already know this word, so an <b>i+0</b> sentence is one you can read fully. <b>i+1</b> and <b>i+2</b> have one or two words you don't know yet,
      highlighted in blue.
    </template>
    <template v-else>
      An <b>i+1</b> sentence is one where you already know every word except this one. <b>i+2</b> and <b>i+3</b> have one or two more words you don't know yet,
      highlighted in blue.
    </template>
    Names, particles and auxiliaries are not counted.
  </p>
  <div v-if="unlocked">
    <div class="flex flex-wrap items-center gap-2 mb-3">
      <SelectButton
        v-model="unknown"
        :options="toleranceOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Unknown words"
        @change="restart"
      />
      <MultiSelect
        v-model="mediaTypes"
        :options="mediaTypeOptions"
        option-label="label"
        option-value="value"
        placeholder="All media"
        :show-toggle-all="false"
        :max-selected-labels="2"
        selected-items-label="{0} media types"
        size="small"
        aria-label="Media type"
        @change="restart"
      />
      <Select v-model="sort" :options="sortOptions" option-label="label" option-value="value" size="small" aria-label="Order" @change="restart" />
      <MultiSelect
        v-model="statuses"
        :options="statusOptions"
        option-label="label"
        option-value="value"
        placeholder="Any media"
        :show-toggle-all="false"
        :max-selected-labels="3"
        size="small"
        aria-label="Only media I marked as"
        @change="restart"
      />
    </div>

    <ExampleSentenceEntry
      v-for="sentence in sentences"
      :key="sentence.sentenceId"
      :example-sentence="sentence"
      :show-source="true"
      :word-id="wordId"
      :reading-index="readingIndex"
      :at-limit="atLimit"
      :saved-texts="savedTexts"
      :target-known="wordKnown"
      @favourited="emit('favourited')"
    />

    <template v-if="loading">
      <div v-for="i in sentences.length === 0 ? 3 : 1" :key="i" class="flex flex-col mb-2">
        <div class="border-l-4 border-surface-300 dark:border-surface-600 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r">
          <div class="h-5 w-3/4 bg-surface-200 dark:bg-surface-700 rounded animate-pulse" />
        </div>
      </div>
    </template>

    <div v-else-if="error" class="flex items-center gap-3 text-sm py-2">
      <span class="text-red-600 dark:text-red-400">{{ error }}</span>
      <Button label="Retry" size="small" severity="secondary" text @click="fetchPages" />
    </div>

    <template v-else>
      <p v-if="finished && sentences.length === 0" class="text-sm text-surface-500 dark:text-surface-400 py-2">{{ emptyMessage }}</p>
      <div v-else-if="next && sentences.length === 0" class="flex flex-wrap items-center gap-3 text-sm py-2">
        <span class="text-surface-500 dark:text-surface-400">Nothing yet in the sentences checked so far.</span>
        <Button label="Keep searching" size="small" severity="secondary" @click="fetchPages" />
      </div>
      <Button v-else-if="next" label="Load more" icon="pi pi-plus" size="small" severity="secondary" text @click="fetchPages" />
    </template>
  </div>

  <div v-else>
    <JitenPlusGate feature="readable-sentences" feature-label="Sentences you can read">
      <div class="space-y-2 py-1">
        <div class="h-8 w-2/3 bg-surface-200 dark:bg-surface-700 rounded" />
        <div v-for="i in 3" :key="i" class="border-l-4 border-primary-300 pl-5 pr-3 py-3 bg-gray-50 dark:bg-gray-900 rounded-r">
          <div class="h-5 bg-surface-200 dark:bg-surface-700 rounded" :class="i === 2 ? 'w-1/2' : 'w-3/4'" />
        </div>
      </div>
    </JitenPlusGate>
    <p class="text-sm text-surface-600 dark:text-surface-300 mt-3">
      Every sentence with this word that you can already read, filtered by media type or by the media you're watching and reading. Favourite the ones you like to use
      them on your cards. Requires an account and an active Jiten+ subscription.
    </p>
  </div>
</template>
