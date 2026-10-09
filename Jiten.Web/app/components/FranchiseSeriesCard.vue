<script setup lang="ts">
  import type { FranchiseNode } from '~/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { releaseYearOf } from '~/utils/franchiseLayout';
  import { coverUrl } from '~/utils/coverImage';

  const props = defineProps<{
    node: FranchiseNode;
    current: boolean;
    showCoverage: boolean;
  }>();

  const localiseTitle = useLocaliseTitle();
  const title = computed(() => localiseTitle(props.node));
  const cover = computed(() => coverUrl(props.node.coverName));
  const coveragePercent = computed(() => Math.min(100, props.node.coverage).toFixed(0));
</script>

<template>
  <NuxtLink
    :data-franchise-deck="node.deckId"
    :to="`/decks/media/${node.deckId}/detail`"
    class="flex gap-2 overflow-hidden rounded border bg-surface-0 p-[3px] pr-2 transition dark:bg-surface-900"
  >
    <span v-if="current" class="absolute top-[3px] left-[3px] z-[1] rounded-sm bg-primary px-1 text-[10px] leading-4 font-bold text-primary-contrast"
      >Here</span
    >
    <img :src="cover" :alt="title" class="h-20 w-[60px] shrink-0 rounded-sm object-cover" loading="lazy" decoding="async" width="60" height="80" />
    <span class="flex min-w-0 flex-1 flex-col pt-0.5">
      <span class="text-[11px] text-gray-500 dark:text-gray-400">{{ getMediaTypeText(node.mediaType) }}, {{ releaseYearOf(node.releaseDate) ?? '?' }}</span>
      <span class="line-clamp-2 text-xs leading-tight font-medium" :title="title" v-bind="japaneseTextAttrs(title)">{{ title }}</span>
      <span class="mt-auto flex items-baseline gap-2 pb-1.5 text-[11px] tabular-nums">
        <DifficultyDisplay v-if="node.difficulty >= 0" :difficulty="node.difficulty" :difficulty-raw="node.difficultyRaw" class="text-[11px]" />
        <span v-if="showCoverage" class="text-gray-500 dark:text-gray-400">{{ coveragePercent }}%</span>
      </span>
    </span>
    <CoverageStrip v-if="showCoverage" :coverage="node.coverage" class="absolute inset-x-0 bottom-0" />
  </NuxtLink>
</template>
