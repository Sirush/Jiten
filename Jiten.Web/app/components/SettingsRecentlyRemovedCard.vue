<script setup lang="ts">
  import { CardArchiveReason, type FsrsState } from '~/types/enums';

  withDefaults(defineProps<{ archivedCount?: number; countLoading?: boolean }>(), {
    archivedCount: 0,
    countLoading: false,
  });

  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const convertToRuby = useConvertToRuby();

  interface ArchivedCard {
    wordId: number;
    readingIndex: number;
    reading: string;
    mainDefinition: string | null;
    frequencyRank: number;
    archivedAt: string;
    reason: CardArchiveReason;
    coveringReadingIndex: number | null;
    coveringReading: string | null;
    state: FsrsState;
    reviewCount: number;
    firstReview: string | null;
    lastReview: string | null;
    lapses: number;
    historyTruncated: boolean;
    autoRestores: boolean;
  }

  type ArchivedRow = ArchivedCard & { key: string; rubyHtml: string; plainReading: string };

  const PAGE_SIZE = 50;
  const PAGE_SIZE_OPTIONS = [50, 100, 200];

  const loading = ref(false);
  const busy = ref(false);
  const showDialog = ref(false);
  const rows = ref<ArchivedRow[]>([]);
  const selectedKeys = ref(new Set<string>());
  const totalItems = ref(0);
  const offset = ref(0);
  const reasonFilter = ref<CardArchiveReason | null>(null);
  const pageSize = ref(PAGE_SIZE);
  // Selection lives on the loaded page, so acting on everything the filter matches is a separate mode
  // rather than a selection the user cannot see.
  const selectAllMatching = ref(false);

  const selected = computed(() => rows.value.filter((r) => selectedKeys.value.has(r.key)));
  const pageFullySelected = computed(() => rows.value.length > 0 && selected.value.length === rows.value.length);

  // Unticking a row is a clear signal the user no longer means "everything".
  watch(pageFullySelected, (full) => {
    if (!full) selectAllMatching.value = false;
  });

  function toggle(key: string) {
    const next = new Set(selectedKeys.value);
    if (!next.delete(key)) next.add(key);
    selectedKeys.value = next;
  }

  function togglePage() {
    selectedKeys.value = pageFullySelected.value ? new Set() : new Set(rows.value.map((r) => r.key));
  }

  function selectEverything() {
    selectedKeys.value = new Set(rows.value.map((r) => r.key));
    selectAllMatching.value = true;
  }

  function selectPageOnly() {
    selectAllMatching.value = false;
  }

  const hasMorePages = computed(() => totalItems.value > rows.value.length);
  const actionCount = computed(() => (selectAllMatching.value ? totalItems.value : selected.value.length));

  const reasonOptions = [
    { label: 'Every reason', value: null },
    { label: 'Replaced by another spelling', value: CardArchiveReason.KanaRedundancy },
    { label: 'Tidied up after an import', value: CardArchiveReason.FormPrune },
    { label: 'Removed from the redundant forms list', value: CardArchiveReason.RedundancyResolve },
    { label: 'You clicked forget on it', value: CardArchiveReason.Forget },
    { label: 'Bulk forget', value: CardArchiveReason.BulkForget },
    { label: 'Mass action delete', value: CardArchiveReason.MassAction },
    { label: 'Merged into another dictionary entry', value: CardArchiveReason.WordReplacementMerge },
  ];

  function reasonText(row: ArchivedCard): string {
    const covering = row.coveringReading ? stripRuby(row.coveringReading) : null;
    switch (row.reason) {
      case CardArchiveReason.KanaRedundancy:
      case CardArchiveReason.FormPrune:
      case CardArchiveReason.RedundancyResolve:
        return covering ? `Replaced by ${covering}` : 'Replaced by another spelling';
      case CardArchiveReason.Forget:
        return 'You clicked forget on this card';
      case CardArchiveReason.BulkForget:
        return 'Forgotten in a bulk action';
      case CardArchiveReason.MassAction:
        return 'Deleted by a mass action';
      case CardArchiveReason.WordReplacementMerge:
        return 'Merged into another dictionary entry';
      default:
        return 'Removed';
    }
  }

  function formatDate(value: string): string {
    return new Date(value).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });
  }

  function metaText(row: ArchivedRow): string {
    return [reasonText(row), formatDate(row.archivedAt), `${row.reviewCount.toLocaleString()} review${row.reviewCount === 1 ? '' : 's'}`].join(' · ');
  }

  // Only paging keeps an "everything matching" selection: any other reload either changed the matching set or already acted on it.
  async function load(newOffset = 0, keepSelectAll = false) {
    const wasSelectAll = keepSelectAll && selectAllMatching.value;
    loading.value = true;
    try {
      const query: Record<string, string | number> = { offset: newOffset, limit: pageSize.value };
      if (reasonFilter.value !== null) query.reason = reasonFilter.value;

      const data = await $api<{ data: ArchivedCard[]; totalItems: number }>('user/vocabulary/archive', { query });
      // Emptying the last page leaves the offset past the end, so fall back to the start rather than showing a blank table.
      if (newOffset > 0 && (data?.data?.length ?? 0) === 0 && (data?.totalItems ?? 0) > 0) {
        await load(0);
        return;
      }

      rows.value = (data?.data ?? []).map((r) => ({
        ...r,
        key: `${r.wordId}:${r.readingIndex}`,
        rubyHtml: convertToRuby(r.reading),
        plainReading: stripRuby(r.reading),
      }));
      totalItems.value = data?.totalItems ?? 0;
      offset.value = newOffset;
      selectedKeys.value = wasSelectAll ? new Set(rows.value.map((r) => r.key)) : new Set();
      selectAllMatching.value = wasSelectAll;
      showDialog.value = true;
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Could not load', detail: extractApiError(e, 'Could not load removed cards.'), life: 5000 });
    } finally {
      loading.value = false;
    }
  }

  function onPage(event: { first: number; rows: number }) {
    pageSize.value = event.rows;
    load(event.first, true);
  }

  function formOf(row: ArchivedRow) {
    return { wordId: row.wordId, readingIndex: row.readingIndex };
  }

  // An absent row means "the current selection", which may be every matching entry rather than the loaded page.
  function selectionBody(row: ArchivedRow | undefined, everythingKey: 'all' | 'emptyForms') {
    if (row) return { forms: [formOf(row)] };
    if (!selectAllMatching.value) return { forms: selected.value.map(formOf) };
    return everythingKey === 'all'
      ? { all: true, reason: reasonFilter.value ?? undefined }
      : { forms: [], reason: reasonFilter.value ?? undefined };
  }

  async function restore(row?: ArchivedRow) {
    busy.value = true;
    try {
      const result = await $api<{ restored: number; remaining: number; results: { error: string | null }[] }>('user/vocabulary/archive/restore', {
        method: 'POST',
        body: selectionBody(row, 'all'),
      });

      const restored = result?.restored ?? 0;
      const remainingCount = result?.remaining ?? 0;
      const failures = (result?.results ?? []).filter((r) => r.error);

      if (restored === 0) {
        toast.add({
          severity: 'warn',
          summary: 'Nothing restored',
          detail: failures[0]?.error ?? (row ? 'This card could not be restored.' : 'These cards could not be restored.'),
          life: 5000,
        });
        return;
      }

      const notes: string[] = [];
      if (failures.length > 0) notes.push(`${failures.length} could not be restored.`);
      if (remainingCount > 0) notes.push(`${remainingCount.toLocaleString()} still to go. Run it again to continue.`);

      toast.add({
        severity: 'success',
        summary: restored === 1 ? 'Card restored' : 'Cards restored',
        detail: row
          ? row.plainReading
          : `Restored ${restored.toLocaleString()} card${restored === 1 ? '' : 's'} with their review history. ${notes.join(' ')}`.trim(),
        life: row ? 2500 : 6000,
      });

      await load(remainingCount > 0 ? 0 : offset.value);
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Restore failed', detail: extractApiError(e, 'Could not restore these cards.'), life: 5000 });
    } finally {
      busy.value = false;
    }
  }

  function confirmForget(row?: ArchivedRow) {
    const count = actionCount.value;
    const reviews = row ? `Its ${row.reviewCount.toLocaleString()} kept review${row.reviewCount === 1 ? '' : 's'} are` : 'The kept review history is';
    const subject = row ? 'the card' : 'these cards';

    confirm.require({
      header: row ? `Forget ${row.plainReading}` : `Forget ${count.toLocaleString()} entr${count === 1 ? 'y' : 'ies'}`,
      message: `This is permanent. ${reviews} deleted, ${subject} can no longer be restored, and your heatmap, streaks and retention will be rebuilt without those reviews.`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Forget for good',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: () => forget(row),
    });
  }

  async function forget(row?: ArchivedRow) {
    busy.value = true;
    try {
      const result = await $api<{ removed: number }>('user/vocabulary/archive', {
        method: 'DELETE',
        body: selectionBody(row, 'emptyForms'),
      });
      toast.add({
        severity: 'success',
        summary: row ? 'Entry forgotten' : 'Entries forgotten',
        detail: row ? row.plainReading : `Removed ${result?.removed ?? 0} entr${result?.removed === 1 ? 'y' : 'ies'} from the list.`,
        life: row ? 2500 : 5000,
      });
      await load(offset.value);
      emit('changed');
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Failed', detail: extractApiError(e, 'Could not remove these entries.'), life: 5000 });
    } finally {
      busy.value = false;
    }
  }

