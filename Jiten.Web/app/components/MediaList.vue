<script setup lang="ts">
  import { useApiFetch, useApiFetchPaginated } from '~/composables/useApiFetch';
  import { readDescribeQuery } from '~/utils/describeQuery';
  import type { DescriptionSearchResponse } from '~/types/types';
  import { type Deck, MediaType, SortOrder, type Word, DisplayStyle } from '~/types';
  import Skeleton from 'primevue/skeleton';
  import Card from 'primevue/card';
  import InputText from 'primevue/inputtext';
  import { debounce } from 'perfect-debounce';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import { useAuthStore } from '~/stores/authStore';
  import { LazyHydrateMediaDeckCard, LazyHydrateMediaDeckCompactView, LazyHydrateMediaDeckTableView } from '~/utils/lazyHydratedComponents';
  import { type DeckSortOption, deckSortMeta, deckSortOption, deckSortOrdering, deckSortLabels } from '~/utils/deckSorting';
  import {
    MEDIA_FILTER_PRESETS_ENDPOINT,
    type MediaFilterPreset,
    type MediaFilterPresetsPayload,
    PRESET_QUERY_KEYS,
    type PresetQuery,
    buildPresetQuery,
    capturePresetQuery,
    parsePresetsResponse,
    presetQueryEquals,
    resolveDefaultPreset,
    toPresetsPayload,
  } from '~/utils/mediaFilterPresets';
  import { useToast } from 'primevue/usetoast';

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

  const describeQuery = computed(() => readDescribeQuery(route.query.describe));
  const isDescribeMode = computed(() => describeQuery.value !== null);
  const titleFilter = ref(describeQuery.value ?? (route.query.title ? (Array.isArray(route.query.title) ? route.query.title[0] : route.query.title) : null));
  const debouncedTitleFilter = ref(titleFilter.value);

  const sortByOptions = ref(
    [
      'popularity',
      'title',
      'difficulty',
      'subdeckCount',
      'extRating',
      'uKanji',
      'uWordCount',
      'wordCount',
      'uKanjiOnce',
      'communityVotes',
      'releaseDate',
      'addedDate',
    ].map(deckSortOption)
  );

  const novelSortOptions = ref<DeckSortOption[]>([]);
  const speechSortOptions = ref<DeckSortOption[]>([]);

  const sortByGrouped = computed(() => {
    const groups: { label: string; items: DeckSortOption[] }[] = [{ label: 'General', items: sortByOptions.value }];
    if (novelSortOptions.value.length > 0) {
      groups.push({ label: 'Novel', items: novelSortOptions.value });
    }
    if (speechSortOptions.value.length > 0) {
      groups.push({ label: 'Audio-Video', items: speechSortOptions.value });
    }
    return groups;
  });

  const sortOrderLabel = computed(() => {
    const meta = deckSortMeta[sortBy.value as string];
    if (!meta) return sortOrder.value === SortOrder.Ascending ? '↑' : '↓';
    return sortOrder.value === SortOrder.Ascending ? meta.asc : meta.desc;
  });

  // Legacy URLs carried status=fav before favourite became its own flag.
  const rawStatus = Array.isArray(route.query.status) ? route.query.status[0] : route.query.status;
  const statusFilter = ref(rawStatus && rawStatus !== 'fav' ? rawStatus : 'none');

  const authStore = useAuthStore();
  const isConnected = computed(() => authStore.isAuthenticated);

  const sortBy = ref(route.query.sortBy ? route.query.sortBy : sortByOptions.value[0].value);
  const sortOrder = ref(route.query.sortOrder ? Number(route.query.sortOrder) : (deckSortMeta[sortBy.value as string]?.default ?? SortOrder.Ascending));
  const wordIdRef = ref(props.word?.wordId);
  const readingIndexRef = ref(props.word?.mainReading?.readingIndex);

  if (isConnected.value) {
    for (const key of ['uCoverage', 'coverage', 'uTotalCoverage', 'totalCoverage']) {
      if (!sortByOptions.value.some((o) => o.value === key)) {
        sortByOptions.value.push(deckSortOption(key));
      }
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
  const runtimeMin = ref<number | null>(toNumOrNull(route.query.runtimeMin));
  const runtimeMax = ref<number | null>(toNumOrNull(route.query.runtimeMax));
  const coverageMin = ref<number | null>(toNumOrNull(route.query.coverageMin));
  const coverageMax = ref<number | null>(toNumOrNull(route.query.coverageMax));
  const uniqueCoverageMin = ref<number | null>(toNumOrNull(route.query.uniqueCoverageMin));
  const uniqueCoverageMax = ref<number | null>(toNumOrNull(route.query.uniqueCoverageMax));
  const totalCoverageMin = ref<number | null>(toNumOrNull(route.query.totalCoverageMin));
  const totalCoverageMax = ref<number | null>(toNumOrNull(route.query.totalCoverageMax));
  const uTotalCoverageMin = ref<number | null>(toNumOrNull(route.query.uTotalCoverageMin));
  const uTotalCoverageMax = ref<number | null>(toNumOrNull(route.query.uTotalCoverageMax));
  const excludeSequels = ref<boolean | null>(toBooleanOrNull(route.query.excludeSequels));
  const favourite = ref<boolean | null>(rawStatus === 'fav' ? true : toBooleanOrNull(route.query.favourite));

  // Genre and Tag filter state
  const includeGenres = ref<number[]>([]);
  const excludeGenres = ref<number[]>([]);
  const includeTags = ref<number[]>([]);
  const excludeTags = ref<number[]>([]);

  includeGenres.value = parseNumberArray(route.query.genres);
  excludeGenres.value = parseNumberArray(route.query.excludeGenres);
  includeTags.value = parseNumberArray(route.query.tags);
  excludeTags.value = parseNumberArray(route.query.excludeTags);

  const normalizePair = (minRef: any, maxRef: any, floor: number, ceil: number) => {
    const { min, max } = clampRange(minRef.value, maxRef.value, floor, ceil);
    minRef.value = min;
    maxRef.value = max;
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
  watch([runtimeMin, runtimeMax], () => normalizePair(runtimeMin, runtimeMax, 0, 240));
  watch([coverageMin, coverageMax], () => normalizePair(coverageMin, coverageMax, 0, 100));
  watch([uniqueCoverageMin, uniqueCoverageMax], () => normalizePair(uniqueCoverageMin, uniqueCoverageMax, 0, 100));
  watch([totalCoverageMin, totalCoverageMax], () => normalizePair(totalCoverageMin, totalCoverageMax, 0, 100));
  watch([uTotalCoverageMin, uTotalCoverageMax], () => normalizePair(uTotalCoverageMin, uTotalCoverageMax, 0, 100));

  const snapshotFilters = () => ({
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
    runtimeMin: runtimeMin.value,
    runtimeMax: runtimeMax.value,
    coverageMin: coverageMin.value,
    coverageMax: coverageMax.value,
    uniqueCoverageMin: uniqueCoverageMin.value,
    uniqueCoverageMax: uniqueCoverageMax.value,
    totalCoverageMin: totalCoverageMin.value,
    totalCoverageMax: totalCoverageMax.value,
    uTotalCoverageMin: uTotalCoverageMin.value,
    uTotalCoverageMax: uTotalCoverageMax.value,
    includeGenres: includeGenres.value,
    excludeGenres: excludeGenres.value,
    includeTags: includeTags.value,
    excludeTags: excludeTags.value,
    excludeSequels: excludeSequels.value,
    favourite: favourite.value,
  });

  const debouncedFilters = ref(snapshotFilters());

  // Set while a preset writes every filter ref at once: the URL is then rewritten by a single
  // replace, and the sort watchers must not overwrite the preset's own sort order on the way.
  const applyingPreset = ref(false);

  const updateFiltersDebounced = debounce(
    () => {
      debouncedFilters.value = snapshotFilters();
      trackEvent('filter_used', { media: mediaType.value == null ? 'all' : (MediaType[Number(mediaType.value)] ?? String(mediaType.value)) });

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
          runtimeMin: toUndef(runtimeMin.value) as any,
          runtimeMax: toUndef(runtimeMax.value) as any,
          coverageMin: toUndef(coverageMin.value) as any,
          coverageMax: toUndef(coverageMax.value) as any,
          uniqueCoverageMin: toUndef(uniqueCoverageMin.value) as any,
          uniqueCoverageMax: toUndef(uniqueCoverageMax.value) as any,
          totalCoverageMin: toUndef(totalCoverageMin.value) as any,
          totalCoverageMax: toUndef(totalCoverageMax.value) as any,
          uTotalCoverageMin: toUndef(uTotalCoverageMin.value) as any,
          uTotalCoverageMax: toUndef(uTotalCoverageMax.value) as any,
          genres: arrayToString(includeGenres.value) as any,
          excludeGenres: arrayToString(excludeGenres.value) as any,
          tags: arrayToString(includeTags.value) as any,
          excludeTags: arrayToString(excludeTags.value) as any,
          offset: 0 as any,
          excludeSequels: excludeSequels.value === true ? true : undefined,
          favourite: favourite.value === true ? true : undefined,
        },
      });
    },
    500,
    { leading: false }
  );

  watch(
    [
      charCountMin,
      charCountMax,
      difficultyMin,
      difficultyMax,
      releaseYearMin,
      releaseYearMax,
      uniqueKanjiMin,
      uniqueKanjiMax,
      subdeckCountMin,
      subdeckCountMax,
      extRatingMin,
      extRatingMax,
      speechSpeedMin,
      speechSpeedMax,
      speechDurationMin,
      speechDurationMax,
      runtimeMin,
      runtimeMax,
      coverageMin,
      coverageMax,
      uniqueCoverageMin,
      uniqueCoverageMax,
      totalCoverageMin,
      totalCoverageMax,
      uTotalCoverageMin,
      uTotalCoverageMax,
      excludeSequels,
      favourite,
    ],
    () => {
      if (applyingPreset.value) return;
      updateFiltersDebounced();
    }
  );

  watch(
    [includeGenres, excludeGenres, includeTags, excludeTags],
    () => {
      if (applyingPreset.value) return;
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
    const showspeechSpeedOptionMediaTypes = [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube];

    if (mediaType.value == null || !showspeechSpeedOptionMediaTypes.includes(Number(mediaType.value))) {
      novelSortOptions.value = ['charCount', 'dialoguePercentage'].map(deckSortOption);
    } else {
      novelSortOptions.value = [];
      if (sortBy.value === 'charCount' || sortBy.value === 'dialoguePercentage') {
        sortBy.value = 'popularity';
      }
    }

    if (mediaType.value == null || showspeechSpeedOptionMediaTypes.includes(Number(mediaType.value))) {
      speechSortOptions.value = ['speechSpeed', 'speechDuration'].map(deckSortOption);
    } else {
      speechSortOptions.value = [];
      if (sortBy.value === 'speechSpeed' || sortBy.value === 'speechDuration') {
        sortBy.value = 'popularity';
      }
    }

    const showAvgSentenceLengthOptionMediaTypes = [MediaType.Novel, MediaType.VisualNovel, MediaType.WebNovel, MediaType.NonFiction, MediaType.VideoGame];

    if (mediaType.value == null || showAvgSentenceLengthOptionMediaTypes.includes(Number(mediaType.value))) {
      if (!sortByOptions.value.some((o) => o.value === 'sentenceLength')) {
        sortByOptions.value.push(deckSortOption('sentenceLength'));
      }
    } else {
      if (sortByOptions.value.some((o) => o.value === 'sentenceLength')) {
        sortByOptions.value = sortByOptions.value.filter((o) => o.value !== 'sentenceLength');
      }
      if (sortBy.value === 'sentenceLength') {
        sortBy.value = 'popularity';
      }
    }

    sortByOptions.value.sort((a, b) => deckSortOrdering.indexOf(a.value) - deckSortOrdering.indexOf(b.value));
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
    runtimeMin.value = null;
    runtimeMax.value = null;
    coverageMin.value = null;
    coverageMax.value = null;
    uniqueCoverageMin.value = null;
    uniqueCoverageMax.value = null;
    totalCoverageMin.value = null;
    totalCoverageMax.value = null;
    uTotalCoverageMin.value = null;
    uTotalCoverageMax.value = null;
    excludeSequels.value = false;
    favourite.value = false;

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
        runtimeMin: undefined,
        runtimeMax: undefined,
        coverageMin: undefined,
        coverageMax: undefined,
        uniqueCoverageMin: undefined,
        uniqueCoverageMax: undefined,
        totalCoverageMin: undefined,
        totalCoverageMax: undefined,
        uTotalCoverageMin: undefined,
        uTotalCoverageMax: undefined,
        genres: undefined,
        excludeGenres: undefined,
        tags: undefined,
        excludeTags: undefined,
        status: undefined,
        offset: 0,
        excludeSequels: undefined,
        favourite: undefined,
      },
    });
  };

  const presets = ref<MediaFilterPreset[]>([]);
  const defaultPresetName = ref<string | null>(null);
  const presetsLoaded = ref(false);

  const currentPresetQuery = computed<PresetQuery>(() =>
    capturePresetQuery({
      mediaType: mediaType.value,
      title: titleFilter.value,
      sortBy: sortBy.value,
      sortOrder: sortOrder.value,
      status: statusFilter.value === 'none' ? null : statusFilter.value,
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
      runtimeMin: runtimeMin.value,
      runtimeMax: runtimeMax.value,
      coverageMin: coverageMin.value,
      coverageMax: coverageMax.value,
      uniqueCoverageMin: uniqueCoverageMin.value,
      uniqueCoverageMax: uniqueCoverageMax.value,
      totalCoverageMin: totalCoverageMin.value,
      totalCoverageMax: totalCoverageMax.value,
      uTotalCoverageMin: uTotalCoverageMin.value,
      uTotalCoverageMax: uTotalCoverageMax.value,
      genres: includeGenres.value.join(','),
      excludeGenres: excludeGenres.value.join(','),
      tags: includeTags.value.join(','),
      excludeTags: excludeTags.value.join(','),
      excludeSequels: excludeSequels.value === true ? 'true' : null,
      favourite: favourite.value === true ? 'true' : null,
    })
  );

  const captureCurrentFilters = (): PresetQuery => ({ ...currentPresetQuery.value });

  // In an embed the host page owns the media tab, so a preset counts as applied whatever the tab.
  const presetMatchIgnores = computed<readonly string[]>(() => (props.word != null ? ['mediaType'] : []));
  const activePresetName = computed(
    () => presets.value.find((preset) => presetQueryEquals(preset.query, currentPresetQuery.value, presetMatchIgnores.value))?.name ?? null
  );

  const applyPreset = (preset: MediaFilterPreset) => {
    const query = preset.query;
    applyingPreset.value = true;

    titleFilter.value = query.title ?? null;
    debouncedTitleFilter.value = titleFilter.value;
    // Presets saved before the split may still carry status=fav.
    statusFilter.value = query.status && query.status !== 'fav' ? query.status : 'none';
    favourite.value = query.status === 'fav' ? true : toBooleanOrNull(query.favourite);
    sortBy.value = query.sortBy ?? sortByOptions.value[0].value;
    sortOrder.value = query.sortOrder != null ? Number(query.sortOrder) : (deckSortMeta[sortBy.value as string]?.default ?? SortOrder.Ascending);

    charCountMin.value = toNumOrNull(query.charCountMin);
    charCountMax.value = toNumOrNull(query.charCountMax);
    difficultyMin.value = toNumOrNull(query.difficultyMin);
    difficultyMax.value = toNumOrNull(query.difficultyMax);
    releaseYearMin.value = toNumOrNull(query.releaseYearMin);
    releaseYearMax.value = toNumOrNull(query.releaseYearMax);
    uniqueKanjiMin.value = toNumOrNull(query.uniqueKanjiMin);
    uniqueKanjiMax.value = toNumOrNull(query.uniqueKanjiMax);
    subdeckCountMin.value = toNumOrNull(query.subdeckCountMin);
    subdeckCountMax.value = toNumOrNull(query.subdeckCountMax);
    extRatingMin.value = toNumOrNull(query.extRatingMin);
    extRatingMax.value = toNumOrNull(query.extRatingMax);
    speechSpeedMin.value = toNumOrNull(query.speechSpeedMin);
    speechSpeedMax.value = toNumOrNull(query.speechSpeedMax);
    speechDurationMin.value = toNumOrNull(query.speechDurationMin);
    speechDurationMax.value = toNumOrNull(query.speechDurationMax);
    runtimeMin.value = toNumOrNull(query.runtimeMin);
    runtimeMax.value = toNumOrNull(query.runtimeMax);
    coverageMin.value = toNumOrNull(query.coverageMin);
    coverageMax.value = toNumOrNull(query.coverageMax);
    uniqueCoverageMin.value = toNumOrNull(query.uniqueCoverageMin);
    uniqueCoverageMax.value = toNumOrNull(query.uniqueCoverageMax);
    totalCoverageMin.value = toNumOrNull(query.totalCoverageMin);
    totalCoverageMax.value = toNumOrNull(query.totalCoverageMax);
    uTotalCoverageMin.value = toNumOrNull(query.uTotalCoverageMin);
    uTotalCoverageMax.value = toNumOrNull(query.uTotalCoverageMax);
    excludeSequels.value = toBooleanOrNull(query.excludeSequels);
    includeGenres.value = parseNumberArray(query.genres);
    excludeGenres.value = parseNumberArray(query.excludeGenres);
    includeTags.value = parseNumberArray(query.tags);
    excludeTags.value = parseNumberArray(query.excludeTags);

    debouncedFilters.value = snapshotFilters();

    nextTick(() => {
      applyingPreset.value = false;
    });
    router.replace({ query: buildPresetQuery(route.query, preset) });
  };

  const { $api } = useNuxtApp();
  const toast = useToast();

  const presetsPayload = (): MediaFilterPresetsPayload => toPresetsPayload({ presets: presets.value, defaultName: defaultPresetName.value });

  let persistedSnapshot = '';

  // In an embed the URL is the host page's; the embed itself writes mediaType/offset on mount, so
  // only the presence of actual filter keys means the viewer already chose a view.
  const urlHasFilterState = () =>
    props.word == null ? Object.keys(route.query).length > 0 : PRESET_QUERY_KEYS.some((key) => key !== 'mediaType' && route.query[key] != null);

  const loadPresets = async () => {
    const canApplyDefault = (props.word != null || props.defaultMediaType == null) && !urlHasFilterState();

    try {
      const state = parsePresetsResponse(await $api(MEDIA_FILTER_PRESETS_ENDPOINT));
      presets.value = state.presets;
      defaultPresetName.value = state.defaultName;
      persistedSnapshot = JSON.stringify(presetsPayload());
      presetsLoaded.value = true;
    } catch {
      return;
    }

    if (!canApplyDefault || urlHasFilterState()) return;
    const preset = resolveDefaultPreset(presets.value, defaultPresetName.value);
    if (!preset) return;
    if (props.word != null && props.defaultMediaType != null) {
      // An explicitly chosen media type on the host page wins over the preset's tab.
      applyPreset({ ...preset, query: { ...preset.query, mediaType: String(props.defaultMediaType) } });
      return;
    }
    applyPreset(preset);
  };

  const persistPresets = async (payload: MediaFilterPresetsPayload) => {
    try {
      await $api(MEDIA_FILTER_PRESETS_ENDPOINT, { method: 'PUT', body: payload });
    } catch {
      toast.add({
        severity: 'warn',
        summary: 'Presets not saved',
        detail: 'Your presets could not be saved. Check your connection and try again.',
        life: 4000,
      });
    }
  };

  onMounted(() => {
    if (isConnected.value) void loadPresets();
  });

  watch(isConnected, (connected) => {
    if (connected) {
      if (!presetsLoaded.value) void loadPresets();
      return;
    }
    presetsLoaded.value = false;
    presets.value = [];
    defaultPresetName.value = null;
    persistedSnapshot = '';
  });

  watch(
    [presets, defaultPresetName],
    () => {
      if (!presetsLoaded.value) return;
      const payload = presetsPayload();
      const snapshot = JSON.stringify(payload);
      if (snapshot === persistedSnapshot) return;
      persistedSnapshot = snapshot;
      void persistPresets(payload);
    },
    { deep: true }
  );

  watch(
    () => props.word,
    (newWord) => {
      if (newWord) {
        wordIdRef.value = newWord.wordId;
        readingIndexRef.value = newWord.mainReading?.readingIndex;

        // Reset sorting when word changes
        if (!sortByOptions.value.some((opt) => opt.value === 'occurrences')) {
          sortByOptions.value.unshift(deckSortOption('occurrences'));
        }
        sortBy.value = 'occurrences';
        sortOrder.value = SortOrder.Descending;
      }
    },
    { immediate: true, deep: true }
  );

  const updateDebounced = debounce(async (newValue: string | null) => {
    if (newValue !== titleFilter.value) return;
    debouncedTitleFilter.value = newValue;
    await router.replace({
      query: {
        ...route.query,
        title: newValue || undefined,
        describe: undefined,
        sortBy: 'filter',
        offset: 0,
      },
    });
    sortBy.value = 'filter';
  }, 500);

  const searchTitlesInstead = async () => {
    const text = describeQuery.value;
    if (!text) return;
    await router.replace({ query: { ...route.query, describe: undefined, title: text, offset: 0 } });
  };

  const searchDescriptionsInstead = async () => {
    const text = debouncedTitleFilter.value;
    if (!text) return;
    await router.replace({ query: { ...route.query, describe: text, title: undefined, offset: 0 } });
  };

  watch(titleFilter, (newValue) => {
    if (applyingPreset.value) return;
    updateDebounced(newValue);
  });

  watch(sortOrder, (newValue) => {
    if (applyingPreset.value) return;
    router.replace({
      query: {
        ...route.query,
        sortBy: sortBy.value,
        sortOrder: newValue,
      },
    });
  });

  watch(sortBy, (newValue) => {
    if (applyingPreset.value) return;
    const meta = deckSortMeta[newValue as string];
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
    if (applyingPreset.value) return;
    router.replace({
      query: {
        ...route.query,
        status: newValue === 'none' ? undefined : newValue,
        offset: 0,
      },
    });
  });

  // Minutes in the URL and panel, seconds on the wire
  const toSeconds = (minutes: number | null) => (minutes == null ? undefined : minutes * 60);

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
      runtimeMin: computed(() => toSeconds(debouncedFilters.value.runtimeMin)),
      runtimeMax: computed(() => toSeconds(debouncedFilters.value.runtimeMax)),
      coverageMin: computed(() => debouncedFilters.value.coverageMin),
      coverageMax: computed(() => debouncedFilters.value.coverageMax),
      uniqueCoverageMin: computed(() => debouncedFilters.value.uniqueCoverageMin),
      uniqueCoverageMax: computed(() => debouncedFilters.value.uniqueCoverageMax),
      totalCoverageMin: computed(() => debouncedFilters.value.totalCoverageMin),
      totalCoverageMax: computed(() => debouncedFilters.value.totalCoverageMax),
      uTotalCoverageMin: computed(() => debouncedFilters.value.uTotalCoverageMin),
      uTotalCoverageMax: computed(() => debouncedFilters.value.uTotalCoverageMax),
      genres: computed(() => (debouncedFilters.value.includeGenres.length > 0 ? debouncedFilters.value.includeGenres.join(',') : undefined)),
      excludeGenres: computed(() => (debouncedFilters.value.excludeGenres.length > 0 ? debouncedFilters.value.excludeGenres.join(',') : undefined)),
      tags: computed(() => (debouncedFilters.value.includeTags.length > 0 ? debouncedFilters.value.includeTags.join(',') : undefined)),
      excludeTags: computed(() => (debouncedFilters.value.excludeTags.length > 0 ? debouncedFilters.value.excludeTags.join(',') : undefined)),
      excludeSequels: computed(() => debouncedFilters.value.excludeSequels),
      favourite: computed(() => (debouncedFilters.value.favourite === true ? true : undefined)),
    },
    watch: [offset, mediaType],
  });

  const facetArr = (a: number[]) => (a.length > 0 ? a.join(',') : undefined);
  const facetNum = (v: number | null) => (v == null ? undefined : v);
  const { data: facetData } = useApiFetch<{ genreCounts: Record<number, number>; tagCounts: Record<number, number> }>('media-deck/filter-facets', {
    server: false,
    lazy: true,
    query: {
      mediaType: computed(() => (mediaType.value ? Number(mediaType.value) : undefined)),
      charCountMin: computed(() => facetNum(debouncedFilters.value.charCountMin)),
      charCountMax: computed(() => facetNum(debouncedFilters.value.charCountMax)),
      difficultyMin: computed(() => facetNum(debouncedFilters.value.difficultyMin)),
      difficultyMax: computed(() => facetNum(debouncedFilters.value.difficultyMax)),
      releaseYearMin: computed(() => facetNum(debouncedFilters.value.releaseYearMin)),
      releaseYearMax: computed(() => facetNum(debouncedFilters.value.releaseYearMax)),
      uniqueKanjiMin: computed(() => facetNum(debouncedFilters.value.uniqueKanjiMin)),
      uniqueKanjiMax: computed(() => facetNum(debouncedFilters.value.uniqueKanjiMax)),
      subdeckCountMin: computed(() => facetNum(debouncedFilters.value.subdeckCountMin)),
      subdeckCountMax: computed(() => facetNum(debouncedFilters.value.subdeckCountMax)),
      extRatingMin: computed(() => facetNum(debouncedFilters.value.extRatingMin)),
      extRatingMax: computed(() => facetNum(debouncedFilters.value.extRatingMax)),
      speechSpeedMin: computed(() => facetNum(debouncedFilters.value.speechSpeedMin)),
      speechSpeedMax: computed(() => facetNum(debouncedFilters.value.speechSpeedMax)),
      speechDurationMin: computed(() => facetNum(debouncedFilters.value.speechDurationMin)),
      speechDurationMax: computed(() => facetNum(debouncedFilters.value.speechDurationMax)),
      runtimeMin: computed(() => toSeconds(debouncedFilters.value.runtimeMin)),
      runtimeMax: computed(() => toSeconds(debouncedFilters.value.runtimeMax)),
      genres: computed(() => facetArr(debouncedFilters.value.includeGenres)),
      excludeGenres: computed(() => facetArr(debouncedFilters.value.excludeGenres)),
      tags: computed(() => facetArr(debouncedFilters.value.includeTags)),
      excludeTags: computed(() => facetArr(debouncedFilters.value.excludeTags)),
      excludeSequels: computed(() => (debouncedFilters.value.excludeSequels === true ? true : undefined)),
    },
    watch: [mediaType, debouncedFilters],
  });

  const genreCounts = computed(() => facetData.value?.genreCounts ?? {});
  const tagCounts = computed(() => facetData.value?.tagCounts ?? {});

  const { start, end, totalItems, previousLink, nextLink, currentPage, totalPages, pageLinkFor } = usePagination(response);

  const HANDOFF_BELOW = 3;
  const handoffText = computed(() => {
    if (isDescribeMode.value || !debouncedTitleFilter.value) return null;
    if (status.value !== 'success' || (response.value?.totalItems ?? 0) >= HANDOFF_BELOW) return null;
    return debouncedTitleFilter.value;
  });

  const describeRequest = computed(() => describeQuery.value ?? handoffText.value);
  const {
    data: describeResponse,
    status: describeStatus,
    error: describeError,
  } = useApiFetch<DescriptionSearchResponse>('media-deck/search-by-description', {
    revalidateOnClient: true,
    immediate: describeRequest.value !== null,
    query: {
      query: describeRequest,
      limit: 40,
      mediaType: computed(() => (mediaType.value ? Number(mediaType.value) : undefined)),
    },
    watch: [describeRequest, mediaType],
  });

  const describeResults = computed(() =>
    describeRequest.value && describeResponse.value?.query === describeRequest.value ? describeResponse.value.results : []
  );

  const describeDecks = computed(() => describeResults.value.map((r) => r.deck));
  const describeMediaTypeLabel = computed(() => {
    const type = describeResponse.value?.mediaType;
    return type ? getMediaTypePluralText(type) : null;
  });

  const effectiveMediaType = computed<number | null>(() => {
    if (mediaType.value) return Number(mediaType.value);
    return isDescribeMode.value ? (describeResponse.value?.detectedMediaType ?? null) : null;
  });
  const showHandoff = computed(() => handoffText.value !== null && describeResults.value.length > 0);

  // Stream cards in over a few frames instead of mounting the whole page at once.
  const { visibleItems: visibleDecks } = useProgressiveList(
    computed(() => response.value?.data ?? []),
    { initial: 6, batch: 4, keyOf: (d) => d.deckId }
  );

  const jitenStore = useJitenStore();
  watch(
    () => jitenStore.coverageVersion,
    () => {
      refreshMediaList();
    }
  );

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
    { type: MediaType.YouTube, label: 'YouTube' },
  ];

  const isActive = (type: MediaType | null) => {
    if (type === null) return !effectiveMediaType.value;
    return effectiveMediaType.value === type;
  };

  const mediaTypeChipClass = (type: MediaType | null) => [
    'rounded-full border px-3 py-1.5 text-sm no-underline! hover:no-underline!',
    isActive(type)
      ? 'border-primary-500 bg-primary-500 font-medium text-white! dark:text-white!'
      : 'border-surface-300 text-surface-700! dark:border-surface-700 dark:text-surface-200!',
  ];

  const mediaTypePopover = ref();

  // Below md the media types are a single scrolling line, so the selected one has to be
  // brought into view or it reads as absent whenever it sits past the right edge.
  const mediaTypeStrip = ref<HTMLElement | null>(null);
  const stripCanScrollLeft = ref(false);
  const stripCanScrollRight = ref(false);

  const updateStripEdges = () => {
    const strip = mediaTypeStrip.value;
    if (!strip) return;
    stripCanScrollLeft.value = strip.scrollLeft > 1;
    stripCanScrollRight.value = strip.scrollLeft + strip.clientWidth < strip.scrollWidth - 1;
  };

  const revealActiveMediaType = () => {
    nextTick(() => {
      mediaTypeStrip.value?.querySelector('[data-active="true"]')?.scrollIntoView({ inline: 'center', block: 'nearest' });
      updateStripEdges();
    });
  };

  onMounted(() => {
    revealActiveMediaType();
    // Web fonts landing after mount change the chip widths, which moves both edges.
    document.fonts?.ready.then(updateStripEdges);
    window.addEventListener('resize', updateStripEdges);
  });
  onBeforeUnmount(() => window.removeEventListener('resize', updateStripEdges));
  watch(mediaType, revealActiveMediaType);

  const sortPopover = ref();
  const sortLabel = computed(() => deckSortLabels[sortBy.value as string] ?? 'Sort');

  // Listbox emits null when the selected row is tapped again; keep the current sort instead.
  const onSortByPicked = (value: unknown) => {
    if (value == null) return;
    sortBy.value = value as string;
    sortPopover.value?.hide();
  };

  const sortDirectionOptions = computed(() => {
    const meta = deckSortMeta[sortBy.value as string];
    return [
      { label: meta?.asc ?? 'Ascending', value: SortOrder.Ascending },
      { label: meta?.desc ?? 'Descending', value: SortOrder.Descending },
    ];
  });
