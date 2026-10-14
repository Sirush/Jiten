<script setup lang="ts">
  import type { SiteStatsWordsForCoverage } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { roundToHundred } from '~/utils/siteStats';

  const props = defineProps<{
    data: SiteStatsWordsForCoverage;
  }>();

  const SHADES = [
    'bg-primary-50 dark:bg-primary-950/60',
    'bg-primary-100 dark:bg-primary-950',
    'bg-primary-200 dark:bg-primary-900',
    'bg-primary-300 dark:bg-primary-800',
  ];

  const SHOWN_LEVELS = [80, 85, 90, 95, 98, 99];

  const columns = computed(() => {
    const all = props.data.levels.map((level, index) => ({ level, index }));
    const shown = all.filter((c) => SHOWN_LEVELS.includes(c.level));
    return shown.length ? shown : all;
  });

  const rows = computed(() => {
    const sortIndex = columns.value[Math.min(1, columns.value.length - 1)]?.index ?? 0;
    return props.data.mediaTypes
      .map((t) => ({ mediaType: t.mediaType, ranks: columns.value.map((c) => t.ranks[c.index] ?? null), sortBy: t.ranks[sortIndex] ?? Infinity }))
      .sort((a, b) => a.sortBy - b.sortBy);
  });

  const logRange = computed(() => {
    const values = rows.value.flatMap((r) => r.ranks).filter((v): v is number => v !== null && v > 0);
    return { min: Math.log(Math.min(...values)), max: Math.log(Math.max(...values)) };
  });

  const shade = (value: number | null) => {
    if (value === null) return '';
    const { min, max } = logRange.value;
    const t = max > min ? (Math.log(value) - min) / (max - min) : 0;
    return SHADES[Math.min(SHADES.length - 1, Math.floor(t * SHADES.length))];
  };
</script>

<template>
  <StatsSection
    v-if="rows.length && columns.length"
    title="How many words you need"
    subtitle="How far down Jiten's frequency list you need to know every word to understand this share of the words in a typical title"
    note="Typical means the median title of each type. Only taking into account words used in at least 1% of a type's titles count."
  >
    <div class="overflow-x-auto">
      <table class="w-full min-w-[22rem] border-collapse text-sm">
        <thead>
          <tr class="text-xs text-gray-600 dark:text-gray-400">
            <th scope="col" class="text-left font-medium py-1.5 pr-3">Media type</th>
            <th v-for="column in columns" :key="column.level" scope="col" class="text-right font-medium py-1.5 px-2">{{ column.level }}%</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in rows" :key="row.mediaType" class="border-t border-surface-200 dark:border-surface-700">
            <th scope="row" class="text-left font-normal py-1.5 pr-3 whitespace-nowrap">{{ getMediaTypeText(row.mediaType) }}</th>
            <td v-for="(rank, i) in row.ranks" :key="i" class="text-right tabular-nums font-medium py-1.5 px-2" :class="shade(rank)">
              {{ rank === null ? '-' : roundToHundred(rank).toLocaleString('en-US') }}
            </td>
          </tr>
        </tbody>
      </table>
    </div>
  </StatsSection>
</template>
