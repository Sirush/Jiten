<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { Tag } from '~/types';
  import { getAllGenres } from '~/utils/genreMapper';
  import { NOT_ORIGINALLY_JP_TAG_ID } from '~/utils/tags';
  import type { TagState } from '~/components/TriStateTag.vue';
  import ScrollPanel from 'primevue/scrollpanel';

  const props = withDefaults(
    defineProps<{
      isConnected: boolean;
      genreCounts?: Record<number, number>;
      tagCounts?: Record<number, number>;
      activePresetName?: string | null;
    }>(),
    { genreCounts: () => ({}), tagCounts: () => ({}), activePresetName: null }
  );

  const emit = defineEmits<{
    reset: [];
  }>();

  const statusFilter = defineModel<string>('statusFilter', { required: true });
  const charCountMin = defineModel<number | null>('charCountMin', { required: true });
  const charCountMax = defineModel<number | null>('charCountMax', { required: true });
  const difficultyMin = defineModel<number | null>('difficultyMin', { required: true });
  const difficultyMax = defineModel<number | null>('difficultyMax', { required: true });
  const releaseYearMin = defineModel<number | null>('releaseYearMin', { required: true });
  const releaseYearMax = defineModel<number | null>('releaseYearMax', { required: true });
  const uniqueKanjiMin = defineModel<number | null>('uniqueKanjiMin', { required: true });
  const uniqueKanjiMax = defineModel<number | null>('uniqueKanjiMax', { required: true });
  const subdeckCountMin = defineModel<number | null>('subdeckCountMin', { required: true });
  const subdeckCountMax = defineModel<number | null>('subdeckCountMax', { required: true });
  const extRatingMin = defineModel<number | null>('extRatingMin', { required: true });
  const extRatingMax = defineModel<number | null>('extRatingMax', { required: true });
  const speechSpeedMin = defineModel<number | null>('speechSpeedMin', { required: true });
  const speechSpeedMax = defineModel<number | null>('speechSpeedMax', { required: true });
  const speechDurationMin = defineModel<number | null>('speechDurationMin', { required: true });
  const speechDurationMax = defineModel<number | null>('speechDurationMax', { required: true });
  const includeGenres = defineModel<number[]>('includeGenres', { required: true });
  const excludeGenres = defineModel<number[]>('excludeGenres', { required: true });
  const includeTags = defineModel<number[]>('includeTags', { required: true });
  const excludeTags = defineModel<number[]>('excludeTags', { required: true });
  const coverageMin = defineModel<number | null>('coverageMin', { required: true });
  const coverageMax = defineModel<number | null>('coverageMax', { required: true });
  const uniqueCoverageMin = defineModel<number | null>('uniqueCoverageMin', { required: true });
  const uniqueCoverageMax = defineModel<number | null>('uniqueCoverageMax', { required: true });
  const totalCoverageMin = defineModel<number | null>('totalCoverageMin', { required: true });
  const totalCoverageMax = defineModel<number | null>('totalCoverageMax', { required: true });
  const uTotalCoverageMin = defineModel<number | null>('uTotalCoverageMin', { required: true });
  const uTotalCoverageMax = defineModel<number | null>('uTotalCoverageMax', { required: true });
  const excludeSequels = defineModel<boolean | null>('excludeSequels', { required: false });

  const excludeNotOriginallyJp = computed({
    get: () => excludeTags.value.includes(NOT_ORIGINALLY_JP_TAG_ID),
    set: (val: boolean) => {
      if (val) {
        if (!excludeTags.value.includes(NOT_ORIGINALLY_JP_TAG_ID)) {
          excludeTags.value.push(NOT_ORIGINALLY_JP_TAG_ID);
        }
      } else {
        excludeTags.value = excludeTags.value.filter((id) => id !== NOT_ORIGINALLY_JP_TAG_ID);
      }
    },
  });

  const popover = ref();

  const currentYear = new Date().getFullYear();

  const statusFilterOptions = [
    { label: 'Show All', value: 'none' },
    { label: 'Without Status', value: 'nostatus' },
    { label: 'Only Favourited', value: 'fav' },
    { label: 'Only Ignored', value: 'ignore' },
    { label: 'Only Planning', value: 'planning' },
    { label: 'Only Ongoing', value: 'ongoing' },
    { label: 'Only Completed', value: 'completed' },
    { label: 'Only Dropped', value: 'dropped' },
  ];

  const { data: availableTags } = useApiFetch<Tag[]>('media-deck/tags', {
    server: true,
    lazy: false,
  });

  const tags = computed(() => availableTags.value || []);
  const genres = computed(() => getAllGenres());

  const genreSearchQuery = ref('');
  const tagSearchQuery = ref('');

  // Only hide zero-count chips once counts have actually loaded (empty = not loaded, or nothing matches).
  const hasGenreFacets = computed(() => Object.keys(props.genreCounts).length > 0);
  const hasTagFacets = computed(() => Object.keys(props.tagCounts).length > 0);

  const filteredGenres = computed(() => {
    const query = genreSearchQuery.value.toLowerCase();
    return genres.value.filter((genre) => {
      if (query && !genre.label.toLowerCase().includes(query)) return false;
      // keep selected chips reachable so they can be toggled back off
      if (includeGenres.value.includes(genre.value) || excludeGenres.value.includes(genre.value)) return true;
      if (!hasGenreFacets.value) return true;
      return (props.genreCounts[genre.value] ?? 0) > 0;
    });
  });

  const filteredTags = computed(() => {
    const query = tagSearchQuery.value.toLowerCase();
    return tags.value.filter((tag) => {
      if (query && !tag.name.toLowerCase().includes(query)) return false;
      if (includeTags.value.includes(tag.tagId) || excludeTags.value.includes(tag.tagId)) return true;
      if (!hasTagFacets.value) return true;
      return (props.tagCounts[tag.tagId] ?? 0) > 0;
    });
  });

  const genreLabel = (value: number, label: string): string => {
    const c = props.genreCounts[value];
    return hasGenreFacets.value && c != null ? `${label} (${c.toLocaleString()})` : label;
  };
  const tagLabel = (tagId: number, name: string): string => {
    const c = props.tagCounts[tagId];
    return hasTagFacets.value && c != null ? `${name} (${c.toLocaleString()})` : name;
  };

  const genreFilteredCount = computed(() => filteredGenres.value.length);
  const genreTotalCount = computed(() => genres.value.length);
  const tagFilteredCount = computed(() => filteredTags.value.length);
  const tagTotalCount = computed(() => tags.value.length);

  const CHAR_COUNT_STEPS: number[] = [];
  for (let v = 0; v <= 10_000; v += 1_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 20_000; v <= 1_000_000; v += 10_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 1_100_000; v <= 3_000_000; v += 100_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 3_500_000; v <= 6_000_000; v += 500_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 7_000_000; v <= 20_000_000; v += 1_000_000) CHAR_COUNT_STEPS.push(v);

  const charCountToPos = (val: number) => {
    let best = 0;
    for (let i = 1; i < CHAR_COUNT_STEPS.length; i++) {
      if (Math.abs(CHAR_COUNT_STEPS[i] - val) < Math.abs(CHAR_COUNT_STEPS[best] - val)) best = i;
    }
    return best;
  };

  const charCountSliderRange = computed<[number, number]>({
    get: () => [charCountToPos(charCountMin.value ?? 0), charCountToPos(charCountMax.value ?? 20_000_000)],
    set: (val) => {
      charCountMin.value = CHAR_COUNT_STEPS[val[0]];
      charCountMax.value = CHAR_COUNT_STEPS[val[1]];
    },
  });

  const difficultyRange = computed<[number, number]>({
    get: () => [difficultyMin.value ?? 0, difficultyMax.value ?? 5],
    set: (val) => {
      difficultyMin.value = val[0];
      difficultyMax.value = val[1];
    },
  });

  const releaseYearRange = computed<[number, number]>({
    get: () => [releaseYearMin.value ?? 1900, releaseYearMax.value ?? currentYear],
    set: (val) => {
      releaseYearMin.value = val[0];
      releaseYearMax.value = val[1];
    },
  });

  const uniqueKanjiRange = computed<[number, number]>({
    get: () => [uniqueKanjiMin.value ?? 0, uniqueKanjiMax.value ?? 5000],
    set: (val) => {
      uniqueKanjiMin.value = val[0];
      uniqueKanjiMax.value = val[1];
    },
  });

  const subdeckCountRange = computed<[number, number]>({
    get: () => [subdeckCountMin.value ?? 0, subdeckCountMax.value ?? 2000],
    set: (val) => {
      subdeckCountMin.value = val[0];
      subdeckCountMax.value = val[1];
    },
  });

  const extRatingRange = computed<[number, number]>({
    get: () => [extRatingMin.value ?? 0, extRatingMax.value ?? 100],
    set: (val) => {
      extRatingMin.value = val[0];
      extRatingMax.value = val[1];
    },
  });

  const subtitleRateRange = computed<[number, number]>({
    get: () => [speechSpeedMin.value ?? 0, speechSpeedMax.value ?? 800],
    set: (val) => {
      speechSpeedMin.value = val[0];
      speechSpeedMax.value = val[1];
    },
  });

  const speechDurationRange = computed<[number, number]>({
    get: () => [speechDurationMin.value ?? 0, speechDurationMax.value ?? 300],
    set: (val) => {
      speechDurationMin.value = val[0];
      speechDurationMax.value = val[1];
    },
  });

  const coverageRange = computed<[number, number]>({
    get: () => [coverageMin.value ?? 0, coverageMax.value ?? 100],
    set: (val) => {
      coverageMin.value = val[0];
      coverageMax.value = val[1];
    },
  });

  const uniqueCoverageRange = computed<[number, number]>({
    get: () => [uniqueCoverageMin.value ?? 0, uniqueCoverageMax.value ?? 100],
    set: (val) => {
      uniqueCoverageMin.value = val[0];
      uniqueCoverageMax.value = val[1];
    },
  });

  const totalCoverageRange = computed<[number, number]>({
    get: () => [totalCoverageMin.value ?? 0, totalCoverageMax.value ?? 100],
    set: (val) => {
      totalCoverageMin.value = val[0];
      totalCoverageMax.value = val[1];
    },
  });

  const uTotalCoverageRange = computed<[number, number]>({
    get: () => [uTotalCoverageMin.value ?? 0, uTotalCoverageMax.value ?? 100],
    set: (val) => {
      uTotalCoverageMin.value = val[0];
      uTotalCoverageMax.value = val[1];
    },
  });

  const updateGenreState = (genreId: number, state: TagState) => {
    if (state === 'include') {
      if (!includeGenres.value.includes(genreId)) {
        includeGenres.value.push(genreId);
      }
      excludeGenres.value = excludeGenres.value.filter((id) => id !== genreId);
    } else if (state === 'exclude') {
      includeGenres.value = includeGenres.value.filter((id) => id !== genreId);
      if (!excludeGenres.value.includes(genreId)) {
        excludeGenres.value.push(genreId);
      }
    } else {
      includeGenres.value = includeGenres.value.filter((id) => id !== genreId);
      excludeGenres.value = excludeGenres.value.filter((id) => id !== genreId);
    }
  };

  const updateTagState = (tagId: number, state: TagState) => {
    if (state === 'include') {
      if (!includeTags.value.includes(tagId)) {
        includeTags.value.push(tagId);
      }
      excludeTags.value = excludeTags.value.filter((id) => id !== tagId);
    } else if (state === 'exclude') {
      includeTags.value = includeTags.value.filter((id) => id !== tagId);
      if (!excludeTags.value.includes(tagId)) {
        excludeTags.value.push(tagId);
      }
    } else {
      includeTags.value = includeTags.value.filter((id) => id !== tagId);
      excludeTags.value = excludeTags.value.filter((id) => id !== tagId);
    }
  };

  const handleReset = () => {
    genreSearchQuery.value = '';
    tagSearchQuery.value = '';
    emit('reset');
  };

  const activeFilterCount = computed(() => {
    let count = 0;
    if (charCountMin.value != null) count++;
    if (charCountMax.value != null) count++;
    if (difficultyMin.value != null) count++;
    if (difficultyMax.value != null) count++;
    if (releaseYearMin.value != null) count++;
    if (releaseYearMax.value != null) count++;
    if (uniqueKanjiMin.value != null) count++;
    if (uniqueKanjiMax.value != null) count++;
    if (subdeckCountMin.value != null) count++;
    if (subdeckCountMax.value != null) count++;
    if (extRatingMin.value != null) count++;
    if (extRatingMax.value != null) count++;
    if (speechSpeedMin.value != null) count++;
    if (speechSpeedMax.value != null) count++;
    if (speechDurationMin.value != null) count++;
    if (speechDurationMax.value != null) count++;
    if (coverageMin.value != null) count++;
    if (coverageMax.value != null) count++;
    if (uniqueCoverageMin.value != null) count++;
    if (uniqueCoverageMax.value != null) count++;
    if (totalCoverageMin.value != null) count++;
    if (totalCoverageMax.value != null) count++;
    if (uTotalCoverageMin.value != null) count++;
    if (uTotalCoverageMax.value != null) count++;
    if (statusFilter.value !== 'none') count++;
    if (excludeSequels.value) count++;
    count += includeGenres.value.length;
    count += excludeGenres.value.length;
    count += includeTags.value.length;
    count += excludeTags.value.length;
    return count;
  });

  const badgeLabel = computed(() => {
    const preset = props.activePresetName;
    if (preset) return preset.length > 12 ? `${preset.slice(0, 11)}…` : preset;
    return activeFilterCount.value > 0 ? String(activeFilterCount.value) : null;
  });

  const toggle = (event: Event) => {
    popover.value.toggle(event);
  };

  defineExpose({ toggle });
