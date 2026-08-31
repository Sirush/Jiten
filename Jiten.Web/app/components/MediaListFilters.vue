<script setup lang="ts">
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { Tag } from '~/types';
  import { getAllGenres } from '~/utils/genreMapper';
  import { NOT_ORIGINALLY_JP_TAG_ID } from '~/utils/tags';
  import type { TagState } from '~/components/TriStateTag.vue';
  import type { TagCloudEntry } from '~/components/MediaListTagCloud.vue';
  import { countActiveFilters, MEDIA_RANGE_SPECS, readRangeBounds, type MediaRangeKey, type MediaRangeRefs } from '~/utils/mediaFilterRanges';
  import type { RangeBounds } from '~/utils/rangeFilters';

  const props = withDefaults(
    defineProps<{
      isConnected: boolean;
      genreCounts?: Record<number, number>;
      tagCounts?: Record<number, number>;
      activePresetName?: string | null;
      deckCount?: number | null;
    }>(),
    { genreCounts: () => ({}), tagCounts: () => ({}), activePresetName: null, deckCount: null }
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
  const favourite = defineModel<boolean | null>('favourite', { required: false });

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

  const rangeBounds: MediaRangeRefs = {
    charCount: { min: charCountMin, max: charCountMax },
    uniqueKanji: { min: uniqueKanjiMin, max: uniqueKanjiMax },
    subdeckCount: { min: subdeckCountMin, max: subdeckCountMax },
    difficulty: { min: difficultyMin, max: difficultyMax },
    coverage: { min: coverageMin, max: coverageMax },
    totalCoverage: { min: totalCoverageMin, max: totalCoverageMax },
    uniqueCoverage: { min: uniqueCoverageMin, max: uniqueCoverageMax },
    uTotalCoverage: { min: uTotalCoverageMin, max: uTotalCoverageMax },
    releaseYear: { min: releaseYearMin, max: releaseYearMax },
    extRating: { min: extRatingMin, max: extRatingMax },
    speechSpeed: { min: speechSpeedMin, max: speechSpeedMax },
    speechDuration: { min: speechDurationMin, max: speechDurationMax },
  };

  const rangeValues = computed<Record<MediaRangeKey, RangeBounds>>({
    get: () => readRangeBounds(rangeBounds),
    set: (next) => {
      for (const spec of MEDIA_RANGE_SPECS) {
        rangeBounds[spec.key].min.value = next[spec.key].min;
        rangeBounds[spec.key].max.value = next[spec.key].max;
      }
    },
  });

  const popover = ref();
  const drawerOpen = ref(false);
  const mobilePane = ref<'genres' | 'tags' | null>(null);
  const expandedKey = ref<MediaRangeKey | null>(null);

  // Popover and Drawer are teleported overlays, so the split cannot be a CSS-only one.
  const isMobile = ref(false);
  let breakpoint: MediaQueryList | null = null;
  const syncBreakpoint = (event: MediaQueryListEvent | MediaQueryList) => {
    isMobile.value = !event.matches;
  };

  onMounted(() => {
    breakpoint = window.matchMedia('(min-width: 768px)');
    syncBreakpoint(breakpoint);
    breakpoint.addEventListener('change', syncBreakpoint);
  });
  onBeforeUnmount(() => breakpoint?.removeEventListener('change', syncBreakpoint));

  const statusFilterOptions = [
    { label: 'Show All', value: 'none' },
    { label: 'Without Status', value: 'nostatus' },
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

  const tags = computed(() => [...(availableTags.value || [])].sort((a, b) => a.name.localeCompare(b.name)));
  const genres = computed(() => getAllGenres());

  const filterSearchQuery = ref('');
  const genreSearchQuery = ref('');
  const tagSearchQuery = ref('');

  // Only hide zero-count chips once counts have actually loaded (empty = not loaded, or nothing matches).
  const hasGenreFacets = computed(() => Object.keys(props.genreCounts).length > 0);
  const hasTagFacets = computed(() => Object.keys(props.tagCounts).length > 0);

  const lower = (value: string) => value.trim().toLowerCase();

  const genreQueries = computed(() => (mobilePane.value === 'genres' ? [genreSearchQuery.value] : [filterSearchQuery.value]));
  const tagQueries = computed(() => (mobilePane.value === 'tags' ? [tagSearchQuery.value] : [tagSearchQuery.value, filterSearchQuery.value]));

  const matchesAll = (text: string, queries: string[]) => queries.every((query) => !lower(query) || text.toLowerCase().includes(lower(query)));

  const genreState = (value: number): TagState =>
    includeGenres.value.includes(value) ? 'include' : excludeGenres.value.includes(value) ? 'exclude' : 'neutral';
  const tagState = (tagId: number): TagState => (includeTags.value.includes(tagId) ? 'include' : excludeTags.value.includes(tagId) ? 'exclude' : 'neutral');

  const genreLabel = (value: number, label: string): string => {
    if (!hasGenreFacets.value) return label;
    return `${label} (${(props.genreCounts[value] ?? 0).toLocaleString()})`;
  };
  const tagLabel = (tagId: number, name: string): string => {
    const count = props.tagCounts[tagId];
    return hasTagFacets.value && count != null ? `${name} (${count.toLocaleString()})` : name;
  };

  const genreEntries = computed<TagCloudEntry[]>(() =>
    genres.value
      .filter((genre) => {
        if (!matchesAll(genre.label, genreQueries.value)) return false;
        if (!isMobile.value) return true;
        if (genreState(genre.value) !== 'neutral') return true;
        if (!hasGenreFacets.value) return true;
        return (props.genreCounts[genre.value] ?? 0) > 0;
      })
      .map((genre) => ({
        id: genre.value,
        label: genreLabel(genre.value, genre.label),
        state: genreState(genre.value),
        dimmed: hasGenreFacets.value && genreState(genre.value) === 'neutral' && (props.genreCounts[genre.value] ?? 0) === 0,
      }))
  );

  const tagEntries = computed<TagCloudEntry[]>(() =>
    tags.value
      .filter((tag) => {
        if (!matchesAll(tag.name, tagQueries.value)) return false;
        if (tagState(tag.tagId) !== 'neutral') return true;
        if (!hasTagFacets.value) return true;
        return (props.tagCounts[tag.tagId] ?? 0) > 0;
      })
      .map((tag) => ({ id: tag.tagId, label: tagLabel(tag.tagId, tag.name), state: tagState(tag.tagId) }))
  );

  const selectedGenreEntries = computed<TagCloudEntry[]>(() =>
    genres.value
      .filter((genre) => genreState(genre.value) !== 'neutral')
      .map((genre) => ({ id: genre.value, label: genreLabel(genre.value, genre.label), state: genreState(genre.value) }))
  );

  const selectedTagEntries = computed<TagCloudEntry[]>(() =>
    tags.value
      .filter((tag) => tagState(tag.tagId) !== 'neutral')
      .map((tag) => ({ id: tag.tagId, label: tagLabel(tag.tagId, tag.name), state: tagState(tag.tagId) }))
  );

  const selectionSummary = (included: string[], excluded: string[]): string | null => {
    const parts = [...included, ...excluded.map((name) => `not ${name}`)];
    if (parts.length === 0) return null;
    return parts.length <= 2 ? parts.join(', ') : `${parts.length} selected`;
  };

  const genreName = (id: number) => genres.value.find((genre) => genre.value === id)?.label ?? String(id);
  const tagName = (id: number) => tags.value.find((tag) => tag.tagId === id)?.name ?? String(id);

  const genreSummary = computed(() => selectionSummary(includeGenres.value.map(genreName), excludeGenres.value.map(genreName)));
  const tagSummary = computed(() => selectionSummary(includeTags.value.map(tagName), excludeTags.value.map(tagName)));

  // A section only disappears for the panel-wide search; its own empty search result keeps the box reachable.
  const hasPanelSearch = computed(() => lower(filterSearchQuery.value).length > 0);
  const showGenreSection = computed(() => genreEntries.value.length > 0 || !hasPanelSearch.value);
  const showTagSection = computed(() => tagEntries.value.length > 0 || !hasPanelSearch.value);

  const CHAR_COUNT_STEPS: number[] = [];
  for (let v = 0; v <= 10_000; v += 1_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 20_000; v <= 1_000_000; v += 10_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 1_100_000; v <= 3_000_000; v += 100_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 3_500_000; v <= 6_000_000; v += 500_000) CHAR_COUNT_STEPS.push(v);
  for (let v = 7_000_000; v <= 20_000_000; v += 1_000_000) CHAR_COUNT_STEPS.push(v);

  const setGenreState = (genreId: number, state: TagState) => {
    if (state === 'include') {
      if (!includeGenres.value.includes(genreId)) includeGenres.value.push(genreId);
      excludeGenres.value = excludeGenres.value.filter((id) => id !== genreId);
    } else if (state === 'exclude') {
      includeGenres.value = includeGenres.value.filter((id) => id !== genreId);
      if (!excludeGenres.value.includes(genreId)) excludeGenres.value.push(genreId);
    } else {
      includeGenres.value = includeGenres.value.filter((id) => id !== genreId);
      excludeGenres.value = excludeGenres.value.filter((id) => id !== genreId);
    }
  };

  const setTagState = (tagId: number, state: TagState) => {
    if (state === 'include') {
      if (!includeTags.value.includes(tagId)) includeTags.value.push(tagId);
      excludeTags.value = excludeTags.value.filter((id) => id !== tagId);
    } else if (state === 'exclude') {
      includeTags.value = includeTags.value.filter((id) => id !== tagId);
      if (!excludeTags.value.includes(tagId)) excludeTags.value.push(tagId);
    } else {
      includeTags.value = includeTags.value.filter((id) => id !== tagId);
      excludeTags.value = excludeTags.value.filter((id) => id !== tagId);
    }
  };

  const clearGenres = () => {
    includeGenres.value = [];
    excludeGenres.value = [];
  };

  const clearTags = () => {
    includeTags.value = [];
    excludeTags.value = [];
  };

  const handleReset = () => {
    filterSearchQuery.value = '';
    genreSearchQuery.value = '';
    tagSearchQuery.value = '';
    expandedKey.value = null;
    emit('reset');
  };

  const activeFilterCount = computed(() =>
    countActiveFilters({
      ranges: rangeValues.value,
      statusFilter: statusFilter.value,
      includeGenres: includeGenres.value,
      excludeGenres: excludeGenres.value,
      includeTags: includeTags.value,
      excludeTags: excludeTags.value,
      excludeSequels: excludeSequels.value,
      favourite: favourite.value,
    })
  );

  const badgeLabel = computed(() => {
    const preset = props.activePresetName;
    if (preset) return preset.length > 12 ? `${preset.slice(0, 11)}…` : preset;
    return activeFilterCount.value > 0 ? String(activeFilterCount.value) : null;
  });

  const deckCountLabel = computed(() => (props.deckCount == null ? 'Show results' : `Show ${props.deckCount.toLocaleString()} decks`));

  const toggle = (event: Event) => {
    if (isMobile.value) {
      mobilePane.value = null;
      drawerOpen.value = true;
      return;
    }
    popover.value?.toggle(event);
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

  <Popover v-if="!isMobile" ref="popover" class="w-[min(48rem,calc(100vw_-_2rem))]">
    <div class="flex max-h-[calc(100dvh-16rem)] min-w-[280px] flex-col">
      <div v-if="$slots.presets" class="shrink-0 border-b border-surface-200 pb-2 dark:border-surface-700">
        <slot name="presets" />
      </div>

      <div class="flex min-h-0 flex-1 flex-col overflow-y-auto py-1.5 pr-1 [scrollbar-width:thin]">
        <MediaListFilterBody
          v-model:ranges="rangeValues"
          v-model:search="filterSearchQuery"
          v-model:expanded-key="expandedKey"
          v-model:status-filter="statusFilter"
          v-model:exclude-sequels="excludeSequels"
          v-model:favourite="favourite"
          v-model:exclude-not-originally-jp="excludeNotOriginallyJp"
          class="min-h-0 flex-1"
          split
          :is-connected="isConnected"
          :char-count-steps="CHAR_COUNT_STEPS"
          :status-options="statusFilterOptions"
        >
          <template #panes>
            <template v-if="showGenreSection">
              <div class="mt-1 mb-1 shrink-0 px-1 text-[11px] leading-none font-semibold tracking-wider text-surface-500 uppercase">Genres</div>
              <MediaListTagCloud
                v-model:search="genreSearchQuery"
                class="shrink-0 [&_.p-tag]:px-1.5 [&_.p-tag]:py-0.5 [&_.p-tag]:text-xs"
                :entries="genreEntries"
                :show-search="false"
                empty-label="No genre matches that search."
                @set="setGenreState"
              />
            </template>

            <template v-if="showTagSection">
              <div class="flex min-h-0 flex-1 flex-col">
                <div class="mt-3 mb-1 shrink-0 px-1 text-[11px] leading-none font-semibold tracking-wider text-surface-500 uppercase">Tags</div>
                <div class="relative min-h-52 flex-1">
                  <MediaListTagCloud
                    v-model:search="tagSearchQuery"
                    class="absolute inset-0 [&_.p-tag]:px-1.5 [&_.p-tag]:py-0.5 [&_.p-tag]:text-xs"
                    :entries="tagEntries"
                    :placeholder="`Search ${tags.length} tags...`"
                    empty-label="No tag matches that search."
                    list-class="min-h-0 flex-1 overflow-y-auto rounded border border-surface-200 dark:border-surface-700"
                    @set="setTagState"
                  />
                </div>
              </div>
            </template>
          </template>
        </MediaListFilterBody>
      </div>

      <div class="flex shrink-0 items-center border-t border-surface-200 pt-1.5 dark:border-surface-700">
        <span class="text-sm text-surface-500">{{ activeFilterCount }} {{ activeFilterCount === 1 ? 'filter' : 'filters' }} active</span>
        <Button severity="danger" size="small" class="ml-auto" @click="handleReset">
          <Icon name="material-symbols:refresh" />
          Reset all filters
        </Button>
      </div>
    </div>
  </Popover>

  <Drawer
    v-else
    v-model:visible="drawerOpen"
    position="bottom"
    :pt="{ root: { class: 'h-[90dvh]! rounded-t-xl overflow-hidden' } }"
    aria-label="Filters"
  >
    <template #container="{ closeCallback }">
      <div class="flex h-full min-h-0 flex-col">
        <div class="flex shrink-0 justify-center pt-2 pb-1">
          <div class="h-1 w-9 rounded-full bg-surface-300 dark:bg-surface-600" />
        </div>

        <template v-if="mobilePane === null">
          <div class="flex shrink-0 items-baseline gap-2 px-4 pb-2">
            <span class="text-lg font-bold text-surface-900 dark:text-surface-0">Filters</span>
            <span class="text-sm text-surface-500">{{ activeFilterCount }} active</span>
            <Button severity="danger" text size="small" class="ml-auto" @click="handleReset">Reset</Button>
          </div>

          <div class="min-h-0 flex-1 overflow-y-auto px-3 pb-3 [scrollbar-width:thin]">
            <div v-if="$slots.presets" class="mb-3 border-b border-surface-200 pb-3 dark:border-surface-700">
              <slot name="presets" />
            </div>

            <MediaListFilterBody
              v-model:ranges="rangeValues"
              v-model:search="filterSearchQuery"
              v-model:expanded-key="expandedKey"
              v-model:status-filter="statusFilter"
              v-model:exclude-sequels="excludeSequels"
              v-model:favourite="favourite"
              v-model:exclude-not-originally-jp="excludeNotOriginallyJp"
              mobile
              :is-connected="isConnected"
              :char-count-steps="CHAR_COUNT_STEPS"
              :status-options="statusFilterOptions"
            >
              <template #before>
                <button
                  type="button"
                  class="flex h-11 cursor-pointer items-center gap-2 rounded px-2 text-left hover:bg-surface-50 dark:hover:bg-surface-800"
                  @click="mobilePane = 'genres'"
                >
                  <span class="text-sm font-semibold text-surface-700 dark:text-surface-200">Genres</span>
                  <span
                    :class="[
                      'ml-auto truncate text-[13px]',
                      genreSummary ? 'font-semibold text-purple-700 dark:text-purple-300' : 'text-surface-400 dark:text-surface-500',
                    ]"
                  >
                    {{ genreSummary ?? 'Any' }}
                  </span>
                  <Icon name="material-symbols:chevron-right-rounded" class="shrink-0 text-surface-400" size="1.1em" />
                </button>
                <button
                  type="button"
                  class="flex h-11 cursor-pointer items-center gap-2 rounded px-2 text-left hover:bg-surface-50 dark:hover:bg-surface-800"
                  @click="mobilePane = 'tags'"
                >
                  <span class="text-sm font-semibold text-surface-700 dark:text-surface-200">Tags</span>
                  <span
                    :class="[
                      'ml-auto truncate text-[13px]',
                      tagSummary ? 'font-semibold text-purple-700 dark:text-purple-300' : 'text-surface-400 dark:text-surface-500',
                    ]"
                  >
                    {{ tagSummary ?? 'Any' }}
                  </span>
                  <Icon name="material-symbols:chevron-right-rounded" class="shrink-0 text-surface-400" size="1.1em" />
                </button>
              </template>
            </MediaListFilterBody>
          </div>
        </template>

        <template v-else-if="mobilePane === 'genres'">
          <div class="flex shrink-0 items-center gap-2 px-4 pb-2">
            <Button text severity="secondary" size="small" class="px-1!" aria-label="Back to filters" @click="mobilePane = null">
              <Icon name="material-symbols:arrow-back-rounded" size="1.3em" />
            </Button>
            <span class="text-lg font-bold text-surface-900 dark:text-surface-0">Genres</span>
            <span class="text-sm text-surface-500">{{ selectedGenreEntries.length }} selected</span>
            <Button severity="danger" text size="small" class="ml-auto" @click="clearGenres">Clear</Button>
          </div>
          <MediaListTagCloud
            v-model:search="genreSearchQuery"
            class="min-h-0 flex-1 px-4 pb-3"
            :entries="genreEntries"
            :selected-entries="selectedGenreEntries"
            placeholder="Search genres..."
            empty-label="No genre matches that search."
            list-class="min-h-0 flex-1 overflow-y-auto"
            @set="setGenreState"
          />
        </template>

        <template v-else>
          <div class="flex shrink-0 items-center gap-2 px-4 pb-2">
            <Button text severity="secondary" size="small" class="px-1!" aria-label="Back to filters" @click="mobilePane = null">
              <Icon name="material-symbols:arrow-back-rounded" size="1.3em" />
            </Button>
            <span class="text-lg font-bold text-surface-900 dark:text-surface-0">Tags</span>
            <span class="text-sm text-surface-500">{{ selectedTagEntries.length }} selected</span>
            <Button severity="danger" text size="small" class="ml-auto" @click="clearTags">Clear</Button>
          </div>
          <MediaListTagCloud
            v-model:search="tagSearchQuery"
            class="min-h-0 flex-1 px-4 pb-3"
            :entries="tagEntries"
            :selected-entries="selectedTagEntries"
            :placeholder="`Search ${tags.length} tags...`"
            empty-label="No tag matches that search."
            list-class="min-h-0 flex-1 overflow-y-auto"
            @set="setTagState"
          />
        </template>

        <div class="shrink-0 border-t border-surface-200 px-4 pt-3 pb-7 dark:border-surface-700">
          <Button class="w-full justify-center" @click="closeCallback">{{ deckCountLabel }}</Button>
        </div>
      </div>
    </template>
  </Drawer>
</template>
