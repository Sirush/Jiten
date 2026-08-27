<script setup lang="ts">
  import type { WordSummary, UsedInPage } from '~/types';

  const props = defineProps<{
    wordId: number;
    readingIndex: number;
    initialItems: WordSummary[];
    total: number;
    highlight?: string;
    collapsedCount?: number;
    hideHeading?: boolean;
  }>();

  const convertToRuby = useConvertToRuby();

  function findSurfaceRange(furigana: string, surface: string): [number, number] | null {
    if (!surface) return null;
    for (let start = 0; start < furigana.length; start++) {
      if (furigana[start] === '[') continue;
      let i = 0;
      let j = start;
      while (j < furigana.length && i < surface.length) {
        if (furigana[j] === '[') {
          while (j < furigana.length && furigana[j] !== ']') j++;
          j++;
          continue;
        }
        if (furigana[j] !== surface[i]) break;
        i++;
        j++;
      }
      if (i === surface.length) {
        if (j < furigana.length && furigana[j] === '[') {
          while (j < furigana.length && furigana[j] !== ']') j++;
          if (j < furigana.length) j++;
        }
        return [start, j];
      }
    }
    return null;
  }

  function renderParent(component: WordSummary): string {
    const furigana = component.readingFurigana || component.reading;
    let range: [number, number] | null = null;
    if (component.matchSurface) range = findSurfaceRange(furigana, component.matchSurface);
    if (!range && props.highlight) {
      const idx = furigana.indexOf(props.highlight);
      if (idx !== -1) range = [idx, idx + props.highlight.length];
    }
    if (!range) return convertToRuby(furigana);
    const [start, end] = range;
    const marked =
      furigana.slice(0, start) +
      `<span class="underline decoration-primary-500 decoration-2 underline-offset-4">${furigana.slice(start, end)}</span>` +
      furigana.slice(end);
    return convertToRuby(marked);
  }

  const collapsedCount = props.collapsedCount ?? 4;
  const expanded = ref(false);
  const fetchedItems = ref<WordSummary[] | null>(null);
  const loading = ref(false);

  const visibleItems = computed(() => {
    const source = fetchedItems.value ?? props.initialItems;
    return expanded.value ? source : source.slice(0, collapsedCount);
  });

  const remaining = computed(() => Math.max(0, props.total - collapsedCount));

  watch(
    () => [props.wordId, props.readingIndex],
    () => {
      expanded.value = false;
      fetchedItems.value = null;
    }
  );

  const { $api } = useNuxtApp();

  async function expand() {
    expanded.value = true;
    if (props.total <= (fetchedItems.value ?? props.initialItems).length) return;
    loading.value = true;
    try {
      const data = await ($api as typeof $fetch)<UsedInPage>(`vocabulary/${props.wordId}/${props.readingIndex}/used-in`);
      if (data) fetchedItems.value = data.items;
    } finally {
      loading.value = false;
    }
  }
</script>

<template>
  <div v-if="total > 0" class="mt-4">
    <h3 v-if="!hideHeading" class="text-gray-500 dark:text-gray-300 font-noto-sans text-sm mb-2">Used in {{ total }} word{{ total === 1 ? '' : 's' }}</h3>
    <div class="flex flex-wrap gap-x-6 gap-y-3 transition-opacity" :class="{ 'opacity-60': loading }">
      <div
        v-for="component in visibleItems"
        :key="`${component.wordId}-${component.readingIndex}`"
        class="flex items-start gap-3 py-1 border-b border-surface-200/60 dark:border-surface-700/60 basis-[22rem] grow shrink-0 max-w-full"
      >
        <NuxtLink
          :to="`/vocabulary/${component.wordId}/${component.readingIndex}`"
          class="text-lg font-medium hover:text-primary-600 dark:hover:text-primary-400 transition-colors whitespace-nowrap !no-underline hover:!no-underline self-end"
        >
          <span lang="ja" v-html="renderParent(component)" />
        </NuxtLink>
        <div class="flex-1 min-w-0 flex flex-col">
          <span v-if="component.frequencyRank" class="text-[10px] text-surface-500 dark:text-surface-400 leading-none self-end">
            #{{ component.frequencyRank.toLocaleString() }}
          </span>
          <span v-if="component.mainDefinition" class="text-surface-600 dark:text-surface-400 text-xs line-clamp-2 mt-0.5">
            {{ component.mainDefinition }}
          </span>
        </div>
      </div>
    </div>

    <button
      v-if="!expanded && remaining > 0"
      type="button"
      class="mt-3 text-sm text-primary-600 dark:text-primary-400 hover:underline disabled:opacity-60"
      :disabled="loading"
      @click="expand"
    >
      {{ loading ? 'Loading…' : `View ${remaining} more…` }}
    </button>

    <button
      v-if="expanded && total > collapsedCount"
      type="button"
      class="mt-3 text-sm text-primary-600 dark:text-primary-400 hover:underline"
      @click="expanded = false"
    >
      View less
    </button>
  </div>
</template>

<style scoped>
  :deep(rt) {
    font-size: 0.55em !important;
  }
</style>
