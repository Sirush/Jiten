<script setup lang="ts">
  import { useApiFetchPaginated } from '~/composables/useApiFetch';
  import { type Deck, MediaType, SortOrder, type Word, DisplayStyle } from '~/types';
  import Skeleton from 'primevue/skeleton';
  import Card from 'primevue/card';
  import InputText from 'primevue/inputtext';
  import { debounce } from 'perfect-debounce';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import MediaDeckCompactView from '~/components/MediaDeckCompactView.vue';
  import MediaDeckTableView from '~/components/MediaDeckTableView.vue';
  import { useAuthStore } from '~/stores/authStore';


  const props = defineProps<{
    word?: Word;
    defaultMediaType?: MediaType | null;
  }>();

  const route = useRoute();
  const router = useRouter();

  watch(
    () => props.defaultMediaType,
    (newVal) => {
      if (newVal !== null && newVal !== undefined) {
        const curr = route.query.mediaType ? Number(Array.isArray(route.query.mediaType) ? route.query.mediaType[0] : route.query.mediaType) : null;
        if (curr !== Number(newVal)) {
          router.replace({
            query: { ...route.query, mediaType: Number(newVal) as any, offset: 0 as any },
          });
        }
      } else if (newVal === null) {
        if (route.query.mediaType) {
          router.replace({
            query: { ...route.query, mediaType: undefined, offset: 0 as any },
          });
        }
      }
    },
    { immediate: true }
  );

  const offset = computed(() => (route.query.offset ? Number(route.query.offset) : 0));
  const mediaType = computed(() => (route.query.mediaType ? route.query.mediaType : null));

  const titleFilter = ref(route.query.title ? (Array.isArray(route.query.title) ? route.query.title[0] : route.query.title) : null);
  const debouncedTitleFilter = ref(titleFilter.value);

  const sortByOptions = ref([
    { label: 'Title', value: 'title' },
    { label: 'Difficulty', value: 'difficulty' },
    { label: 'Subdeck Count', value: 'subdeckCount' },
    { label: 'External Rating', value: 'extRating' },
    { label: 'Unique Kanji', value: 'uKanji' },
    { label: 'Unique Word Count', value: 'uWordCount' },
    { label: 'Word Count', value: 'wordCount' },
    { label: 'Unique Kanji Used Once', value: 'uKanjiOnce' },
    { label: 'Release Date', value: 'releaseDate' },
    { label: 'Added Date', value: 'addedDate' },
  ]);

  const novelSortOptions = ref<{ label: string; value: string }[]>([]);
  const speechSortOptions = ref<{ label: string; value: string }[]>([]);

  const sortByGrouped = computed(() => {
    const groups: { label: string; items: { label: string; value: string }[] }[] = [
      { label: 'General', items: sortByOptions.value },
    ];
    if (novelSortOptions.value.length > 0) {
      groups.push({ label: 'Novel', items: novelSortOptions.value });
    }
    if (speechSortOptions.value.length > 0) {
      groups.push({ label: 'Audio-Video', items: speechSortOptions.value });
    }
    return groups;
  });

  // Array used to reorder the sortByOptions when some options are added or removed to keep the order consistent
  const sortByOrdering = [
    'title',
    'difficulty',
    'coverage',
    'uCoverage',
    'extRating',
    'sentenceLength',
    'uKanji',
    'uWordCount',
    'wordCount',
    'subdeckCount',
    'uKanjiOnce',
    'releaseDate',
    'addedDate',
  ];

  const sortMeta: Record<string, { default: SortOrder; asc: string; desc: string }> = {
    title:               { default: SortOrder.Ascending,  asc: 'A → Z',               desc: 'Z → A' },
    difficulty:          { default: SortOrder.Ascending,  asc: 'Easiest first',        desc: 'Hardest first' },
    coverage:            { default: SortOrder.Descending, asc: 'Lowest first',         desc: 'Highest first' },
    uCoverage:           { default: SortOrder.Descending, asc: 'Lowest first',         desc: 'Highest first' },
    extRating:           { default: SortOrder.Descending, asc: 'Lowest first',         desc: 'Highest first' },
    sentenceLength:      { default: SortOrder.Ascending,  asc: 'Shortest first',       desc: 'Longest first' },
    uKanji:              { default: SortOrder.Ascending,  asc: 'Fewest first',         desc: 'Most first' },
    uWordCount:          { default: SortOrder.Ascending,  asc: 'Fewest first',         desc: 'Most first' },
    wordCount:           { default: SortOrder.Ascending,  asc: 'Fewest first',         desc: 'Most first' },
    subdeckCount:        { default: SortOrder.Ascending,  asc: 'Fewest first',         desc: 'Most first' },
    uKanjiOnce:          { default: SortOrder.Ascending,  asc: 'Fewest first',         desc: 'Most first' },
    releaseDate:         { default: SortOrder.Descending, asc: 'Oldest first',         desc: 'Newest first' },
    addedDate:           { default: SortOrder.Descending, asc: 'Oldest first',         desc: 'Newest first' },
    charCount:           { default: SortOrder.Ascending,  asc: 'Shortest first',       desc: 'Longest first' },
    dialoguePercentage:  { default: SortOrder.Descending, asc: 'Least dialogue',       desc: 'Most dialogue' },
    speechSpeed:         { default: SortOrder.Ascending,  asc: 'Slowest first',        desc: 'Fastest first' },
    speechDuration:      { default: SortOrder.Ascending,  asc: 'Shortest first',       desc: 'Longest first' },
    occurrences:         { default: SortOrder.Descending, asc: 'Fewest first',         desc: 'Most first' },
    filter:              { default: SortOrder.Descending, asc: 'Least relevant',       desc: 'Most relevant' },
  };

  const sortOrderLabel = computed(() => {
    const meta = sortMeta[sortBy.value as string];
    if (!meta) return sortOrder.value === SortOrder.Ascending ? '↑' : '↓';
    return sortOrder.value === SortOrder.Ascending ? meta.asc : meta.desc;
  });

  const statusFilter = ref(route.query.status ? (Array.isArray(route.query.status) ? route.query.status[0] : route.query.status) : 'none');

  const authStore = useAuthStore();
  const isConnected = computed(() => authStore.isAuthenticated);

  const sortBy = ref(route.query.sortBy ? route.query.sortBy : sortByOptions.value[0].value);
  const sortOrder = ref(route.query.sortOrder ? Number(route.query.sortOrder) : (sortMeta[sortBy.value as string]?.default ?? SortOrder.Ascending));
  const wordIdRef = ref(props.word?.wordId);
  const readingIndexRef = ref(props.word?.mainReading?.readingIndex);

  if (isConnected.value) {
    if (!sortByOptions.value.some((o) => o.value === 'uCoverage')) {
      sortByOptions.value.push({ label: 'Unique Coverage', value: 'uCoverage' });
    }
    if (!sortByOptions.value.some((o) => o.value === 'coverage')) {
      sortByOptions.value.push({ label: 'Coverage', value: 'coverage' });
    }
  }

  // Advanced filter state
  const currentYear = new Date().getFullYear();
  const charCountMin = ref<number | null>(toNumOrNull(route.query.charCountMin));
  const charCountMax = ref<number | null>(toNumOrNull(route.query.charCountMax));
  const difficultyMin = ref<number | null>(toNumOrNull(route.query.difficultyMin));
  const difficultyMax = ref<number | null>(toNumOrNull(route.query.difficultyMax));
  const releaseYearMin = ref<number | null>(toNumOrNull(route.query.releaseYearMin));
  const releaseYearMax = ref<number | null>(toNumOrNull(route.query.releaseYearMax));
  const uniqueKanjiMin = ref<number | null>(toNumOrNull(route.query.uniqueKanjiMin));
  const uniqueKanjiMax = ref<number | null>(toNumOrNull(route.query.uniqueKanjiMax));
  const subdeckCountMin = ref<number | null>(toNumOrNull(route.query.subdeckCountMin));
  const subdeckCountMax = ref<number | null>(toNumOrNull(route.query.subdeckCountMax));
  const extRatingMin = ref<number | null>(toNumOrNull(route.query.extRatingMin));
  const extRatingMax = ref<number | null>(toNumOrNull(route.query.extRatingMax));
  const speechSpeedMin = ref<number | null>(toNumOrNull(route.query.speechSpeedMin));
  const speechSpeedMax = ref<number | null>(toNumOrNull(route.query.speechSpeedMax));
  const speechDurationMin = ref<number | null>(toNumOrNull(route.query.speechDurationMin));
  const speechDurationMax = ref<number | null>(toNumOrNull(route.query.speechDurationMax));
  const coverageMin = ref<number | null>(toNumOrNull(route.query.coverageMin));
  const coverageMax = ref<number | null>(toNumOrNull(route.query.coverageMax));
  const uniqueCoverageMin = ref<number | null>(toNumOrNull(route.query.uniqueCoverageMin));
  const uniqueCoverageMax = ref<number | null>(toNumOrNull(route.query.uniqueCoverageMax));
  const excludeSequels = ref<boolean | null>(toBooleanOrNull(route.query.excludeSequels));

  // Genre and Tag filter state
  const includeGenres = ref<number[]>([]);
  const excludeGenres = ref<number[]>([]);
  const includeTags = ref<number[]>([]);
  const excludeTags = ref<number[]>([]);

  includeGenres.value = parseNumberArray(route.query.genres);
  excludeGenres.value = parseNumberArray(route.query.excludeGenres);
  includeTags.value = parseNumberArray(route.query.tags);
  excludeTags.value = parseNumberArray(route.query.excludeTags);

  // Ensure min is not higher than max and values stay within bounds
  const clamp = (val: number, min: number, max: number) => Math.min(max, Math.max(min, val));
  const normalizePair = (minRef: any, maxRef: any, floor: number, ceil: number) => {
    if (minRef.value != null) minRef.value = clamp(minRef.value, floor, ceil);
    if (maxRef.value != null) maxRef.value = clamp(maxRef.value, floor, ceil);
    if (minRef.value != null && maxRef.value != null && minRef.value > maxRef.value) {
      // keep ranges valid: when min surpasses max, move max up to min
      maxRef.value = minRef.value;
    }
  };

  // Normalize ranges when user edits inputs
  watch([charCountMin, charCountMax], () => normalizePair(charCountMin, charCountMax, 0, 20000000));
  watch([difficultyMin, difficultyMax], () => normalizePair(difficultyMin, difficultyMax, 0, 5));
  watch([releaseYearMin, releaseYearMax], () => normalizePair(releaseYearMin, releaseYearMax, 1900, currentYear));
  watch([uniqueKanjiMin, uniqueKanjiMax], () => normalizePair(uniqueKanjiMin, uniqueKanjiMax, 0, 5000));
  watch([subdeckCountMin, subdeckCountMax], () => normalizePair(subdeckCountMin, subdeckCountMax, 0, 2000));
  watch([extRatingMin, extRatingMax], () => normalizePair(extRatingMin, extRatingMax, 0, 2000));
  watch([speechSpeedMin, speechSpeedMax], () => normalizePair(speechSpeedMin, speechSpeedMax, 0, 800));
  watch([speechDurationMin, speechDurationMax], () => normalizePair(speechDurationMin, speechDurationMax, 0, 300));
  watch([coverageMin, coverageMax], () => normalizePair(coverageMin, coverageMax, 0, 100));
  watch([uniqueCoverageMin, uniqueCoverageMax], () => normalizePair(uniqueCoverageMin, uniqueCoverageMax, 0, 100));

  const debouncedFilters = ref({
    charCountMin: charCountMin.value,
    charCountMax: charCountMax.value,
    difficultyMin: difficultyMin.value,
    difficultyMax: difficultyMax.value,
    releaseYearMin: releaseYearMin.value,
    releaseYearMax: releaseYearMax.value,
    uniqueKanjiMin: uniqueKanjiMin.value,
    uniqueKanjiMax: uniqueKanjiMax.value,
    subdeckCountMin: subdeckCountMin.value,
    subdeckCountMax: subdeckCountMax.value,
    extRatingMin: extRatingMin.value,
    extRatingMax: extRatingMax.value,
    speechSpeedMin: speechSpeedMin.value,
    speechSpeedMax: speechSpeedMax.value,
    speechDurationMin: speechDurationMin.value,
    speechDurationMax: speechDurationMax.value,
    coverageMin: coverageMin.value,
    coverageMax: coverageMax.value,
    uniqueCoverageMin: uniqueCoverageMin.value,
    uniqueCoverageMax: uniqueCoverageMax.value,
    includeGenres: includeGenres.value,
    excludeGenres: excludeGenres.value,
    includeTags: includeTags.value,
    excludeTags: excludeTags.value,
    excludeSequels: excludeSequels.value
  });

  const updateFiltersDebounced = debounce(
    () => {
      debouncedFilters.value = {
        charCountMin: charCountMin.value,
        charCountMax: charCountMax.value,
        difficultyMin: difficultyMin.value,
        difficultyMax: difficultyMax.value,
        releaseYearMin: releaseYearMin.value,
        releaseYearMax: releaseYearMax.value,
        uniqueKanjiMin: uniqueKanjiMin.value,
        uniqueKanjiMax: uniqueKanjiMax.value,
        subdeckCountMin: subdeckCountMin.value,
        subdeckCountMax: subdeckCountMax.value,
        extRatingMin: extRatingMin.value,
        extRatingMax: extRatingMax.value,
        speechSpeedMin: speechSpeedMin.value,
        speechSpeedMax: speechSpeedMax.value,
        speechDurationMin: speechDurationMin.value,
        speechDurationMax: speechDurationMax.value,
        coverageMin: coverageMin.value,
        coverageMax: coverageMax.value,
        uniqueCoverageMin: uniqueCoverageMin.value,
        uniqueCoverageMax: uniqueCoverageMax.value,
        includeGenres: includeGenres.value,
        excludeGenres: excludeGenres.value,
        includeTags: includeTags.value,
        excludeTags: excludeTags.value,
        excludeSequels: excludeSequels.value
      };

      const toUndef = (v: number | null) => (v === null ? undefined : v);
      const arrayToString = (arr: number[]) => (arr.length > 0 ? arr.join(',') : undefined);

      router.replace({
        query: {
          ...route.query,
          charCountMin: toUndef(charCountMin.value) as any,
          charCountMax: toUndef(charCountMax.value) as any,
          difficultyMin: toUndef(difficultyMin.value) as any,
          difficultyMax: toUndef(difficultyMax.value) as any,
          releaseYearMin: toUndef(releaseYearMin.value) as any,
          releaseYearMax: toUndef(releaseYearMax.value) as any,
          uniqueKanjiMin: toUndef(uniqueKanjiMin.value) as any,
          uniqueKanjiMax: toUndef(uniqueKanjiMax.value) as any,
          subdeckCountMin: toUndef(subdeckCountMin.value) as any,
          subdeckCountMax: toUndef(subdeckCountMax.value) as any,
          extRatingMin: toUndef(extRatingMin.value) as any,
          extRatingMax: toUndef(extRatingMax.value) as any,
          speechSpeedMin: toUndef(speechSpeedMin.value) as any,
          speechSpeedMax: toUndef(speechSpeedMax.value) as any,
          speechDurationMin: toUndef(speechDurationMin.value) as any,
          speechDurationMax: toUndef(speechDurationMax.value) as any,
          coverageMin: toUndef(coverageMin.value) as any,
          coverageMax: toUndef(coverageMax.value) as any,
          uniqueCoverageMin: toUndef(uniqueCoverageMin.value) as any,
          uniqueCoverageMax: toUndef(uniqueCoverageMax.value) as any,
          genres: arrayToString(includeGenres.value) as any,
          excludeGenres: arrayToString(excludeGenres.value) as any,
          tags: arrayToString(includeTags.value) as any,
          excludeTags: arrayToString(excludeTags.value) as any,
          offset: 0 as any,
          excludeSequels: excludeSequels.value === true ? true : undefined
        },
      });
    },
    500,
    { leading: false }
  );

  watch(
    [charCountMin, charCountMax, difficultyMin, difficultyMax, releaseYearMin, releaseYearMax, uniqueKanjiMin, uniqueKanjiMax, subdeckCountMin, subdeckCountMax, extRatingMin, extRatingMax, speechSpeedMin, speechSpeedMax, speechDurationMin, speechDurationMax, coverageMin, coverageMax, uniqueCoverageMin, uniqueCoverageMax, excludeSequels],
    () => {
      updateFiltersDebounced();
    }
  );

  watch(
    [includeGenres, excludeGenres, includeTags, excludeTags],
    () => {
      updateFiltersDebounced();
    },
    { deep: true }
  );

  watch(
    () => mediaType.value,
    (newMediaType) => {
      updateOptions();
    }
  );

  const updateOptions = () => {
    const showspeechSpeedOptionMediaTypes = [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio];

    if (mediaType.value == null || !showspeechSpeedOptionMediaTypes.includes(Number(mediaType.value))) {
      novelSortOptions.value = [
        { label: 'Character Count', value: 'charCount' },
        { label: 'Dialogue Percentage', value: 'dialoguePercentage' },
      ];
    } else {
      novelSortOptions.value = [];
      if (sortBy.value === 'charCount' || sortBy.value === 'dialoguePercentage') {
        sortBy.value = 'title';
      }
    }

    if (mediaType.value == null || showspeechSpeedOptionMediaTypes.includes(Number(mediaType.value))) {
      speechSortOptions.value = [
        { label: 'Speech Speed', value: 'speechSpeed' },
        { label: 'Speech Duration', value: 'speechDuration' },
      ];
    } else {
      speechSortOptions.value = [];
      if (sortBy.value === 'speechSpeed' || sortBy.value === 'speechDuration') {
        sortBy.value = 'title';
      }
    }

    const showAvgSentenceLengthOptionMediaTypes = [MediaType.Novel, MediaType.VisualNovel, MediaType.WebNovel, MediaType.NonFiction, MediaType.VideoGame];

    if (mediaType.value == null || showAvgSentenceLengthOptionMediaTypes.includes(Number(mediaType.value))) {
      if (!sortByOptions.value.some((o) => o.value === 'sentenceLength')) {
        sortByOptions.value.push({ label: 'Average Sentence Length', value: 'sentenceLength' });
      }
    } else {
      if (sortByOptions.value.some((o) => o.value === 'sentenceLength')) {
        sortByOptions.value = sortByOptions.value.filter((o) => o.value !== 'sentenceLength');
      }
      if (sortBy.value === 'sentenceLength') {
        sortBy.value = 'title';
      }
    }

    // reorder the options by sortByOrdering
    sortByOptions.value.sort((a, b) => {
      const indexA = sortByOrdering.indexOf(a.value);
      const indexB = sortByOrdering.indexOf(b.value);
      return indexA - indexB;
    });
  };

  updateOptions();

  const resetAllFilters = () => {
    // Text filters
    titleFilter.value = null;
    debouncedTitleFilter.value = null;

    // Numeric range filters
    charCountMin.value = null;
    charCountMax.value = null;
    difficultyMin.value = null;
    difficultyMax.value = null;
    releaseYearMin.value = null;
    releaseYearMax.value = null;
    uniqueKanjiMin.value = null;
    uniqueKanjiMax.value = null;
    subdeckCountMin.value = null;
    subdeckCountMax.value = null;
    extRatingMin.value = null;
    extRatingMax.value = null;
    speechSpeedMin.value = null;
    speechSpeedMax.value = null;
    speechDurationMin.value = null;
    speechDurationMax.value = null;
    coverageMin.value = null;
    coverageMax.value = null;
    uniqueCoverageMin.value = null;
    uniqueCoverageMax.value = null;
    excludeSequels.value = false;

    // Genre and tag filters
    includeGenres.value = [];
    excludeGenres.value = [];
    includeTags.value = [];
    excludeTags.value = [];

    // Status filter
    statusFilter.value = 'none';

    // Update URL state
    router.replace({
      query: {
        ...route.query,
        title: undefined,
        charCountMin: undefined,
        charCountMax: undefined,
        difficultyMin: undefined,
        difficultyMax: undefined,
        releaseYearMin: undefined,
        releaseYearMax: undefined,
        uniqueKanjiMin: undefined,
        uniqueKanjiMax: undefined,
        subdeckCountMin: undefined,
        subdeckCountMax: undefined,
        extRatingMin: undefined,
        extRatingMax: undefined,
        speechSpeedMin: undefined,
        speechSpeedMax: undefined,
        speechDurationMin: undefined,
        speechDurationMax: undefined,
        coverageMin: undefined,
        coverageMax: undefined,
        uniqueCoverageMin: undefined,
        uniqueCoverageMax: undefined,
        genres: undefined,
        excludeGenres: undefined,
        tags: undefined,
        excludeTags: undefined,
        status: undefined,
        offset: 0,
        excludeSequels: undefined
      },
    });
  };

  watch(
    () => props.word,
    (newWord) => {
      if (newWord) {
        wordIdRef.value = newWord.wordId;
        readingIndexRef.value = newWord.mainReading?.readingIndex;

        // Reset sorting when word changes
        if (!sortByOptions.value.some((opt) => opt.value === 'occurrences')) {
          sortByOptions.value.unshift({ label: 'Occurrences', value: 'occurrences' });
        }
        sortBy.value = 'occurrences';
        sortOrder.value = SortOrder.Descending;
      }
    },
    { immediate: true, deep: true }
  );

  if (props.word != null) {
    sortByOptions.value.unshift({ label: 'Occurrences', value: 'occurrences' });
    sortBy.value = 'occurrences';
    sortOrder.value = SortOrder.Descending;
  }

  const updateDebounced = debounce(async (newValue: string | null) => {
    debouncedTitleFilter.value = newValue;
    await router.replace({
      query: {
        ...route.query,
        title: newValue || undefined,
        sortBy: 'filter',
        offset: 0,
      },
    });
    sortBy.value = 'filter';
  }, 500);

  watch(titleFilter, (newValue) => {
    updateDebounced(newValue);
  });

  watch(sortOrder, (newValue) => {
    router.replace({
      query: {
        ...route.query,
        sortOrder: newValue,
      },
    });
  });

  watch(sortBy, (newValue) => {
    const meta = sortMeta[newValue as string];
    if (meta) {
      sortOrder.value = meta.default;
    }
    router.replace({
      query: {
        ...route.query,
        sortBy: newValue,
        sortOrder: sortOrder.value,
      },
    });
  });

  watch(statusFilter, (newValue) => {
    router.replace({
      query: {
        ...route.query,
        status: newValue === 'none' ? undefined : newValue,
        offset: 0,
      },
    });
  });

  const url = computed(() => `media-deck/get-media-decks`);

  const {
    data: response,
    status,
    error,
    refresh: refreshMediaList,
  } = useApiFetchPaginated<Deck[]>(url, {
    revalidateOnClient: true,
    query: {
      offset: offset,
      mediaType: mediaType,
      wordId: wordIdRef,
      readingIndex: readingIndexRef,
      titleFilter: debouncedTitleFilter,
      sortBy: sortBy,
      sortOrder: sortOrder,
      status: statusFilter,
      charCountMin: computed(() => debouncedFilters.value.charCountMin),
      charCountMax: computed(() => debouncedFilters.value.charCountMax),
      difficultyMin: computed(() => debouncedFilters.value.difficultyMin),
      difficultyMax: computed(() => debouncedFilters.value.difficultyMax),
      releaseYearMin: computed(() => debouncedFilters.value.releaseYearMin),
      releaseYearMax: computed(() => debouncedFilters.value.releaseYearMax),
      uniqueKanjiMin: computed(() => debouncedFilters.value.uniqueKanjiMin),
      uniqueKanjiMax: computed(() => debouncedFilters.value.uniqueKanjiMax),
      subdeckCountMin: computed(() => debouncedFilters.value.subdeckCountMin),
      subdeckCountMax: computed(() => debouncedFilters.value.subdeckCountMax),
      extRatingMin: computed(() => debouncedFilters.value.extRatingMin),
      extRatingMax: computed(() => debouncedFilters.value.extRatingMax),
      speechSpeedMin: computed(() => debouncedFilters.value.speechSpeedMin),
      speechSpeedMax: computed(() => debouncedFilters.value.speechSpeedMax),
      speechDurationMin: computed(() => debouncedFilters.value.speechDurationMin),
      speechDurationMax: computed(() => debouncedFilters.value.speechDurationMax),
      coverageMin: computed(() => debouncedFilters.value.coverageMin),
      coverageMax: computed(() => debouncedFilters.value.coverageMax),
      uniqueCoverageMin: computed(() => debouncedFilters.value.uniqueCoverageMin),
      uniqueCoverageMax: computed(() => debouncedFilters.value.uniqueCoverageMax),
      genres: computed(() => (debouncedFilters.value.includeGenres.length > 0 ? debouncedFilters.value.includeGenres.join(',') : undefined)),
      excludeGenres: computed(() => (debouncedFilters.value.excludeGenres.length > 0 ? debouncedFilters.value.excludeGenres.join(',') : undefined)),
      tags: computed(() => (debouncedFilters.value.includeTags.length > 0 ? debouncedFilters.value.includeTags.join(',') : undefined)),
      excludeTags: computed(() => (debouncedFilters.value.excludeTags.length > 0 ? debouncedFilters.value.excludeTags.join(',') : undefined)),
      excludeSequels: computed(() => debouncedFilters.value.excludeSequels),
    },
    watch: [offset, mediaType],
  });

  const { start, end, totalItems, previousLink, nextLink } = usePagination(response);

  const jitenStore = useJitenStore();
  watch(() => jitenStore.coverageVersion, () => {
    refreshMediaList();
  });

  const updateDeckInList = (updatedDeck: Deck) => {
    if (response.value?.data) {
      const index = response.value.data.findIndex((d) => d.deckId === updatedDeck.deckId);
      if (index !== -1) {
        const newData = [...response.value.data];
        newData[index] = updatedDeck;
        response.value = { ...response.value, data: newData };
      }
    }
  };

  const displayStyleStore = useDisplayStyleStore();
  const displayStyle = computed(() => displayStyleStore.displayStyle);

  const mediaTypeOptions = [
    { type: null, label: 'All' },
    { type: MediaType.Anime, label: 'Anime' },
    { type: MediaType.Audio, label: 'Audio' },
    { type: MediaType.Drama, label: 'Dramas' },
    { type: MediaType.Manga, label: 'Manga' },
    { type: MediaType.Movie, label: 'Movies' },
    { type: MediaType.NonFiction, label: 'Non-Fiction' },
    { type: MediaType.Novel, label: 'Novels' },
    { type: MediaType.VideoGame, label: 'Video Games' },
    { type: MediaType.VisualNovel, label: 'Visual Novels' },
    { type: MediaType.WebNovel, label: 'Web Novels' },
  ];

  const isActive = (type: MediaType | null) => {
    if (type === null) {
      return !mediaType.value || mediaType.value === '0';
    }
    return Number(mediaType.value) === type;
  };
