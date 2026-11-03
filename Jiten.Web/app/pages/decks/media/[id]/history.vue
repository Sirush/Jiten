<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import type { DeckDetail } from '~/types';
  import { MetadataEditActor, MetadataEditStatus, type MetadataDeckHistory, type MetadataHistoryItem, type MetadataRevertResult } from '~/types/metadataEdits';
  import { useApiFetch, useApiFetchPaginated } from '~/composables/useApiFetch';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import { formatDateShort, formatDateTime } from '~/utils/dateFormat';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { showErrorToast, showSuccessToast } from '~/utils/toast';
  import { useOlderPages } from '~/composables/useOlderPages';
  import { avatarCharacter } from '~/utils/profileAppearance';
  import { editStatus, joinList, lookedRightText, revertSummary } from '~/utils/metadataEdits';

  definePageMeta({
    middleware: ['auth'],
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const PAGE_SIZE = 30;

  const route = useRoute();
  const deckId = Number(route.params.id);
  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();

  const { data: deckResponse } = useApiFetchPaginated<DeckDetail>(`media-deck/${deckId}/detail`);
  const {
    data: history,
    status: historyStatus,
    error: historyError,
    refresh: refreshHistory,
  } = useApiFetch<MetadataDeckHistory>(`media-deck/${deckId}/metadata-edits`, { query: { offset: 0, limit: PAGE_SIZE } });

  const {
    items: loadedItems,
    total,
    loadingMore,
    loadMore,
  } = useOlderPages(
    history,
    (offset) => $api<MetadataDeckHistory>(`media-deck/${deckId}/metadata-edits`, { query: { offset, limit: PAGE_SIZE } }),
    (err) => showErrorToast(toast, "Older edits didn't load", apiErrorMessage(err, 'Try again in a moment.'))
  );
  const patches = ref<Record<number, Partial<MetadataHistoryItem>>>({});
  const items = computed(() => loadedItems.value.map((i) => (patches.value[i.id] ? { ...i, ...patches.value[i.id] } : i)));

  function patchItem(id: number, patch: Partial<MetadataHistoryItem>) {
    patches.value = { ...patches.value, [id]: { ...patches.value[id], ...patch } };
  }

  const role = computed(() => history.value?.viewerRole ?? 'user');
  const isReviewer = computed(() => role.value !== 'user');
  const origin = computed(() => history.value?.origin ?? null);
  const allLoaded = computed(() => items.value.length >= total.value);

  const mainDeck = computed(() => deckResponse.value?.data?.mainDeck ?? null);
  const parentDeck = computed(() => deckResponse.value?.data?.parentDeck ?? null);
  const title = computed(() => (mainDeck.value ? localiseTitle(mainDeck.value) : ''));

  useHead(() => ({ title: title.value ? `${title.value} - Edit history` : 'Edit history' }));
  useRobotsRule({ noindex: true, follow: true });

  function actorName(item: MetadataHistoryItem): string {
    if (item.actor === MetadataEditActor.Provider) return item.provider != null ? `Updated from ${getLinkTypeText(item.provider)}` : 'Updated from a provider';
    if (item.actor === MetadataEditActor.System || !item.author) return 'Jiten';
    return item.author.displayName;
  }

  function avatarLetter(item: MetadataHistoryItem): string {
    if (item.actor === MetadataEditActor.Provider) return item.provider != null ? avatarCharacter(null, getLinkTypeText(item.provider)) : '?';
    if (item.actor === MetadataEditActor.System || !item.author) return 'J';
    return avatarCharacter(null, item.author.displayName);
  }

  const REVIEWER_VERB: Partial<Record<MetadataEditStatus, string>> = {
    [MetadataEditStatus.Approved]: 'Approved by',
    [MetadataEditStatus.Rejected]: 'Rejected by',
    [MetadataEditStatus.Reverted]: 'Reverted by',
  };

  const isPartialRevert = (item: MetadataHistoryItem) => item.publicStatus === 'reverted' && !!item.skippedFields?.length;

  function partialRevertNotice(item: MetadataHistoryItem): string | null {
    if (!isPartialRevert(item)) return null;
    return item.reviewNote || revertSummary(item.revertedFields ?? [], item.skippedFields ?? []);
  }

  function reviewNoteText(item: MetadataHistoryItem): string | null {
    if (!item.reviewNote || isPartialRevert(item)) return null;
    return item.publicStatus === 'rejected' ? `Rejected: ${item.reviewNote}` : item.reviewNote;
  }

  const busyId = ref<number | null>(null);

  async function runAction(item: MetadataHistoryItem, failure: string, action: () => Promise<void>) {
    if (busyId.value) return;
    busyId.value = item.id;
    try {
      await action();
    } catch (err) {
      showErrorToast(toast, failure, apiErrorMessage(err, 'Try again in a moment.'));
    } finally {
      busyId.value = null;
    }
  }

  function revert(item: MetadataHistoryItem) {
    runAction(item, "The edit wasn't reverted", async () => {
      const result = await $api<MetadataRevertResult>(`metadata-edits/${item.id}/revert`, { method: 'POST' });
      patchItem(item.id, {
        publicStatus: 'reverted',
        status: MetadataEditStatus.Reverted,
        revertedFields: result.revertedFields,
        skippedFields: result.skippedFields,
        canRevert: false,
      });
      showSuccessToast(toast, 'Reverted', revertSummary(result.revertedFields, result.skippedFields));
    });
  }

  function withdraw(item: MetadataHistoryItem) {
    runAction(item, "The suggestion wasn't withdrawn", async () => {
      await $api(`metadata-edits/${item.id}/withdraw`, { method: 'POST' });
      patchItem(item.id, { publicStatus: 'withdrawn', status: item.status == null ? null : MetadataEditStatus.Withdrawn });
      showSuccessToast(toast, 'Suggestion withdrawn', 'It was taken out of the review queue.');
    });
  }

  function markReviewed(item: MetadataHistoryItem) {
    runAction(item, "The edit wasn't marked as reviewed", async () => {
      await $api(`metadata-edits/${item.id}/confirm-applied`, { method: 'POST' });
      patchItem(item.id, { status: MetadataEditStatus.Approved });
      showSuccessToast(toast, 'Marked as reviewed');
    });
  }

  const originProviders = computed(() => joinList((origin.value?.providers ?? []).map((p) => getLinkTypeText(p))));
</script>

<template>
  <div class="flex flex-col gap-3">
    <DeckBreadcrumb :deck="mainDeck" :parent-deck="parentDeck" current="Edit history" />

    <div class="flex flex-wrap items-end justify-between gap-3">
      <div class="flex flex-col gap-0.5 min-w-0">
        <h1 class="m-0 text-lg md:text-2xl font-bold min-h-7">
          <span v-if="title" v-bind="japaneseTextAttrs(title)">{{ title }}</span>
          <span class="hidden md:inline"> - Edit history</span>
        </h1>
        <p class="m-0 text-sm text-muted-color min-h-5">
          <template v-if="history">{{ total.toLocaleString() }} {{ total === 1 ? 'edit' : 'edits' }}</template>
        </p>
      </div>
      <Button as="router-link" :to="`/decks/media/${deckId}/edit`" label="Suggest an edit" icon="pi pi-pencil" severity="secondary" />
    </div>

    <div v-if="historyStatus === 'pending' && !history" class="flex flex-col gap-2" aria-busy="true">
      <Skeleton v-for="i in 4" :key="i" height="5rem" />
    </div>

    <div
      v-else-if="historyError && !history"
      class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-5 flex flex-col items-start gap-3"
    >
      <p class="m-0">The edit history didn't load.</p>
      <Button label="Retry" icon="pi pi-refresh" @click="refreshHistory()" />
    </div>

    <ol
      v-else
      class="list-none m-0 p-0 rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 divide-y divide-surface-200 dark:divide-surface-700"
    >
      <li
        v-for="item in items"
        :key="item.id"
        class="grid grid-cols-[2rem_minmax(0,1fr)] sm:grid-cols-[2rem_minmax(0,1fr)_auto] gap-x-3 gap-y-2 px-4 py-3.5 items-start"
      >
        <span
          class="w-8 h-8 rounded-full grid place-items-center text-sm font-bold"
          :class="
            item.actor === MetadataEditActor.User
              ? 'bg-primary-100 text-primary-800 dark:bg-primary-900/50 dark:text-primary-100'
              : 'bg-surface-200 text-surface-700 dark:bg-surface-700 dark:text-surface-100'
          "
          aria-hidden="true"
        >
          {{ avatarLetter(item) }}
        </span>
        <div class="min-w-0 flex flex-col gap-1.5">
          <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <strong class="text-sm">{{ actorName(item) }}</strong>
            <Tag :value="editStatus(item, role).label" :severity="editStatus(item, role).severity" class="!text-xs !py-0" />
            <span class="text-xs text-muted-color tabular-nums">{{ formatDateTime(item.createdAt) }}</span>
          </div>
          <p v-if="item.contentHidden" class="m-0 text-sm text-muted-color">The details of this edit are hidden because it was reverted.</p>
          <MetadataChangeList v-else :changes="item.changes" compact />
          <p v-if="item.sources.length" class="m-0 text-xs text-muted-color [overflow-wrap:anywhere]">
            {{ item.sources.length === 1 ? 'Source:' : 'Sources:' }}
            <template v-for="(source, i) in item.sources" :key="source">
              <a :href="source" target="_blank" rel="noopener nofollow ugc">{{ source }}</a
              ><template v-if="i < item.sources.length - 1">, </template>
            </template>
          </p>
          <div v-if="item.publicStatus === 'pending'" class="flex flex-wrap items-center gap-x-3 gap-y-1">
            <span class="text-xs text-muted-color">
              {{ item.confirmCount ? lookedRightText(item.confirmCount) : 'Not checked yet' }}
            </span>
            <NuxtLink v-if="!item.isYours" :to="{ path: '/review', query: { deckId, edit: item.id } }" class="text-xs font-medium">
              {{ isReviewer ? 'Review this edit' : 'Check this edit' }}
            </NuxtLink>
            <button
              v-else
              type="button"
              class="text-xs font-medium text-primary-600 dark:text-primary-400 hover:underline cursor-pointer rounded focus-visible:outline-2 focus-visible:outline-primary-500 disabled:opacity-60"
              :disabled="busyId === item.id"
              @click="withdraw(item)"
            >
              Withdraw
            </button>
          </div>
          <p v-if="reviewNoteText(item)" class="m-0 text-xs text-muted-color">{{ reviewNoteText(item) }}</p>
          <p v-if="isReviewer && item.reviewer && item.status && REVIEWER_VERB[item.status]" class="m-0 text-xs text-muted-color">
            {{ REVIEWER_VERB[item.status] }} {{ item.reviewer.displayName }}
          </p>
          <p v-if="partialRevertNotice(item)" class="m-0 text-xs text-muted-color rounded-md bg-surface-100 dark:bg-surface-800 px-2.5 py-2">
            {{ partialRevertNotice(item) }}
          </p>
        </div>
        <div
          v-if="item.canRevert || (isReviewer && item.status === MetadataEditStatus.Applied)"
          class="col-start-2 sm:col-start-3 flex flex-wrap gap-2 sm:justify-end"
        >
          <Button
            v-if="isReviewer && item.status === MetadataEditStatus.Applied"
            label="Mark reviewed"
            size="small"
            severity="secondary"
            :loading="busyId === item.id"
            :disabled="!!busyId && busyId !== item.id"
            @click="markReviewed(item)"
          />
          <Button
            v-if="item.canRevert"
            label="Revert"
            size="small"
            severity="danger"
            outlined
            :loading="busyId === item.id"
            :disabled="!!busyId && busyId !== item.id"
            @click="revert(item)"
          />
        </div>
      </li>

      <li v-if="!items.length && history" class="px-4 py-3.5 text-sm text-muted-color">Nobody has edited this deck yet.</li>

      <li v-if="!allLoaded" class="px-4 py-3 flex justify-center">
        <Button label="Show older edits" severity="secondary" text :loading="loadingMore" @click="loadMore" />
      </li>

      <li v-if="allLoaded && origin" class="grid grid-cols-[2rem_minmax(0,1fr)] gap-x-3 px-4 py-3.5 items-start">
        <span
          class="w-8 h-8 rounded-full grid place-items-center text-sm font-bold bg-primary-600 text-white dark:bg-primary-400 dark:text-surface-950"
          aria-hidden="true"
        >
          J
        </span>
        <div class="min-w-0 flex flex-col gap-1.5">
          <div class="flex flex-wrap items-center gap-x-2">
            <strong class="text-sm">Added to Jiten</strong>
            <span class="text-xs text-muted-color tabular-nums">{{ formatDateShort(origin.createdAt) }}</span>
          </div>
          <p v-if="originProviders" class="m-0 text-sm">Details imported from {{ originProviders }}.</p>
        </div>
      </li>
    </ol>
  </div>
</template>