</script>

<template>
  <div class="flex flex-col gap-4 max-md:gap-2">
    <!-- Below md the eleven labels wrap to three lines, so they scroll on one line instead.
         The strip can hide most of the list, so the fades mark the overflow and the trailing
         button stays put as a way to reach every type without scrolling. -->
    <div class="md:hidden flex items-center gap-2">
      <div class="relative min-w-0 flex-1">
        <!-- `w-0 min-w-full` fills the parent while contributing nothing to intrinsic width, so
             the w-max row inside cannot stretch an ancestor that is sized by its content. -->
        <div
          ref="mediaTypeStrip"
          class="w-0 min-w-full overflow-x-auto overscroll-x-contain [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
          @scroll.passive="updateStripEdges"
        >
          <div class="flex w-max gap-2 py-0.5">
            <NuxtLink
              v-for="option in mediaTypeOptions"
              :key="option.label"
              :to="{ query: option.type ? { ...route.query, mediaType: option.type, offset: 0 } : { ...route.query, mediaType: undefined, offset: 0 } }"
              :data-active="isActive(option.type)"
              :class="[mediaTypeChipClass(option.type), 'whitespace-nowrap']"
            >
              {{ option.label }}
            </NuxtLink>
          </div>
        </div>
        <div
          v-show="stripCanScrollLeft"
          class="pointer-events-none absolute inset-y-0 left-0 w-8 bg-gradient-to-r from-[var(--jiten-page-bg)] to-transparent"
        />
        <div
          v-show="stripCanScrollRight"
          class="pointer-events-none absolute inset-y-0 right-0 w-8 bg-gradient-to-l from-[var(--jiten-page-bg)] to-transparent"
        />
      </div>

      <Button rounded outlined size="small" class="shrink-0 px-2!" aria-label="Show all media types" @click="mediaTypePopover.toggle($event)">
        <Icon name="material-symbols:expand-more-rounded" size="1.25em" />
      </Button>
    </div>

    <Popover ref="mediaTypePopover" class="md:hidden">
      <div class="grid w-60 grid-cols-2 gap-1.5">
        <NuxtLink
          v-for="option in mediaTypeOptions"
          :key="option.label"
          :to="{ query: option.type ? { ...route.query, mediaType: option.type, offset: 0 } : { ...route.query, mediaType: undefined, offset: 0 } }"
          :class="[mediaTypeChipClass(option.type), 'text-center']"
          @click="mediaTypePopover.hide()"
        >
          {{ option.label }}
        </NuxtLink>
      </div>
    </Popover>

    <!-- The wrapper carries the breakpoint: PrimeVue's runtime .p-card display rule outranks
         a `hidden` utility placed on the component itself. -->
    <div class="hidden md:block">
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
    </div>

    <!-- Below md this is the only place the controls live, so it sticks; the page header is
         static, which leaves top-0 free. -->
    <div
      class="flex flex-col gap-2 max-md:sticky max-md:top-0 max-md:z-20 max-md:-mx-4 max-md:border-b max-md:border-surface-200 max-md:bg-[var(--jiten-page-bg)] max-md:px-4 max-md:py-2 max-md:dark:border-surface-800"
    >
      <div class="flex gap-2 max-md:flex-row max-md:flex-wrap max-md:items-center md:flex-row">
        <div class="hidden md:flex flex-row gap-2">
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
          <Button
            :icon="sortOrder === SortOrder.Ascending ? 'pi pi-arrow-up' : 'pi pi-arrow-down'"
            class="!px-4"
            @click="sortOrder = sortOrder === SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending"
          />
        </div>

        <!-- A min width rather than min-w-0: in a narrow container (the vocabulary detail page nests
           this inside a card, leaving ~314px) the field would otherwise shrink to a stub instead
           of wrapping onto its own row. -->
        <IconField class="max-md:min-w-32 max-md:flex-1 md:w-full">
          <InputIcon>
            <Icon name="material-symbols:search-rounded" />
          </InputIcon>
          <InputText
            v-model="titleFilter"
            type="text"
            placeholder="Search by title, or describe what you want"
            aria-label="Search by title, or describe what you want"
            class="w-full"
          />
          <InputIcon v-if="titleFilter" class="cursor-pointer" @click="titleFilter = null">
            <Icon name="material-symbols:close" />
          </InputIcon>
        </IconField>

        <!-- The breakpoint sits on the wrapper: PrimeVue's runtime .p-button display rule
           outranks a `hidden` utility placed on the Button itself. -->
        <div class="md:hidden shrink-0">
          <Button class="px-2!" :aria-label="`Sort by ${sortLabel}, ${sortOrderLabel}`" @click="sortPopover.toggle($event)">
            <Icon name="material-symbols:sort-rounded" size="1.25em" />
          </Button>
        </div>

        <Popover ref="sortPopover" class="md:hidden">
          <div class="flex w-68 flex-col gap-3">
            <SelectButton
              v-model="sortOrder"
              :options="sortDirectionOptions"
              option-label="label"
              option-value="value"
              :allow-empty="false"
              size="small"
              class="w-full"
            />
            <Listbox
              :model-value="sortBy"
              :options="sortByGrouped"
              option-label="label"
              option-value="value"
              option-group-label="label"
              option-group-children="items"
              scroll-height="50vh"
              class="w-full border-0!"
              @update:model-value="onSortByPicked"
            >
              <template #optiongroup="{ option }">
                <div class="text-xs font-semibold text-surface-500 dark:text-surface-400 py-0.5 px-1">{{ option.label }}</div>
              </template>
            </Listbox>
          </div>
        </Popover>

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
          v-model:runtime-min="runtimeMin"
          v-model:runtime-max="runtimeMax"
          v-model:coverage-min="coverageMin"
          v-model:coverage-max="coverageMax"
          v-model:unique-coverage-min="uniqueCoverageMin"
          v-model:unique-coverage-max="uniqueCoverageMax"
          v-model:total-coverage-min="totalCoverageMin"
          v-model:total-coverage-max="totalCoverageMax"
          v-model:u-total-coverage-min="uTotalCoverageMin"
          v-model:u-total-coverage-max="uTotalCoverageMax"
          v-model:include-genres="includeGenres"
          v-model:exclude-genres="excludeGenres"
          v-model:include-tags="includeTags"
          v-model:exclude-tags="excludeTags"
          v-model:exclude-sequels="excludeSequels"
          v-model:favourite="favourite"
          :is-connected="isConnected"
          :genre-counts="genreCounts"
          :tag-counts="tagCounts"
          :active-preset-name="activePresetName"
          :deck-count="totalItems"
          @reset="resetAllFilters"
        >
          <template v-if="isConnected" #presets>
            <MediaListFilterPresets
              v-model:presets="presets"
              v-model:default-name="defaultPresetName"
              :capture="captureCurrentFilters"
              :active-preset-name="activePresetName"
              @apply="applyPreset"
            />
          </template>
        </MediaListFilters>

        <div class="flex flex-row gap-2 items-center">
          <DisplayStyleSelector />
        </div>
      </div>
    </div>

    <!-- The Filters badge is the only other trace of active filters, and it scrolls away with
         its button; these keep the applied state readable and removable next to the results. -->
    <MediaListFilterChips
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
      v-model:runtime-min="runtimeMin"
      v-model:runtime-max="runtimeMax"
      v-model:coverage-min="coverageMin"
      v-model:coverage-max="coverageMax"
      v-model:unique-coverage-min="uniqueCoverageMin"
      v-model:unique-coverage-max="uniqueCoverageMax"
      v-model:total-coverage-min="totalCoverageMin"
      v-model:total-coverage-max="totalCoverageMax"
      v-model:u-total-coverage-min="uTotalCoverageMin"
      v-model:u-total-coverage-max="uTotalCoverageMax"
      v-model:include-genres="includeGenres"
      v-model:exclude-genres="excludeGenres"
      v-model:include-tags="includeTags"
      v-model:exclude-tags="excludeTags"
      v-model:exclude-sequels="excludeSequels"
      v-model:favourite="favourite"
      @reset="resetAllFilters"
    />

    <div v-if="isDescribeMode" class="flex flex-col gap-2">
      <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1 text-sm text-surface-600 dark:text-surface-300">
        <p>
          Closest by description to "<span lang="ja">{{ describeResponse?.searchedText || describeQuery }}</span
          >"<template v-if="describeMediaTypeLabel">, {{ describeMediaTypeLabel }} only</template>.
          <template v-if="totalItems > 0">
            <button type="button" class="text-primary-500 hover:underline cursor-pointer" @click="searchTitlesInstead">
              {{ totalItems.toLocaleString() }} {{ totalItems === 1 ? 'title matches' : 'titles match' }} this text
            </button>
          </template>
          <button v-else type="button" class="text-primary-500 hover:underline cursor-pointer" @click="searchTitlesInstead">Search titles instead</button>
        </p>
        <span v-if="describeResults.length" class="text-surface-400 whitespace-nowrap">Best {{ describeResults.length }} matches</span>
      </div>

      <div v-if="describeStatus === 'pending'" class="flex flex-col gap-4">
        <Card v-for="i in 5" :key="i" class="p-2">
          <template #content>
            <Skeleton width="100%" height="250px" />
          </template>
        </Card>
      </div>

      <div v-else-if="describeError" class="flex flex-col items-center justify-center py-16 text-center">
        <p class="text-lg font-medium text-primary-700 dark:text-primary-300">Description search is not available right now</p>
        <button type="button" class="text-sm text-primary-500 hover:underline cursor-pointer" @click="searchTitlesInstead">Search titles instead</button>
      </div>

      <div v-else-if="!describeResults.length" class="flex flex-col items-center justify-center py-16">
        <i class="pi pi-search text-4xl text-primary-500 mb-4" />
        <p class="text-lg font-medium text-primary-700 dark:text-primary-300">Nothing close enough</p>
        <p class="text-sm text-surface-400">Try describing the story, setting or mood in a few words</p>
      </div>

      <div v-else-if="displayStyle === DisplayStyle.Card" class="flex flex-col gap-2">
        <LazyHydrateMediaDeckCard
          v-for="(deck, index) in describeDecks"
          :key="deck.deckId"
          :deck="deck"
          :lazy-cover="index >= 3"
          :class="index >= 3 ? '[content-visibility:auto] [contain-intrinsic-size:auto_30rem] p-1 -m-1' : ''"
        />
      </div>

      <div v-else-if="displayStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center">
        <LazyHydrateMediaDeckCompactView v-for="(deck, index) in describeDecks" :key="deck.deckId" :deck="deck" :lazy-cover="index >= 12" />
      </div>

      <div v-else-if="displayStyle === DisplayStyle.Table" class="flex flex-col gap-0.5">
        <LazyHydrateMediaDeckTableView v-for="(deck, index) in describeDecks" :key="deck.deckId" :deck="deck" :lazy-render="index >= 12" />
      </div>
    </div>

    <div v-else>
      <div class="flex flex-col gap-1">
        <PaginationControls
          v-if="response?.data?.length"
          :previous-link="previousLink"
          :next-link="nextLink"
          :current-page="currentPage"
          :total-pages="totalPages"
          :page-link-for="pageLinkFor"
          :start="start"
          :end="end"
          :total-items="totalItems"
          item-label="decks"
          mobile-compact
        />

        <div v-if="status === 'pending'" class="flex flex-col gap-4">
          <Card v-for="i in 5" :key="i" class="p-2">
            <template #content>
              <Skeleton width="100%" height="250px" />
            </template>
          </Card>
        </div>

        <div v-else-if="error">Error: {{ error }}</div>

        <div v-else-if="!response?.data?.length && !showHandoff" class="flex flex-col items-center justify-center py-16">
          <i class="pi pi-search text-4xl text-primary-500 mb-4" />
          <p class="text-lg font-medium text-primary-700 dark:text-primary-300">No decks found</p>
          <p class="text-sm text-surface-400">Try adjusting your search or filters</p>
        </div>

        <div v-else-if="!response?.data?.length" />

        <!-- Card View -->
        <!-- LazyHydrate* keeps the SSR HTML but defers each item's hydration until it
             scrolls into view, so the initial hydration flush stays small. -->
        <div v-else-if="displayStyle === DisplayStyle.Card" class="flex flex-col gap-2">
          <LazyHydrateMediaDeckCard
            v-for="(deck, index) in visibleDecks"
            :key="deck.deckId"
            :deck="deck"
            :lazy-cover="index >= 3"
            :class="index >= 3 ? '[content-visibility:auto] [contain-intrinsic-size:auto_30rem] p-1 -m-1' : ''"
            @update:deck="updateDeckInList"
          />
        </div>

        <!-- Compact View -->
        <div v-else-if="displayStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center">
          <LazyHydrateMediaDeckCompactView v-for="(deck, index) in visibleDecks" :key="deck.deckId" :deck="deck" :lazy-cover="index >= 12" />
        </div>

        <!-- Table View -->
        <div v-else-if="displayStyle === DisplayStyle.Table" class="flex flex-col gap-0.5">
          <LazyHydrateMediaDeckTableView v-for="(deck, index) in visibleDecks" :key="deck.deckId" :deck="deck" :lazy-render="index >= 12" />
        </div>

        <Card v-if="showHandoff" class="mt-2">
          <template #content>
            <div class="flex flex-col gap-3">
              <div class="flex flex-col gap-0.5">
                <p class="font-medium">
                  <template v-if="response?.data?.length">Only {{ totalItems }} {{ totalItems === 1 ? 'title matches' : 'titles match' }}</template>
                  <template v-else>There is no title matching</template>
                  "<span lang="ja">{{ handoffText }}</span
                  >", but the following media have a description that comes close
                </p>
                <p class="text-sm text-surface-500 dark:text-surface-400">You can type what you feel like watching or reading, in English or Japanese.</p>
              </div>
              <div class="flex flex-wrap gap-3 max-h-48 overflow-hidden">
                <MediaDeckCompactView v-for="deck in describeDecks" :key="deck.deckId" :deck="deck" />
              </div>
              <div>
                <Button :label="`Show all ${describeResults.length} matches`" size="small" @click="searchDescriptionsInstead" />
              </div>
            </div>
          </template>
        </Card>
      </div>
      <PaginationControls
        v-if="response?.data?.length"
        :previous-link="previousLink"
        :next-link="nextLink"
        :current-page="currentPage"
        :total-pages="totalPages"
        :page-link-for="pageLinkFor"
        :start="start"
        :end="end"
        :total-items="totalItems"
        :show-summary="false"
        :scroll-to-top-on-navigate="true"
      />
    </div>
  </div>
</template>

<style scoped></style>
