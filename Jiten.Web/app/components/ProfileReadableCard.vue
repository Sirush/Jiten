<script setup lang="ts">
  import type { ProfileReadable } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { formatDateLong } from '~/utils/dateFormat';

  const props = defineProps<{
    readable: ProfileReadable;
    /** Null on the owner's own profile. */
    name: string | null;
  }>();

  /** Below this many titles a share says more about the catalogue than about the reader. */
  const MIN_TITLES = 10;

  const SHADES = [
    'bg-primary-50 dark:bg-primary-950/60',
    'bg-primary-100 dark:bg-primary-950',
    'bg-primary-200 dark:bg-primary-900',
    'bg-primary-300 dark:bg-primary-800',
  ];

  const withYoung = ref(true);

  const subject = computed(() => (props.name ? `${props.name} knows` : 'you know'));

  const rows = computed(() =>
    props.readable.mediaTypes
      .map((t) => {
        const counts = withYoung.value ? t.withYoung : t.mature;
        const tooSmall = t.mediaType !== null && t.titles < MIN_TITLES;
        return {
          key: t.mediaType ?? 'all',
          name: t.mediaType === null ? 'All titles' : getMediaTypeText(t.mediaType),
          isTotal: t.mediaType === null,
          titles: t.titles,
          cells: counts.map((count) => ({
            count,
            share: tooSmall || t.titles === 0 ? null : count / t.titles,
          })),
        };
      })
      .sort((a, b) => (a.isTotal ? -1 : b.isTotal ? 1 : a.name.localeCompare(b.name)))
  );

  const maxShare = computed(() => Math.max(0, ...rows.value.flatMap((r) => r.cells.map((c) => c.share ?? 0))));

  function shade(share: number | null) {
    if (share === null || share === 0 || maxShare.value === 0) return '';
    return SHADES[Math.min(SHADES.length - 1, Math.floor((share / maxShare.value) * SHADES.length))];
  }

  function formatShare(share: number) {
    if (share === 0) return '0%';
    if (share < 0.01) return '<1%';
    return `${Math.round(share * 100)}%`;
  }
</script>

<template>
  <Card class="min-w-0">
    <template #content>
      <section aria-labelledby="readable-heading" class="flex min-w-0 flex-col gap-3">
        <header class="flex flex-wrap items-start justify-between gap-x-4 gap-y-2">
          <div class="min-w-0">
            <h2 id="readable-heading" class="text-base font-semibold">{{ name ? `What ${name} can read` : 'What you can read' }}</h2>
            <p class="mt-0.5 text-sm text-muted-color">Share of the titles on Jiten in which {{ subject }} at least this percentage of the words.</p>
          </div>
          <label class="flex items-center gap-2 text-sm">
            <ToggleSwitch v-model="withYoung" input-id="readable-young" />
            <span>Count young cards</span>
          </label>
        </header>

        <div class="overflow-x-auto">
          <table class="w-full border-collapse text-sm max-sm:text-xs">
            <thead>
              <tr class="text-xs text-muted-color">
                <th scope="col" class="py-1.5 pr-2 text-left font-medium sm:pr-3">Media type</th>
                <th scope="col" class="px-1.5 py-1.5 text-right font-medium sm:px-2">Titles</th>
                <th v-for="level in readable.levels" :key="level" scope="col" class="px-1.5 py-1.5 text-right font-medium sm:px-2">{{ level }}%+</th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="row in rows"
                :key="row.key"
                class="border-t border-surface-200 dark:border-surface-700"
                :class="{ 'font-semibold': row.isTotal }"
              >
                <th scope="row" class="whitespace-nowrap py-1.5 pr-2 text-left sm:pr-3" :class="row.isTotal ? 'font-semibold' : 'font-normal'">{{ row.name }}</th>
                <td class="px-1.5 py-1.5 text-right tabular-nums text-muted-color sm:px-2">{{ row.titles.toLocaleString('en-US') }}</td>
                <td v-for="(cell, i) in row.cells" :key="i" class="px-1.5 py-1.5 text-right tabular-nums sm:px-2" :class="shade(cell.share)">
                  <Tooltip v-if="cell.share !== null" :content="`${cell.count.toLocaleString('en-US')} of ${row.titles.toLocaleString('en-US')} titles`">
                    <span class="font-medium">{{ formatShare(cell.share) }}</span>
                  </Tooltip>
                  <span v-else class="text-muted-color">-</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
    </template>
  </Card>
</template>
