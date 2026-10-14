<script setup lang="ts">
  import type { SiteStatsReading } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { formatCompact, isWatchedType, watchedFigure } from '~/utils/siteStats';

  const props = defineProps<{
    rows: SiteStatsReading[];
  }>();

  interface Bar {
    key: string;
    label: string;
    value: number;
    display: string;
    detail: string;
  }

  const learnerText = (n: number) => (n > 0 ? `${n.toLocaleString('en-US')} ${n === 1 ? 'learner' : 'learners'}` : '');
  const typeLabel = (row: SiteStatsReading) => (row.mediaType === null ? 'Other' : getMediaTypeText(row.mediaType));

  const read = computed<Bar[]>(() =>
    props.rows
      .filter((r) => !isWatchedType(r.mediaType) && r.characters > 0)
      .sort((a, b) => (a.mediaType === null ? 1 : b.mediaType === null ? -1 : b.characters - a.characters))
      .map((r) => ({ key: String(r.mediaType), label: typeLabel(r), value: r.characters, display: formatCompact(r.characters), detail: learnerText(r.learners) }))
  );

  const watched = computed<Bar[]>(() =>
    props.rows
      .filter((r) => isWatchedType(r.mediaType))
      .map((r) => ({ row: r, figure: watchedFigure(r) }))
      .filter(({ figure }) => figure.value > 0)
      .sort((a, b) => b.figure.value - a.figure.value)
      .map(({ row, figure }) => ({
        key: String(row.mediaType),
        label: typeLabel(row),
        value: figure.value,
        display: figure.value.toLocaleString('en-US'),
        detail: [figure.unit, learnerText(row.learners)].filter(Boolean).join(', '),
      }))
  );

  const groups = computed(() =>
    [
      { title: 'Characters read', bars: read.value },
      { title: 'Watched', bars: watched.value },
    ].filter((g) => g.bars.length)
  );

  const width = (bar: Bar, bars: Bar[]) => `${Math.max(1, (bar.value / Math.max(...bars.map((b) => b.value))) * 100)}%`;
</script>

<template>
  <StatsSection v-if="groups.length" title="What learners read and watch">
    <div class="grid grid-cols-1 lg:grid-cols-2 gap-6">
      <div v-for="group in groups" :key="group.title" class="min-w-0">
        <h3 class="text-sm font-semibold mb-2">{{ group.title }}</h3>
        <ul class="list-none m-0 p-0 flex flex-col gap-2.5">
          <li v-for="bar in group.bars" :key="bar.key" class="grid items-center gap-3" style="grid-template-columns: 6.5rem minmax(0, 1fr) auto">
            <span class="text-sm truncate">{{ bar.label }}</span>
            <span class="h-3.5 rounded-sm bg-primary-100 dark:bg-primary-950 overflow-hidden" aria-hidden="true">
              <span class="block h-full rounded-sm bg-primary-500 dark:bg-primary-400" :style="{ width: width(bar, group.bars) }" />
            </span>
            <span class="text-right leading-tight min-w-[4.5rem]">
              <span class="block text-sm font-bold tabular-nums">{{ bar.display }}</span>
              <span v-if="bar.detail" class="block text-xs text-gray-500 dark:text-gray-400">{{ bar.detail }}</span>
            </span>
          </li>
        </ul>
      </div>
    </div>
  </StatsSection>
</template>
