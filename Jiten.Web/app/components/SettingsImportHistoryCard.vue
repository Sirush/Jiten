<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';
  import type { CardBatch, PaginatedResponse } from '~/types/types';
  import { batchDetail, cardSourceDisplay } from '~/utils/cardSource';
  import { formatDateLong, formatDateShort } from '~/utils/dateFormat';

  const props = withDefaults(defineProps<{ reloadKey?: number }>(), { reloadKey: 0 });
  const emit = defineEmits<{ changed: [] }>();

  const PAGE_SIZE = 10;

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const { undoingId, undoBatch } = useBatchUndo();
  const localiseTitle = useLocaliseTitle();

  const batches = ref<CardBatch[]>([]);
  const totalItems = ref(0);
  const loaded = ref(false);
  const pending = ref(false);
  const error = ref(false);
  const hasMore = computed(() => batches.value.length < totalItems.value);

  async function fetchPage(offset: number, append: boolean) {
    pending.value = true;
    try {
      const page = await $api<PaginatedResponse<CardBatch[]>>('user/vocabulary/batches', { query: { offset, limit: PAGE_SIZE } });
      batches.value = append ? [...batches.value, ...page.data] : page.data;
      totalItems.value = page.totalItems;
      loaded.value = true;
      error.value = false;
    } catch (e) {
      if (loaded.value) toast.add({ severity: 'error', detail: extractApiError(e, 'Could not load your import history.'), life: 5000 });
      else error.value = true;
    } finally {
      pending.value = false;
    }
  }

  const reload = () => fetchPage(0, false);
  const loadMore = () => fetchPage(batches.value.length, true);

  onMounted(reload);

  watch(
    () => props.reloadKey,
    () => reload()
  );

  function plural(count: number, word: string): string {
    return `${count.toLocaleString()} ${word}${count === 1 ? '' : 's'}`;
  }

  function sourceLabel(batch: CardBatch): string {
    return cardSourceDisplay(batch.source).label;
  }

  const detail = (batch: CardBatch) => batchDetail(batch, localiseTitle);

  function fullLabel(batch: CardBatch): string {
    const text = detail(batch);
    return text ? `${sourceLabel(batch)}: ${text}` : sourceLabel(batch);
  }

  function confirmUndo(batch: CardBatch) {
    const what = fullLabel(batch);
    const kept = batch.wordCount - batch.removableCount;
    confirm.require({
      header: 'Undo this import',
      message:
        `${plural(batch.removableCount, 'word')} added by "${what}" will be removed from your vocabulary.` +
        (kept > 0 ? ` ${plural(kept, 'word')} you have studied or changed since will stay.` : '') +
        ' Any of them with review history will be kept under Recently Removed, where you can restore them.',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: `Remove ${plural(batch.removableCount, 'word')}`,
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      rejectProps: { severity: 'secondary' },
      accept: () => undo(batch),
    });
  }

  async function undo(batch: CardBatch) {
    try {
      const result = await undoBatch(batch.batchId);
      if (result.alreadyUndone) {
        toast.add({ severity: 'info', summary: 'Already undone', detail: 'This import had already been undone.', life: 4000 });
      } else {
        toast.add({
          severity: 'success',
          summary: 'Import undone',
          detail:
            result.kept > 0
              ? `Removed ${plural(result.removed, 'word')}, kept ${result.kept.toLocaleString()} you've studied or changed since.`
              : `Removed ${plural(result.removed, 'word')}.`,
          life: 6000,
        });
        emit('changed');
      }
      batches.value = batches.value.map((b) => (b.batchId === batch.batchId ? { ...b, undone: true, removableCount: 0 } : b));
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Undo failed', detail: extractApiError(e, 'Could not undo this import.'), life: 5000 });
    }
  }
</script>

