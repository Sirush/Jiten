import type { PaginatedResponse } from '~/types/types';

export function usePagination<T>(response: Ref<PaginatedResponse<T> | null | undefined>) {
  const route = useRoute();

  const pageSize = computed(() => response.value?.pageSize ?? 100);
  const totalItems = computed(() => response.value?.totalItems ?? 0);
  const currentPage = computed(() => {
    const r = response.value;
    return r ? Math.floor(r.currentOffset / r.pageSize) + 1 : 1;
  });
  const totalPages = computed(() => Math.ceil(totalItems.value / pageSize.value));

  const start = computed(() => (currentPage.value - 1) * pageSize.value + 1);
  const end = computed(() => Math.min(currentPage.value * pageSize.value, totalItems.value));

  const previousLink = computed(() => {
    const r = response.value;
    if (!r || currentPage.value <= 1) return null;
    return { query: { ...route.query, offset: Math.max(0, r.currentOffset - r.pageSize) } };
  });

  const nextLink = computed(() => {
    const r = response.value;
    if (!r || currentPage.value >= totalPages.value) return null;
    return { query: { ...route.query, offset: Math.min(r.totalItems, r.currentOffset + r.pageSize) } };
  });

  const pageLinkFor = (page: number) => ({ query: { ...route.query, offset: Math.max(0, (page - 1) * pageSize.value) } });

  return { currentPage, totalPages, pageSize, totalItems, start, end, previousLink, nextLink, pageLinkFor };
}