</script>

<template>
  <Card>
    <template #title>
      <h2 class="text-xl font-bold">Recently Removed</h2>
    </template>
    <template #content>
      <p class="text-sm text-muted-color mb-4">
        Cards that left your collection keep their review history here, whether you removed them yourself or they were replaced automatically with another form of the
        same word. Restoring one brings its schedule and every review back. You can also restore them individually on vocabulary pages.
      </p>

      <div class="flex flex-wrap items-center gap-3">
        <Button
          label="Show removed cards"
          icon="pi pi-history"
          severity="secondary"
          :loading="loading"
          :disabled="!countLoading && archivedCount === 0"
          @click="load(0)"
        />
        <Skeleton v-if="countLoading" width="7rem" height="1.2rem" />
        <span v-else-if="archivedCount === 0" class="text-sm text-muted-color italic">Nothing has been removed.</span>
        <span v-else class="text-sm text-muted-color">
          <strong>{{ archivedCount.toLocaleString() }}</strong> card{{ archivedCount === 1 ? '' : 's' }} kept here
        </span>
      </div>

      <Dialog
        v-model:visible="showDialog"
        modal
        :header="`Recently removed — ${totalItems.toLocaleString()} card${totalItems === 1 ? '' : 's'}`"
        :style="{ width: '800px', maxWidth: '95vw' }"
      >
        <p class="text-sm text-muted-color mb-3">
          Every card here kept the review history it had when it left your collection, and restoring one brings its schedule and reviews back with it. Newest
          first. <strong>Forgetting</strong> an entry deletes those reviews and cannot be undone.
        </p>

        <div class="flex flex-wrap items-center gap-2 mb-3">
          <span class="text-sm text-muted-color">Show</span>
          <Select
            v-model="reasonFilter"
            :options="reasonOptions"
            option-label="label"
            option-value="value"
            placeholder="Every reason"
            size="small"
            class="w-full sm:w-72"
            @change="load(0)"
          />
        </div>

        <div v-if="rows.length === 0" class="text-sm text-muted-color italic py-4 text-center">Nothing here for this filter.</div>

        <template v-else>
          <div
            v-if="selectAllMatching"
            class="mb-2 rounded border border-amber-300 dark:border-amber-700/70 bg-amber-50 dark:bg-amber-900/20 px-3 py-2 text-xs text-amber-800 dark:text-amber-200"
          >
            <strong>All {{ totalItems.toLocaleString() }} matching entries are selected,</strong> including the ones on other pages. Restore and Forget will act
            on every one of them.
          </div>

          <div class="flex items-center justify-between gap-3 border-b border-surface-200 dark:border-surface-700 pb-2 mb-1">
            <label class="flex items-center gap-2 cursor-pointer select-none">
              <Checkbox
                :model-value="pageFullySelected"
                binary
                :indeterminate="!pageFullySelected && selected.length > 0"
                @update:model-value="togglePage"
              />
              <span class="text-sm">Select all</span>
            </label>
            <div class="flex items-center gap-3 text-xs">
              <button
                v-if="hasMorePages && !selectAllMatching"
                class="text-primary-600 dark:text-primary-400 hover:underline cursor-pointer"
                @click="selectEverything"
              >
                Select all {{ totalItems.toLocaleString() }}
              </button>
              <button v-if="selectAllMatching" class="text-primary-600 dark:text-primary-400 hover:underline cursor-pointer" @click="selectPageOnly">
                Select only this page
              </button>
              <span class="text-muted-color">{{ actionCount > 0 ? `${actionCount.toLocaleString()} selected` : 'Nothing selected' }}</span>
            </div>
          </div>

          <div class="max-h-[55vh] overflow-y-auto divide-y divide-surface-200 dark:divide-surface-700">
            <div
              v-for="row in rows"
              :key="row.key"
              class="flex items-start gap-3 px-1 py-2.5 cursor-pointer transition-colors hover:bg-surface-50 dark:hover:bg-surface-800/60"
              @click="toggle(row.key)"
            >
              <Checkbox
                :model-value="selectedKeys.has(row.key)"
                binary
                class="mt-1 shrink-0"
                :aria-label="`Select ${row.plainReading}`"
                @click.stop
                @update:model-value="toggle(row.key)"
              />

              <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-baseline gap-x-2 gap-y-1">
                  <NuxtLink
                    :to="`/vocabulary/${row.wordId}/${row.readingIndex}`"
                    target="_blank"
                    class="font-noto-sans text-base text-blue-500 hover:underline"
                    @click.stop
                  >
                    <span lang="ja" v-html="row.rubyHtml" />
                  </NuxtLink>
                  <span class="text-xs" :class="fsrsStateTone(row.state).tone">{{ fsrsStateTone(row.state).label }}</span>
                  <span v-if="row.frequencyRank > 0" class="text-xs text-muted-color tabular-nums">#{{ row.frequencyRank.toLocaleString() }}</span>
                  <span class="min-w-0 truncate text-xs text-muted-color">{{ row.mainDefinition || '—' }}</span>
                </div>

                <div class="mt-0.5 flex flex-wrap items-center gap-x-2 text-xs text-muted-color">
                  <span>{{ metaText(row) }}</span>
                  <span v-if="row.historyTruncated" class="text-orange-500">· History truncated</span>
                  <span
                    v-if="row.autoRestores"
                    v-tooltip.top="'Comes back on its own if you add this form again'"
                    class="text-emerald-600 dark:text-emerald-400"
                  >
                    · Auto-restores
                  </span>
                </div>
              </div>

              <div class="flex shrink-0 gap-0.5" @click.stop>
                <Button
                  v-tooltip.top="'Restore this card'"
                  icon="pi pi-replay"
                  severity="success"
                  text
                  rounded
                  size="small"
                  :disabled="busy"
                  @click="restore(row)"
                />
                <Button
                  v-tooltip.top="'Forget for good'"
                  icon="pi pi-trash"
                  severity="danger"
                  text
                  rounded
                  size="small"
                  :disabled="busy"
                  @click="confirmForget(row)"
                />
              </div>
            </div>
          </div>

          <Paginator
            v-if="totalItems > pageSize"
            :first="offset"
            :rows="pageSize"
            :total-records="totalItems"
            :rows-per-page-options="PAGE_SIZE_OPTIONS"
            class="mt-2"
            @page="onPage"
          />
        </template>

        <template #footer>
          <div class="flex flex-wrap justify-end gap-2">
            <Button label="Close" severity="secondary" @click="showDialog = false" />
            <Button
              :label="actionCount === 0 ? 'Forget for good' : `Forget ${actionCount.toLocaleString()} for good`"
              icon="pi pi-trash"
              severity="danger"
              outlined
              :disabled="actionCount === 0 || busy"
              @click="confirmForget()"
            />
            <Button
              :label="actionCount === 0 ? 'Restore' : `Restore ${actionCount.toLocaleString()}`"
              icon="pi pi-replay"
              :disabled="actionCount === 0 || busy"
              :loading="busy"
              @click="restore()"
            />
          </div>
        </template>
      </Dialog>
    </template>
  </Card>
</template>
