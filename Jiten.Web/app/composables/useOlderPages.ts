import type { Ref } from 'vue';

interface Page<T> {
  items: T[];
  total: number;
}

/** Appends older pages to a first page fetched with useApiFetch, which stays the source: SSR never re-runs watchers, so a copy would render empty on the server. */
export function useOlderPages<T extends { id: number }>(
  first: Ref<Page<T> | null | undefined>,
  fetchPage: (offset: number) => Promise<Page<T>>,
  onError: (err: unknown) => void
) {
  const olderItems = ref([]) as Ref<T[]>;
  const olderTotal = ref<number | null>(null);
  const loadingMore = ref(false);

  const items = computed(() => {
    const head = first.value?.items ?? [];
    const seen = new Set(head.map((i) => i.id));
    return [...head, ...olderItems.value.filter((i) => !seen.has(i.id))];
  });
  const total = computed(() => olderTotal.value ?? first.value?.total ?? 0);

  function reset() {
    olderItems.value = [];
    olderTotal.value = null;
  }

  async function loadMore() {
    if (loadingMore.value || items.value.length >= total.value) return;
    loadingMore.value = true;
    try {
      const page = await fetchPage(items.value.length);
      olderItems.value = [...olderItems.value, ...page.items];
      olderTotal.value = page.total;
    } catch (err) {
      onError(err);
    } finally {
      loadingMore.value = false;
    }
  }

  return { items, total, loadingMore, loadMore, reset };
}