</script>

<template>
  <div class="flex flex-col gap-4">
    <Card>
      <template #content>
        <div class="flex flex-row flex-wrap justify-around gap-2">
          <NuxtLink
            v-for="option in mediaTypeOptions"
            :key="option.label"
            :to="{ query: option.type ? { ...route.query, mediaType: option.type, offset: 0 } : { ...route.query, mediaType: undefined, offset: 0 } }"
            :class="{ 'font-bold !text-purple-500': isActive(option.type) }"
          >
            {{ option.label }}
          </NuxtLink>
        </div>
      </template>
    </Card>
    <div class="flex flex-col md:flex-row gap-2">
      <div class="flex flex-row gap-2">
        <FloatLabel variant="on" class="w-full">
          <Select
            v-model="sortBy"
            :options="sortByGrouped"
            option-label="label"
            option-value="value"
            option-group-label="label"
            option-group-children="items"
            placeholder="Sort by"
            input-id="sortBy"
            class="w-full md:w-56"
            scroll-height="50vh"
          >
            <template #optiongroup="{ option }">
              <div class="text-xs font-semibold text-surface-500 dark:text-surface-400 py-0.5 px-1">{{ option.label }}</div>
            </template>
          </Select>
          <label for="sortBy">Sort by</label>
        </FloatLabel>
        <Button :icon="sortOrder === SortOrder.Ascending ? 'pi pi-arrow-up' : 'pi pi-arrow-down'" class="!px-4" @click="sortOrder = sortOrder === SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending" />
      </div>

      <IconField class="w-full">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText v-model="titleFilter" type="text" placeholder="Search by title" class="w-full" />
        <InputIcon v-if="titleFilter" class="cursor-pointer" @click="titleFilter = null">
          <Icon name="material-symbols:close" />
        </InputIcon>
      </IconField>

      <!-- Advanced Filters -->
      <MediaListFilters
        v-model:status-filter="statusFilter"
        v-model:char-count-min="charCountMin"
        v-model:char-count-max="charCountMax"
        v-model:difficulty-min="difficultyMin"
        v-model:difficulty-max="difficultyMax"
        v-model:release-year-min="releaseYearMin"
        v-model:release-year-max="releaseYearMax"
        v-model:unique-kanji-min="uniqueKanjiMin"
        v-model:unique-kanji-max="uniqueKanjiMax"
        v-model:subdeck-count-min="subdeckCountMin"
        v-model:subdeck-count-max="subdeckCountMax"
        v-model:ext-rating-min="extRatingMin"
        v-model:ext-rating-max="extRatingMax"
        v-model:speech-speed-min="speechSpeedMin"
        v-model:speech-speed-max="speechSpeedMax"
        v-model:speech-duration-min="speechDurationMin"
        v-model:speech-duration-max="speechDurationMax"
        v-model:coverage-min="coverageMin"
        v-model:coverage-max="coverageMax"
        v-model:unique-coverage-min="uniqueCoverageMin"
        v-model:unique-coverage-max="uniqueCoverageMax"
        v-model:include-genres="includeGenres"
        v-model:exclude-genres="excludeGenres"
        v-model:include-tags="includeTags"
        v-model:exclude-tags="excludeTags"
        v-model:exclude-sequels="excludeSequels"
        :is-connected="isConnected"
        @reset="resetAllFilters"
      />

      <div class="flex flex-row gap-2 items-center">
        <DisplayStyleSelector />
      </div>
    </div>
    <div>
      <div class="flex flex-col gap-1">
        <PaginationControls v-if="response?.data?.length" :previous-link="previousLink" :next-link="nextLink" :start="start" :end="end" :total-items="totalItems" item-label="decks" />

        <div v-if="status === 'pending'" class="flex flex-col gap-4">
          <Card v-for="i in 5" :key="i" class="p-2">
            <template #content>
              <Skeleton width="100%" height="250px" />
            </template>
          </Card>
        </div>

        <div v-else-if="error">Error: {{ error }}</div>

        <div v-else-if="!response?.data?.length" class="flex flex-col items-center justify-center py-16">
          <i class="pi pi-search text-4xl text-primary-500 mb-4" />
          <p class="text-lg font-medium text-primary-700 dark:text-primary-300">No decks found</p>
          <p class="text-sm text-surface-400">Try adjusting your search or filters</p>
        </div>

        <!-- Card View -->
        <div v-else-if="displayStyle === DisplayStyle.Card" class="flex flex-col gap-2">
          <MediaDeckCard v-for="deck in response.data" :key="deck.deckId" :deck="deck" @update:deck="updateDeckInList" />
        </div>

        <!-- Compact View -->
        <div v-else-if="displayStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center">
          <MediaDeckCompactView v-for="deck in response.data" :key="deck.id" :deck="deck" />
        </div>

        <!-- Table View -->
        <div v-else-if="displayStyle === DisplayStyle.Table" class="flex flex-col gap-0.5">
          <MediaDeckTableView v-for="deck in response.data" :key="deck.id" :deck="deck" />
        </div>
      </div>
      <PaginationControls v-if="response?.data?.length" :previous-link="previousLink" :next-link="nextLink" :start="start" :end="end" :total-items="totalItems" :show-summary="false" :scroll-to-top-on-next="true" />
    </div>
  </div>
</template>

<style scoped></style>
