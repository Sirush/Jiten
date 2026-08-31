<script setup lang="ts">
  import type { Ref } from 'vue';
  import { useApiFetch } from '~/composables/useApiFetch';
  import type { Tag } from '~/types';
  import { getGenreText } from '~/utils/genreMapper';
  import { NOT_ORIGINALLY_JP_TAG_ID } from '~/utils/tags';
  import { buildRangeChips, readRangeBounds, type MediaRangeRefs } from '~/utils/mediaFilterRanges';

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

  const { data: availableTags } = useApiFetch<Tag[]>('media-deck/tags', { server: true, lazy: false });

  const statusChipLabels: Record<string, string> = {
    nostatus: 'Without status',
    ignore: 'Ignored',
    planning: 'Planning',
    ongoing: 'Ongoing',
    completed: 'Completed',
    dropped: 'Dropped',
  };

  const tagName = (tagId: number) => availableTags.value?.find((tag) => tag.tagId === tagId)?.name ?? `Tag ${tagId}`;

  const removeFrom = (list: Ref<number[]>, id: number) => {
    list.value = list.value.filter((entry) => entry !== id);
  };

  type FilterChip = { key: string; label: string; excluded: boolean; clear: () => void };

  const chips = computed<FilterChip[]>(() => {
    const result: FilterChip[] = [];

    if (statusFilter.value !== 'none') {
      result.push({
        key: 'status',
        label: statusChipLabels[statusFilter.value] ?? 'Status',
        excluded: false,
        clear: () => {
          statusFilter.value = 'none';
        },
      });
    }

    for (const range of buildRangeChips(readRangeBounds(rangeBounds))) {
      const bounds = rangeBounds[range.key];
      result.push({
        key: range.key,
        label: range.label,
        excluded: false,
        clear: () => {
          bounds.min.value = null;
          bounds.max.value = null;
        },
      });
    }

    if (favourite.value) {
      result.push({
        key: 'favourite',
        label: 'Favourited',
        excluded: false,
        clear: () => {
          favourite.value = null;
        },
      });
    }

    if (excludeSequels.value) {
      result.push({
        key: 'excludeSequels',
        label: 'No sequels or fandiscs',
        excluded: true,
        clear: () => {
          excludeSequels.value = null;
        },
      });
    }

    for (const id of includeGenres.value) {
      result.push({ key: `genre-${id}`, label: `Genre: ${getGenreText(id)}`, excluded: false, clear: () => removeFrom(includeGenres, id) });
    }
    for (const id of excludeGenres.value) {
      result.push({ key: `genre-x-${id}`, label: `Not: ${getGenreText(id)}`, excluded: true, clear: () => removeFrom(excludeGenres, id) });
    }
    for (const id of includeTags.value) {
      result.push({ key: `tag-${id}`, label: `Tag: ${tagName(id)}`, excluded: false, clear: () => removeFrom(includeTags, id) });
    }
    for (const id of excludeTags.value) {
      const label = id === NOT_ORIGINALLY_JP_TAG_ID ? 'Originally Japanese only' : `Not: ${tagName(id)}`;
      result.push({ key: `tag-x-${id}`, label, excluded: true, clear: () => removeFrom(excludeTags, id) });
    }

    return result;
  });

  const chipClass = (excluded: boolean) =>
    excluded
      ? 'border-red-200 bg-red-50 text-red-700 dark:border-red-900 dark:bg-red-950/40 dark:text-red-300'
      : 'border-purple-200 bg-purple-50 text-purple-700 dark:border-purple-900 dark:bg-purple-950/40 dark:text-purple-300';
</script>

<template>
  <div v-if="chips.length" class="flex flex-row flex-wrap items-center gap-1.5">
    <span class="mr-0.5 inline-flex items-center gap-1.5 text-[13px] text-surface-500">
      <Icon name="material-symbols:filter-list" size="1.1em" />
      {{ chips.length }} {{ chips.length === 1 ? 'filter' : 'filters' }}
    </span>

    <button
      v-for="chip in chips"
      :key="chip.key"
      type="button"
      :class="['inline-flex cursor-pointer items-center gap-1.5 rounded border px-2 py-0.5 text-[13px] font-semibold', chipClass(chip.excluded)]"
      :aria-label="`Remove filter ${chip.label}`"
      @click="chip.clear()"
    >
      {{ chip.label }}
      <Icon name="material-symbols:close-rounded" size="0.9em" />
    </button>

    <Button v-if="chips.length > 1" severity="danger" text size="small" class="py-0.5! text-[13px]!" @click="emit('reset')">Clear all</Button>
  </div>
</template>
