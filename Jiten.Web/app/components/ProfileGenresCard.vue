<script setup lang="ts">
  import type { ProfileGenreShare } from '~/types';
  import { genreRows } from '~/utils/profileGenres';

  const props = defineProps<{ genres: ProfileGenreShare[] }>();

  const rows = computed(() => {
    const top = genreRows(props.genres);
    const otherShare = 1 - props.genres.reduce((sum, g) => sum + g.share, 0);
    if (otherShare < 0.005) return top;
    return [
      ...top,
      {
        key: 'other',
        label: 'Other',
        swatch: 'bg-surface-400 dark:bg-surface-500',
        share: otherShare,
        percent: Math.round(otherShare * 100),
        tooltip: 'All other genres combined',
      },
    ];
  });
</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-base font-semibold">Genres</h2>
    </template>
    <template #content>
      <div class="flex h-2.5 gap-0.5 overflow-hidden rounded-full bg-surface-200 dark:bg-surface-700" aria-hidden="true">
        <div v-for="row in rows" :key="row.key" :class="row.swatch" :style="{ width: `${(row.share * 100).toFixed(2)}%` }" />
      </div>
      <ul class="mt-3 flex flex-col">
        <li v-for="row in rows" :key="row.key">
          <Tooltip :content="row.tooltip" block>
            <div
              tabindex="0"
              class="flex items-center gap-2 rounded px-1.5 py-1 text-sm -mx-1.5 hover:bg-surface-100 focus-visible:bg-surface-100 dark:hover:bg-surface-800 dark:focus-visible:bg-surface-800"
              :aria-label="`${row.label}, ${row.percent}%, ${row.tooltip.toLowerCase()}`"
            >
              <span class="h-2.5 w-2.5 shrink-0 rounded-sm" :class="row.swatch" aria-hidden="true" />
              <span class="flex-1">{{ row.label }}</span>
              <span class="tabular-nums text-muted-color">{{ row.percent }}%</span>
            </div>
          </Tooltip>
        </li>
      </ul>
    </template>
  </Card>
</template>
