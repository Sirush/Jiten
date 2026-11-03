<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { MetadataRecentItem, MetadataRecentResponse } from '~/types/metadataEdits';
  import { useApiFetch } from '~/composables/useApiFetch';
  import { useOlderPages } from '~/composables/useOlderPages';
  import { formatDayMonthTime, formatDateTime } from '~/utils/dateFormat';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { showErrorToast } from '~/utils/toast';
  import { editStatus, fieldLabel, fieldsFromFlags, lookedRightText, toggleChipClass } from '~/utils/metadataEdits';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Recent changes' });
  useRobotsRule({ noindex: true, follow: true });

  type StatusFilter = 'all' | 'live' | 'pending' | 'unreviewed' | 'reverted';

  const PAGE_SIZE = 50;

  const route = useRoute();
  const router = useRouter();
  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  const VALID: StatusFilter[] = ['all', 'live', 'pending', 'unreviewed', 'reverted'];
  const statusFilter = computed<StatusFilter>(() => {
    const value = String(route.query.status ?? 'all') as StatusFilter;
    return VALID.includes(value) ? value : 'all';
  });

  const { data, status, error, refresh } = useApiFetch<MetadataRecentResponse>('metadata-edits/recent', {
    query: { status: statusFilter, offset: 0, limit: PAGE_SIZE },
    watch: [statusFilter],
  });

  const { items, total, loadingMore, loadMore, reset } = useOlderPages(
    data,
    (offset) => $api<MetadataRecentResponse>('metadata-edits/recent', { query: { status: statusFilter.value, offset, limit: PAGE_SIZE } }),
    (err) => showErrorToast(toast, "Older changes didn't load", apiErrorMessage(err, 'Try again in a moment.'))
  );

  watch(statusFilter, reset);

  const role = computed(() => data.value?.viewerRole ?? 'user');
  const isReviewer = computed(() => role.value !== 'user');

  const filters = computed(() => [
    { value: 'all' as const, label: 'All' },
    { value: 'live' as const, label: 'Live' },
    ...(isReviewer.value ? [{ value: 'unreviewed' as const, label: 'Live, unreviewed' }] : []),
    { value: 'pending' as const, label: 'Waiting for review' },
    { value: 'reverted' as const, label: 'Reverted' },
  ]);

  function setFilter(value: StatusFilter) {
    router.replace({ query: { ...route.query, status: value === 'all' ? undefined : value } });
  }

  const changedFields = (item: MetadataRecentItem) => fieldsFromFlags(item.fields).map(fieldLabel).join(', ');

  const tableGrid = 'sm:grid sm:grid-cols-[7.5rem_minmax(0,1fr)_minmax(0,1.6fr)_8rem_auto] sm:gap-x-3';
  const rowGrid = 'sm:col-span-full sm:grid sm:grid-cols-subgrid sm:gap-x-3 sm:items-start';
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-end justify-between gap-3">
      <div class="flex flex-col gap-1 max-w-2xl">
        <h1 class="m-0 text-xl md:text-2xl font-bold">Recent changes</h1>
        <p class="m-0 text-sm text-muted-color">
          Every edit to decks across the site. Spot something wrong on a deck you know? Open its history, or check pending edits in the
          <NuxtLink to="/review">review queue</NuxtLink>.
        </p>
      </div>
      <div class="flex flex-wrap gap-1.5" role="group" aria-label="Filter by status">
        <button
          v-for="filter in filters"
          :key="filter.value"
          type="button"
          :class="toggleChipClass(statusFilter === filter.value)"
          :aria-pressed="statusFilter === filter.value"
          @click="setFilter(filter.value)"
        >
          {{ filter.label }}
        </button>
      </div>
    </div>

    <div :class="[tableGrid, 'rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900']">
      <div :class="[rowGrid, 'hidden px-4 py-2 border-b border-surface-200 dark:border-surface-700 text-xs font-semibold text-muted-color']" aria-hidden="true">
        <span>When</span>
        <span>Deck</span>
        <span>Change</span>
        <span>By</span>
        <span>Status</span>
      </div>

      <div v-if="status === 'pending' && !items.length" class="sm:col-span-full p-4 flex flex-col gap-2" aria-busy="true">
        <Skeleton v-for="i in 6" :key="i" height="2.5rem" />
      </div>
      <div v-else-if="error && !items.length" class="sm:col-span-full p-5 flex flex-col items-start gap-3">
        <p class="m-0">Recent changes didn't load.</p>
        <Button label="Retry" icon="pi pi-refresh" @click="refresh()" />
      </div>
      <p v-else-if="!items.length" class="sm:col-span-full m-0 p-6 text-center text-sm text-muted-color">
        {{ statusFilter === 'all' ? 'No edits yet.' : 'No edits match this filter.' }}
      </p>
      <ul
        v-else
        class="list-none m-0 p-0 divide-y divide-surface-200 dark:divide-surface-700 sm:col-span-full sm:grid sm:grid-cols-subgrid"
        :class="{ 'opacity-60 transition-opacity': status === 'pending' }"
      >
        <li v-for="item in items" :key="item.id" :class="[rowGrid, 'px-4 py-2.5 text-sm flex flex-col gap-1']">
          <span class="text-xs sm:text-sm text-muted-color tabular-nums" :title="formatDateTime(item.createdAt)">{{ formatDayMonthTime(item.createdAt) }}</span>
          <NuxtLink
            v-if="item.deck"
            :to="`/decks/media/${item.deck.deckId}/history`"
            class="font-medium min-w-0 [overflow-wrap:anywhere]"
            v-bind="japaneseTextAttrs(localiseTitle(item.deck))"
          >
            {{ localiseTitle(item.deck) }}
          </NuxtLink>
          <span v-else class="text-muted-color">Deleted deck</span>
          <span v-if="item.contentHidden" class="min-w-0 text-muted-color">{{ changedFields(item) }}, details hidden after the revert</span>
          <MetadataChangeList v-else-if="item.changes?.length" :changes="item.changes" compact class="min-w-0" />
          <span v-else class="min-w-0">{{ changedFields(item) }}</span>
          <span class="min-w-0 truncate text-muted-color sm:text-color">{{ item.author ?? 'Jiten' }}</span>
          <span class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <Tag :value="editStatus(item, role).label" :severity="editStatus(item, role).severity" class="!text-xs !py-0" />
            <template v-if="item.publicStatus === 'pending'">
              <span v-if="item.confirmCount" class="text-xs text-muted-color">{{ lookedRightText(item.confirmCount) }}</span>
              <NuxtLink v-if="!item.isYours" :to="{ path: '/review', query: { deckId: item.deck?.deckId, edit: item.id } }" class="text-xs font-medium">
                {{ isReviewer ? 'Review' : 'Check' }}
              </NuxtLink>
            </template>
          </span>
        </li>
        <li v-if="items.length < total" class="sm:col-span-full px-4 py-3 flex justify-center">
          <Button label="Show older changes" severity="secondary" text :loading="loadingMore" @click="loadMore" />
        </li>
      </ul>
    </div>
  </div>
</template>
