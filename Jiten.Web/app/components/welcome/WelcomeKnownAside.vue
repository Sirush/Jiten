<script setup lang="ts">
  import type { CardBatch, PaginatedResponse } from '~/types/types';

  const props = defineProps<{ since: string | null; refreshKey: number }>();

  const { $api } = useNuxtApp();
  const { totalWordsAmount, fetchKnownWordsAmount } = useVocabularyStats();

  const batches = ref<CardBatch[]>([]);
  const loaded = ref(false);

  const sessionBatches = computed(() => {
    const since = props.since ? new Date(props.since).getTime() : 0;
    return batches.value.filter((b) => !b.undone && b.wordCount > 0 && new Date(b.createdAt).getTime() >= since - 60_000);
  });

  async function load() {
    try {
      const [, page] = await Promise.all([fetchKnownWordsAmount(), $api<PaginatedResponse<CardBatch[]>>('user/vocabulary/batches', { query: { limit: 10 } })]);
      batches.value = page.data;
    } catch {
      batches.value = [];
    } finally {
      loaded.value = true;
    }
  }

  onMounted(load);
  watch(() => props.refreshKey, load);
</script>

<template>
  <aside class="rounded-xl border border-gray-200 dark:border-neutral-700 bg-white dark:bg-neutral-900 p-5 flex flex-col gap-3.5">
    <span class="text-[11px] font-semibold uppercase tracking-wide text-gray-500 dark:text-gray-400">Your known words</span>
    <div v-if="loaded" class="flex items-baseline gap-2">
      <span class="text-4xl font-bold tabular-nums">{{ (totalWordsAmount ?? 0).toLocaleString() }}</span>
      <span class="text-sm text-gray-600 dark:text-gray-300">words</span>
    </div>
    <Skeleton v-else width="8rem" height="2.5rem" />
    <ul v-if="sessionBatches.length > 0" class="flex flex-col gap-2 text-sm">
      <li v-for="batch in sessionBatches" :key="batch.batchId" class="flex justify-between gap-3">
        <span class="text-gray-700 dark:text-gray-300 truncate">{{ cardSourceDisplay(batch.source).label }}</span>
        <span class="font-semibold tabular-nums">{{ batch.wordCount.toLocaleString() }}</span>
      </li>
    </ul>
    <p
      v-if="sessionBatches.length > 0"
      class="flex items-center gap-2.5 px-3 py-2.5 rounded-lg bg-gray-100 dark:bg-neutral-800 text-[13px] text-gray-700 dark:text-gray-300"
    >
      <Icon name="material-symbols:sync-rounded" class="shrink-0 text-primary-600 dark:text-primary-300" />
      Your coverage can take a few minutes to update.
    </p>
    <p class="text-[13px] text-gray-600 dark:text-gray-300">This is an estimate. You can undo those from the settings page.</p>
  </aside>
</template>