</script>

<template>
  <div class="relative shrink-0">
    <Button class="max-md:px-2!" aria-label="Filters" @click="toggle($event)">
      <Icon name="material-symbols:filter-list" size="1.25em" />
      <span class="hidden md:inline">Filters</span>
    </Button>
    <Badge
      v-if="badgeLabel"
      :value="badgeLabel"
      severity="warn"
      :class="['absolute -top-2 -right-2 pointer-events-none', activePresetName ? 'max-w-20 truncate md:max-w-32' : '']"
    />
  </div>

  <Popover ref="popover" class="w-[min(48rem,calc(100vw_-_2rem))]">
    <div class="flex flex-col gap-4 p-3 min-w-[280px]">
      <div v-if="$slots.presets" class="border-b border-gray-200 pb-3 dark:border-gray-700">
        <slot name="presets" />
      </div>

      <Tabs value="filters" :show-navigators="false" lazy>
        <TabList>
          <Tab value="filters">Filters</Tab>
          <Tab value="genres">Genres</Tab>
          <Tab value="tags">Tags</Tab>
        </TabList>

        <TabPanels>
          <TabPanel value="filters">
            <div class="overflow-y-auto max-md:max-h-[50vh] md:max-h-[60vh]">
              <div class="flex flex-col gap-4 pt-4">
                <FloatLabel v-if="isConnected" variant="on" class="w-full">
                  <Select
                    v-model="statusFilter"
                    :options="statusFilterOptions"
                    option-label="label"
                    option-value="value"
                    placeholder="Status"
                    input-id="preferenceFilter"
                    class="w-full md:w-56"
                    scroll-height="30vh"
                  />
                  <label for="preferenceFilter">Status</label>
                </FloatLabel>

                <Accordion multiple lazy>
                  <AccordionPanel value="content">
                    <AccordionHeader>Content</AccordionHeader>
                    <AccordionContent>
                      <div class="flex flex-col gap-4 pt-2">
                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Character count</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="charCountMin"
                              :min="0"
                              :max="20000000"
                              :use-grouping="true"
                              fluid
                              class="max-w-34 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                              :step="10000"
                            />
                            <Slider v-model="charCountSliderRange" range :min="0" :max="CHAR_COUNT_STEPS.length - 1" class="flex-1" />
                            <InputNumber
                              v-model="charCountMax"
                              :min="0"
                              :max="20000000"
                              :use-grouping="true"
                              fluid
                              class="max-w-34 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                              :step="10000"
                            />
                          </div>
                        </div>

                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Unique kanji</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="uniqueKanjiMin"
                              :min="0"
                              :max="5000"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                              :step="10"
                            />
                            <Slider v-model="uniqueKanjiRange" range :min="0" :max="5000" class="flex-1" />
                            <InputNumber
                              v-model="uniqueKanjiMax"
                              :min="0"
                              :max="5000"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                              :step="10"
                            />
                          </div>
                        </div>

                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Subdecks</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="subdeckCountMin"
                              :min="0"
                              :max="2000"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="subdeckCountRange" range :min="0" :max="2000" class="flex-1" />
                            <InputNumber
                              v-model="subdeckCountMax"
                              :min="0"
                              :max="2000"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>
                      </div>
                    </AccordionContent>
                  </AccordionPanel>

                  <AccordionPanel value="difficulty">
                    <AccordionHeader>Difficulty</AccordionHeader>
                    <AccordionContent>
                      <div class="flex flex-col gap-4 pt-2">
                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Difficulty</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="difficultyMin"
                              :min="0"
                              :max="5"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="1"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                              :step="0.5"
                            />
                            <Slider v-model="difficultyRange" range :min="0" :max="5" :step="0.5" class="flex-1" />
                            <InputNumber
                              v-model="difficultyMax"
                              :min="0"
                              :max="5"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="1"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                              :step="0.5"
                            />
                          </div>
                        </div>

                        <div v-if="isConnected" class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Coverage (Mature) (%)</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="coverageMin"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="coverageRange" range :min="0" :max="100" class="flex-1" />
                            <InputNumber
                              v-model="coverageMax"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>

                        <div v-if="isConnected" class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Coverage (Total) (%)</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="totalCoverageMin"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="totalCoverageRange" range :min="0" :max="100" class="flex-1" />
                            <InputNumber
                              v-model="totalCoverageMax"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>

                        <div v-if="isConnected" class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Unique Coverage (Mature) (%)</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="uniqueCoverageMin"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="uniqueCoverageRange" range :min="0" :max="100" class="flex-1" />
                            <InputNumber
                              v-model="uniqueCoverageMax"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>

                        <div v-if="isConnected" class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Unique Coverage (Total) (%)</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="uTotalCoverageMin"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="uTotalCoverageRange" range :min="0" :max="100" class="flex-1" />
                            <InputNumber
                              v-model="uTotalCoverageMax"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="2"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>
                      </div>
                    </AccordionContent>
                  </AccordionPanel>

                  <AccordionPanel value="media-properties">
                    <AccordionHeader>Media Properties</AccordionHeader>
                    <AccordionContent>
                      <div class="flex flex-col gap-4 pt-2">
                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Release year</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="releaseYearMin"
                              :min="1900"
                              :max="currentYear"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="releaseYearRange" range :min="1900" :max="currentYear" class="flex-1" />
                            <InputNumber
                              v-model="releaseYearMax"
                              :min="1900"
                              :max="currentYear"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>

                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">External Rating (0 = unknown rating)</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="extRatingMin"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                            />
                            <Slider v-model="extRatingRange" range :min="0" :max="100" class="flex-1" />
                            <InputNumber
                              v-model="extRatingMax"
                              :min="0"
                              :max="100"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                            />
                          </div>
                        </div>
                      </div>
                    </AccordionContent>
                  </AccordionPanel>

                  <AccordionPanel value="audio">
                    <AccordionHeader>Audio</AccordionHeader>
                    <AccordionContent>
                      <div class="flex flex-col gap-4 pt-2">
                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Speech speed</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="speechSpeedMin"
                              :min="0"
                              :max="800"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="1"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                              :step="10"
                            />
                            <Slider v-model="subtitleRateRange" range :min="0" :max="800" :step="10" class="flex-1" />
                            <InputNumber
                              v-model="speechSpeedMax"
                              :min="0"
                              :max="800"
                              :use-grouping="false"
                              mode="decimal"
                              :min-fraction-digits="0"
                              :max-fraction-digits="1"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                              :step="10"
                            />
                          </div>
                        </div>

                        <div class="flex flex-col gap-2">
                          <div class="text-sm font-medium text-gray-600 dark:text-gray-300">Speech duration (hours)</div>
                          <div class="flex items-center gap-3">
                            <InputNumber
                              v-model="speechDurationMin"
                              :min="0"
                              :max="300"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Min"
                              :step="1"
                            />
                            <Slider v-model="speechDurationRange" range :min="0" :max="300" :step="1" class="flex-1" />
                            <InputNumber
                              v-model="speechDurationMax"
                              :min="0"
                              :max="300"
                              :use-grouping="false"
                              fluid
                              class="max-w-28 flex-shrink-0"
                              show-buttons
                              size="small"
                              placeholder="Max"
                              :step="1"
                            />
                          </div>
                        </div>
                      </div>
                    </AccordionContent>
                  </AccordionPanel>
                </Accordion>

                <div class="flex flex-col gap-2">
                  <div class="flex items-center gap-2">
                    <Checkbox v-model="excludeSequels" class="flex-shrink-0" input-id="excludeSequels" binary />
                    <label for="excludeSequels" class="text-sm font-medium text-gray-600 dark:text-gray-300">Exclude sequels and fandiscs</label>
                  </div>
                  <div class="flex items-center gap-2">
                    <Checkbox v-model="excludeNotOriginallyJp" class="flex-shrink-0" input-id="excludeNotOriginallyJp" binary />
                    <label for="excludeNotOriginallyJp" class="text-sm font-medium text-gray-600 dark:text-gray-300">
                      Exclude not originally Japanese media
                    </label>
                  </div>
                </div>
              </div>
            </div>
          </TabPanel>

          <TabPanel value="genres">
            <div class="flex flex-col gap-3 pt-4">
              <div class="flex items-center justify-between gap-2">
                <div class="text-sm font-medium text-gray-600 dark:text-gray-300">{{ genreFilteredCount }}/{{ genreTotalCount }}</div>
                <IconField class="flex-1">
                  <InputIcon>
                    <Icon name="material-symbols:search-rounded" />
                  </InputIcon>
                  <InputText v-model="genreSearchQuery" type="text" placeholder="Search genres..." class="w-full" />
                  <InputIcon v-if="genreSearchQuery" class="cursor-pointer" @click="genreSearchQuery = ''">
                    <Icon name="material-symbols:close" />
                  </InputIcon>
                </IconField>
              </div>
              <ScrollPanel class="w-full" style="height: 350px">
                <div class="flex flex-wrap gap-2 p-2">
                  <TriStateTag
                    v-for="genre in filteredGenres"
                    :key="genre.value"
                    :label="genreLabel(genre.value, genre.label)"
                    :state="includeGenres.includes(genre.value) ? 'include' : excludeGenres.includes(genre.value) ? 'exclude' : 'neutral'"
                    @update:state="(state) => updateGenreState(genre.value, state)"
                  />
                </div>
              </ScrollPanel>
            </div>
          </TabPanel>

          <TabPanel value="tags">
            <div class="flex flex-col gap-3 pt-4">
              <div class="flex items-center justify-between gap-2">
                <div class="text-sm font-medium text-gray-600 dark:text-gray-300">{{ tagFilteredCount }}/{{ tagTotalCount }}</div>
                <IconField class="flex-1">
                  <InputIcon>
                    <Icon name="material-symbols:search-rounded" />
                  </InputIcon>
                  <InputText v-model="tagSearchQuery" type="text" placeholder="Search tags..." class="w-full" />
                  <InputIcon v-if="tagSearchQuery" class="cursor-pointer" @click="tagSearchQuery = ''">
                    <Icon name="material-symbols:close" />
                  </InputIcon>
                </IconField>
              </div>
              <ScrollPanel class="w-full" style="height: 40vh">
                <div class="flex flex-wrap gap-2 p-2">
                  <TriStateTag
                    v-for="tag in filteredTags"
                    :key="tag.tagId"
                    :label="tagLabel(tag.tagId, tag.name)"
                    :state="includeTags.includes(tag.tagId) ? 'include' : excludeTags.includes(tag.tagId) ? 'exclude' : 'neutral'"
                    @update:state="(state) => updateTagState(tag.tagId, state)"
                  />
                </div>
              </ScrollPanel>
            </div>
          </TabPanel>
        </TabPanels>
      </Tabs>

      <!-- Reset Button -->
      <div class="flex justify-end pt-3 border-t border-gray-200 dark:border-gray-700">
        <Button severity="danger" @click="handleReset">
          <Icon name="material-symbols:refresh" class="mr-2" />
          Reset All Filters
        </Button>
      </div>
    </div>
  </Popover>
</template>

<style scoped>
  @media (max-width: 767px) {
    :deep(.p-slider) {
      display: none;
    }
  }
</style>