<template>
  <SettingsListItem title="Import History">
    <template #default>
      <p class="text-sm text-muted-color mb-4">
        Imports and other bulk additions to your vocabulary. Undoing one will remove the words it added and keep any you have studied or changed since.
      </p>

      <div v-if="!loaded && (pending || !error)" class="flex flex-col gap-3" aria-busy="true" aria-label="Loading import history">
        <div v-for="i in 3" :key="i" class="flex items-center gap-3 py-1">
          <div class="flex-1 flex flex-col gap-1.5">
            <Skeleton width="45%" height="1rem" />
            <Skeleton width="30%" height="0.8rem" />
          </div>
          <Skeleton width="4.5rem" height="2rem" />
        </div>
      </div>

      <div v-else-if="!loaded" class="flex flex-wrap items-center gap-3 text-sm">
        <span class="text-muted-color">Could not load your import history.</span>
        <Button label="Try again" icon="pi pi-refresh" severity="secondary" size="small" @click="reload" />
      </div>

      <p v-else-if="batches.length === 0" class="text-sm text-muted-color italic">
        Nothing imported yet. Imports, deck learns and frequency ranges you add will be listed here so you can undo them.
      </p>

      <div v-else class="transition-opacity duration-200" :class="{ 'opacity-60 delay-200': pending }">
        <div
          class="hidden sm:grid grid-cols-[minmax(0,1fr)_7.5rem_6.5rem_7rem] gap-3 px-1 pb-2 text-xs font-semibold text-muted-color border-b border-surface-200 dark:border-surface-700"
        >
          <span>Source</span>
          <span>Date</span>
          <span class="text-right">Words</span>
          <span class="sr-only">Action</span>
        </div>

        <ul class="divide-y divide-surface-200 dark:divide-surface-700">
          <li
            v-for="batch in batches"
            :key="batch.batchId"
            class="flex flex-wrap items-center gap-x-3 gap-y-2 px-1 py-3 sm:grid sm:grid-cols-[minmax(0,1fr)_7.5rem_6.5rem_7rem]"
          >
            <div class="min-w-0 flex-1 basis-full sm:basis-auto">
              <div class="flex items-center gap-2 min-w-0">
                <i :class="cardSourceDisplay(batch.source).icon" class="text-muted-color shrink-0" aria-hidden="true" />
                <span class="font-medium">{{ sourceLabel(batch) }}</span>
              </div>
              <div v-if="detail(batch)" class="text-sm text-muted-color truncate mt-0.5" :title="detail(batch) ?? undefined">{{ detail(batch) }}</div>
            </div>

            <Tooltip :content="formatDateLong(batch.createdAt)">
              <span class="flex items-center gap-1.5 text-sm text-muted-color tabular-nums" :aria-label="`Imported on ${formatDateLong(batch.createdAt)}`">
                <i class="pi pi-calendar text-xs sm:hidden" aria-hidden="true" />
                {{ formatDateShort(batch.createdAt) }}
              </span>
            </Tooltip>

            <span class="flex items-center gap-1.5 text-sm tabular-nums sm:justify-end" :aria-label="`${plural(batch.wordCount, 'word')} added`">
              <i class="pi pi-book text-xs text-muted-color sm:hidden" aria-hidden="true" />
              {{ batch.wordCount.toLocaleString() }}
            </span>

            <div class="ml-auto sm:ml-0 flex justify-end">
              <Tag v-if="batch.undone" value="Undone" severity="secondary" icon="pi pi-history" />
              <Tooltip v-else-if="batch.removableCount === 0" content="Every word from this import has been studied or changed since">
                <span tabindex="0" class="inline-flex">
                  <Button label="Undo" icon="pi pi-undo" severity="secondary" outlined size="small" disabled />
                </span>
              </Tooltip>
              <Button
                v-else
                label="Undo"
                icon="pi pi-undo"
                severity="danger"
                outlined
                size="small"
                :loading="undoingId === batch.batchId"
                :disabled="undoingId !== null"
                :aria-label="`Undo ${fullLabel(batch)}`"
                @click="confirmUndo(batch)"
              />
            </div>
          </li>
        </ul>

        <div v-if="hasMore" class="flex justify-center pt-3">
          <Button
            :label="`Show more (${(totalItems - batches.length).toLocaleString()} left)`"
            severity="secondary"
            text
            size="small"
            :loading="pending"
            @click="loadMore"
          />
        </div>
      </div>
    </template>
  </SettingsListItem>
</template>
