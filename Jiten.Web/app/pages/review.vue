<script setup lang="ts">
  import { useToast } from 'primevue/usetoast';
  import {
    MetadataEditVerdict,
    MetadataField,
    type MetadataNamePreview,
    type MetadataQueueConflict,
    type MetadataQueueItem,
    type MetadataQueueResponse,
    type MetadataRevertResult,
    type MetadataViewerRole,
  } from '~/types/metadataEdits';
  import { coverUrl } from '~/utils/coverImage';
  import { formatDateOmitCurrentYear, formatDateTime } from '~/utils/dateFormat';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { apiErrorMessage, apiStatusCode } from '~/utils/apiErrorMessage';
  import { showUndoToast } from '~/composables/useMediaListSync';
  import { avatarCharacter } from '~/utils/profileAppearance';
  import {
    DOUBT_REASONS,
    TIER_LABELS,
    approvalExclusion,
    contributorLevelLabel,
    conflictClashes,
    describeValue,
    fieldLabel,
    fieldPhrase,
    fieldsFromFlags,
    isListField,
    leftOutPhrase,
    noteHasReason,
    queueMode,
    queueShortcut,
    queueTabFromQuery,
    revertSummary,
    lookedRightText,
    sortChanges,
    tierLabel,
    toggleChipClass,
    toggleNoteReason,
    type ApprovalExclusion,
    type ConflictClash,
    type QueueTab,
  } from '~/utils/metadataEdits';
  import { isTypingTarget } from '~/utils/keyboard';

  definePageMeta({ middleware: ['auth'] });
  useHead({ title: 'Review queue' });
  useRobotsRule({ noindex: true, follow: false });

  type QueueSort = 'new' | 'confirmed' | 'doubted';

  const PAGE_SIZE = 20;
  const CONFLICT_OPTIONS = ['current', 'edit'] as const;
  const REJECT_REASONS = ["The source doesn't support this", 'No source', 'Already correct', 'Belongs on a different deck'];
  const FLAGGED_NAMES_NOTE = 'The automatic check flagged these names.';

  const route = useRoute();
  const router = useRouter();
  const { $api } = useNuxtApp();
  const toast = useToast();
  const localiseTitle = useLocaliseTitle();
  const deckCountText = (count: number) => `${count} ${count === 1 ? 'deck' : 'decks'}`;

  const deckFilter = computed(() => {
    const value = Number(route.query.deckId);
    return Number.isInteger(value) && value > 0 ? value : undefined;
  });
  const focusEditId = Number(route.query.edit) || null;

  const sort = ref<QueueSort>('new');
  const tier = ref<number | null>(null);
  const items = ref<MetadataQueueItem[]>([]);
  const total = ref(0);
  const role = ref<MetadataViewerRole>('user');
  const loaded = ref(false);
  const loading = ref(false);
  const loadFailed = ref(false);
  const selectedId = ref<number | null>(null);
  const showDone = ref(false);

  const isReviewer = computed(() => role.value !== 'user');

  const tab = computed(() => queueTabFromQuery(route.query.tab));
  const mode = computed(() => queueMode(role.value, tab.value));
  const counts = ref<Record<QueueTab, number | null>>({ pending: null, unreviewed: null });
  let loadRequest = 0;

  /** Your own edits and ones you already checked need nothing from you, so they're hidden unless asked for. */
  const isDone = (item: MetadataQueueItem) => item.author.isYou || (mode.value === 'check' && !!item.myCheck);
  const doneCount = computed(() => items.value.filter(isDone).length);

  const pageOf = new Map<number, number>();
  let lastPage = 0;

  /** Edits to the same deck sit together within a loaded page only, so a later page never inserts ahead of the selection. */
  const groups = computed(() => {
    const byKey = new Map<string, { key: string; deckId: number; deck: MetadataQueueItem['deck']; items: MetadataQueueItem[] }>();
    for (const item of items.value) {
      if (!showDone.value && isDone(item)) continue;
      const key = `${pageOf.get(item.id) ?? 0}:${item.deckId}`;
      const group = byKey.get(key) ?? { key, deckId: item.deckId, deck: item.deck, items: [] };
      group.items.push(item);
      byKey.set(key, group);
    }
    return [...byKey.values()];
  });
  const ordered = computed(() => groups.value.flatMap((g) => g.items));
  const selected = computed(() => ordered.value.find((i) => i.id === selectedId.value) ?? ordered.value[0] ?? null);
  const position = computed(() => (selected.value ? ordered.value.indexOf(selected.value) + 1 : 0));
  const reachableTotal = computed(() => total.value - (items.value.length - ordered.value.length));

  const queueQuery = (state: QueueTab, offset: number, limit: number) => ({
    state,
    sort: sort.value,
    tier: tier.value ?? undefined,
    deckId: deckFilter.value,
    offset,
    limit,
  });

  async function load(append = false) {
    if (append && loading.value) return;
    const request = ++loadRequest;
    const state = tab.value;
    loading.value = true;
    loadFailed.value = false;
    try {
      const response = await $api<MetadataQueueResponse>('metadata-edits/pending', {
        query: queueQuery(state, append ? items.value.length : 0, PAGE_SIZE),
      });
      if (request !== loadRequest) return;
      role.value = response.viewerRole;
      total.value = response.total;
      counts.value = { ...counts.value, [state]: response.total };
      if (append) {
        const seen = new Set(items.value.map((i) => i.id));
        const fresh = response.items.filter((i) => !seen.has(i.id));
        lastPage += 1;
        for (const item of fresh) pageOf.set(item.id, lastPage);
        items.value.push(...fresh);
      } else {
        pageOf.clear();
        lastPage = 0;
        items.value = response.items;
        const focused = !loaded.value && focusEditId ? response.items.find((i) => i.id === focusEditId) : undefined;
        if (focused && isDone(focused)) showDone.value = true;
        selectedId.value = focused?.id ?? null;
        if (response.viewerRole !== 'user') counts.value = { ...counts.value, [state === 'pending' ? 'unreviewed' : 'pending']: response.otherTotal };
      }
      loaded.value = true;
    } catch {
      if (request === loadRequest) loadFailed.value = true;
    } finally {
      if (request === loadRequest) loading.value = false;
    }
  }

  function setTab(value: QueueTab) {
    if (value === tab.value) return;
    router.replace({ query: { ...route.query, tab: value === 'unreviewed' ? value : undefined, edit: undefined } });
  }

  onMounted(() => load());
  watch(tab, () => {
    items.value = [];
    loaded.value = false;
  });
  watch([sort, tier, deckFilter, tab], () => load());

  const panel = ref<'doubt' | 'reject' | null>(null);
  const doubtNote = ref('');
  const doubtFields = ref<number[]>([]);
  const rejectNote = ref('');
  const excludedFields = ref<Record<number, number>>({});
  const excludedItems = ref<Record<number, Record<number, string[]>>>({});
  const excludeNote = ref('');
  const busy = ref(false);
  const actionError = ref<string | null>(null);

  watch(
    () => selected.value?.id,
    () => {
      panel.value = null;
      doubtNote.value = '';
      doubtFields.value = selected.value?.changes.map((c) => c.field) ?? [];
      rejectNote.value = '';
      excludeNote.value = '';
      actionError.value = null;
    },
    { immediate: true }
  );

  const detailRef = ref<HTMLElement | null>(null);

  watch(
    () => selected.value?.id,
    (id, previous) => {
      const detail = detailRef.value;
      if (id == null || previous == null || !detail) return;
      const top = detail.getBoundingClientRect().top;
      if (top < 0 || top > window.innerHeight / 2) detail.scrollIntoView({ block: 'start' });
    },
    { flush: 'post' }
  );

  const actionBarRef = ref<HTMLElement | null>(null);
  const actionBarHeight = ref(0);
  watch(actionBarRef, (el, _, onCleanup) => {
    if (!el) {
      actionBarHeight.value = 0;
      return;
    }
    const observer = new ResizeObserver(([entry]) => (actionBarHeight.value = entry?.borderBoxSize[0]?.blockSize ?? 0));
    observer.observe(el);
    onCleanup(() => observer.disconnect());
  });
  useHead({ htmlAttrs: { style: computed(() => (actionBarHeight.value ? `--undo-toast-bottom: ${actionBarHeight.value + 12}px` : undefined)) } });

  const stepLabel = computed(() => (mode.value === 'check' && (selected.value?.myCheck || !selected.value?.canCheck) ? 'Next' : 'Skip'));

  function select(item: MetadataQueueItem) {
    selectedId.value = item.id;
  }

  const hasMore = computed(() => items.value.length < total.value);
  let nextVisible: Promise<MetadataQueueItem | null> | null = null;

  async function appendUntilVisible(): Promise<MetadataQueueItem | null> {
    const known = new Set(ordered.value.map((i) => i.id));
    while (hasMore.value) {
      const before = items.value.length;
      await load(true);
      const fresh = ordered.value.find((i) => !known.has(i.id));
      if (fresh) return fresh;
      if (items.value.length === before) break;
    }
    return null;
  }

  /** A page can hold only hidden edits, so this keeps loading until one shows; concurrent callers share one run. */
  function loadNextVisible(): Promise<MetadataQueueItem | null> {
    nextVisible ??= appendUntilVisible().finally(() => (nextVisible = null));
    return nextVisible;
  }

  async function move(step: number) {
    const index = selected.value ? ordered.value.indexOf(selected.value) : -1;
    if (step < 0 && index <= 0) return;
    if (step > 0 && index === ordered.value.length - 1) {
      const fresh = await loadNextVisible();
      if (fresh) {
        selectedId.value = fresh.id;
        return;
      }
    }
    const list = ordered.value;
    if (!list.length) return;
    selectedId.value = list[(index + step + list.length) % list.length]!.id;
  }

  /** Call before the item is handled, since it may then drop out of view; the returned function resolves the item to show next. */
  function planNext(item: MetadataQueueItem): () => Promise<MetadataQueueItem | null> {
    const list = ordered.value;
    const index = list.indexOf(item);
    const forward = list[index + 1] ?? null;
    const back = list[index - 1] ?? null;
    return async () => forward ?? (await loadNextVisible()) ?? back;
  }

  async function removeItem(item: MetadataQueueItem) {
    const index = items.value.indexOf(item);
    if (index === -1) return;
    const next = planNext(item);
    items.value.splice(index, 1);
    total.value = Math.max(0, total.value - 1);
    counts.value = { ...counts.value, [tab.value]: total.value };
    selectedId.value = (await next())?.id ?? null;
  }

  watch(
    () => loaded.value && !ordered.value.length && hasMore.value && !loading.value && !loadFailed.value,
    (stranded) => {
      if (stranded) loadNextVisible();
    }
  );

  function restoreItem(item: MetadataQueueItem, index: number) {
    items.value.splice(Math.min(index, items.value.length), 0, item);
    total.value += 1;
    counts.value = { ...counts.value, [tab.value]: total.value };
    selectedId.value = item.id;
  }

  /** Every queue outcome shows in the same bottom toast; only reversible ones get an Undo button. */
  const notify = (summary: string, run?: () => Promise<unknown>) => showUndoToast(toast, summary, run);

  async function putCheck(item: MetadataQueueItem, verdict: MetadataEditVerdict, note?: string, fields?: number[]) {
    const result = await $api<{ confirmCount: number }>(`metadata-edits/${item.id}/check`, {
      method: 'PUT',
      body: { verdict, note: note?.trim() || undefined, fields },
    });
    const flags = verdict === MetadataEditVerdict.Doubt && fields?.length ? fields.reduce((a, f) => a | f, 0) : 0;
    item.myCheck = { verdict, note: note?.trim() || null, fields: flags === item.fields ? 0 : flags };
    item.confirmCount = result.confirmCount;
  }

  async function deleteCheck(item: MetadataQueueItem) {
    const previous = item.myCheck;
    await $api(`metadata-edits/${item.id}/check`, { method: 'DELETE' });
    item.myCheck = null;
    if (previous?.verdict === MetadataEditVerdict.Confirm) item.confirmCount = Math.max(0, item.confirmCount - 1);
    selectedId.value = item.id;
  }

  async function runAction(action: () => Promise<void>, failure: string) {
    if (busy.value) return;
    busy.value = true;
    actionError.value = null;
    try {
      await action();
    } catch (err) {
      actionError.value = apiErrorMessage(err, failure);
    } finally {
      busy.value = false;
    }
  }

  const canCheckItem = (item: MetadataQueueItem | null) => !!item && item.canCheck && !item.myCheck && !isReviewer.value;

  function confirmEdit() {
    const item = selected.value;
    if (!item || !canCheckItem(item)) return;
    runAction(async () => {
      const next = planNext(item);
      await putCheck(item, MetadataEditVerdict.Confirm);
      selectedId.value = (await next())?.id ?? null;
      notify('Thanks. Editors will see your answer when they review this.', () => deleteCheck(item));
    }, "Your check wasn't saved. Try again in a moment.");
  }

  function openDoubt() {
    if (!canCheckItem(selected.value)) return;
    panel.value = 'doubt';
    nextTick(() => document.querySelector<HTMLElement>('[data-doubt-reason]')?.focus());
  }

  function toggleDoubtField(field: number) {
    doubtFields.value = doubtFields.value.includes(field) ? doubtFields.value.filter((f) => f !== field) : [...doubtFields.value, field];
  }

  function sendDoubt() {
    const item = selected.value;
    if (!item || !doubtNote.value.trim() || !doubtFields.value.length) return;
    const fields = doubtFields.value.length === item.changes.length ? undefined : [...doubtFields.value];
    runAction(async () => {
      const next = planNext(item);
      await putCheck(item, MetadataEditVerdict.Doubt, doubtNote.value, fields);
      panel.value = null;
      selectedId.value = (await next())?.id ?? null;
      notify('Sent to editors. Thanks for checking.', () => deleteCheck(item));
    }, "Your check wasn't saved. Try again in a moment.");
  }

  function withdrawCheck() {
    const item = selected.value;
    const previous = item?.myCheck;
    if (!item || !previous) return;
    runAction(async () => {
      await deleteCheck(item);
      const fields = previous.fields ? fieldsFromFlags(previous.fields) : undefined;
      notify('Check withdrawn.', () => putCheck(item, previous.verdict, previous.note ?? undefined, fields));
    }, "Your check wasn't withdrawn. Try again in a moment.");
  }

  const resolutions = ref<Record<number, Record<number, 'current' | 'edit'>>>({});

  function resolutionFor(item: MetadataQueueItem, conflict: MetadataQueueConflict): 'current' | 'edit' {
    return resolutions.value[item.id]?.[conflict.field] ?? 'current';
  }

  /** A list conflict the merge rules can't narrow to items falls back to showing both whole lists. */
  function clashesFor(conflict: MetadataQueueConflict): ConflictClash[] {
    const clashes = conflictClashes(conflict);
    if (clashes.length) return clashes;
    return [
      { label: fieldLabel(conflict.field), current: describeValue(conflict.field, conflict.current), edit: describeValue(conflict.field, conflict.editAfter) },
    ];
  }

  function setResolution(item: MetadataQueueItem, field: MetadataField, keep: 'current' | 'edit') {
    resolutions.value = { ...resolutions.value, [item.id]: { ...resolutions.value[item.id], [field]: keep } };
  }

  const isIncluded = (item: MetadataQueueItem, field: number) => ((excludedFields.value[item.id] ?? 0) & field) === 0;

  function toggleIncluded(item: MetadataQueueItem, field: number) {
    excludedFields.value = { ...excludedFields.value, [item.id]: (excludedFields.value[item.id] ?? 0) ^ field };
  }

  function toggleItem(item: MetadataQueueItem, field: number, key: string) {
    const keys = excludedItems.value[item.id]?.[field] ?? [];
    const next = keys.includes(key) ? keys.filter((k) => k !== key) : [...keys, key];
    excludedItems.value = { ...excludedItems.value, [item.id]: { ...excludedItems.value[item.id], [field]: next } };
  }

  function exclusionFor(item: MetadataQueueItem, flaggedNames: string[] = []): ApprovalExclusion {
    const items = excludedItems.value[item.id] ?? {};
    const names = [...(items[MetadataField.CharacterNames] ?? []), ...flaggedNames];
    return approvalExclusion(item.changes, excludedFields.value[item.id] ?? 0, { ...items, [MetadataField.CharacterNames]: names });
  }

  const emptyExclusion: ApprovalExclusion = { wholeFields: [], items: [], total: 0, leftOut: 0 };
  const exclusion = computed(() => (selected.value ? exclusionFor(selected.value) : emptyExclusion));
  const orderedChanges = computed(() => (selected.value ? sortChanges(selected.value.changes) : []));
  const canChoose = computed(() => mode.value === 'review' && !!selected.value?.canApprove);
  const choosesChanges = computed(() => canChoose.value && (selected.value?.changes.length ?? 0) > 1);
  const approveLabel = computed(() => {
    const { total, leftOut } = exclusion.value;
    return leftOut ? `Approve ${total - leftOut} of ${total} changes` : 'Approve';
  });
  const canApproveSelection = computed(
    () => !!selected.value && exclusion.value.wholeFields.length < selected.value.changes.length && (!exclusion.value.leftOut || !!excludeNote.value.trim())
  );

  async function approveItem(item: MetadataQueueItem, flaggedNames: string[] = []) {
    const resolutionList = (item.conflicts ?? []).map((c) => ({ field: c.field, keep: resolutionFor(item, c) }));
    const excluded = exclusionFor(item, flaggedNames);
    try {
      await $api(`metadata-edits/${item.id}/approve`, {
        method: 'POST',
        body: {
          resolutions: resolutionList.length ? resolutionList : undefined,
          excludeFields: excluded.wholeFields.length ? excluded.wholeFields : undefined,
          excludeItems: excluded.items.length ? excluded.items : undefined,
          excludeNote: excluded.leftOut ? excludeNote.value.trim() || FLAGGED_NAMES_NOTE : undefined,
        },
      });
    } catch (err) {
      if (apiStatusCode(err) === 409) {
        const conflicts = (err as { data?: { conflicts?: MetadataQueueConflict[] } }).data?.conflicts;
        if (conflicts) item.conflicts = conflicts;
      }
      throw err;
    }
  }

  function approve(flaggedNames: string[] = []) {
    const item = selected.value;
    if (!item || mode.value !== 'review' || !item.canApprove || !canApproveSelection.value) return;
    const partial = exclusionFor(item, flaggedNames).leftOut > 0;
    runAction(async () => {
      await approveItem(item, flaggedNames);
      await removeItem(item);
      notify(
        partial
          ? `Approved in part. ${item.author.displayName} will get your reason for the rest.`
          : `Approved. ${item.author.displayName} will get a notification.`
      );
    }, "The edit wasn't approved. Try again in a moment.");
  }

  function openReject() {
    if (mode.value !== 'review' || !selected.value?.canApprove) return;
    panel.value = 'reject';
    nextTick(() => document.getElementById('reject-note')?.focus());
  }

  function reject() {
    const item = selected.value;
    const note = rejectNote.value.trim();
    if (!item || !note) return;
    runAction(async () => {
      await $api(`metadata-edits/${item.id}/reject`, { method: 'POST', body: { note } });
      const index = items.value.indexOf(item);
      await removeItem(item);
      notify(`Rejected. ${item.author.displayName} will get your reason.`, async () => {
        try {
          await $api(`metadata-edits/${item.id}/reopen`, { method: 'POST' });
          restoreItem(item, index);
        } catch (err) {
          actionError.value = apiErrorMessage(err, "The rejection couldn't be undone.");
        }
      });
    }, "The edit wasn't rejected. Try again in a moment.");
  }

  function markReviewed() {
    const item = selected.value;
    if (!item || mode.value !== 'unreviewed' || !item.canMarkReviewed) return;
    runAction(async () => {
      await $api(`metadata-edits/${item.id}/confirm-applied`, { method: 'POST' });
      await removeItem(item);
      notify('Marked as reviewed. The edit stays live.');
    }, "The edit wasn't marked as reviewed. Try again in a moment.");
  }

  function revert() {
    const item = selected.value;
    if (!item || mode.value !== 'unreviewed' || !item.canRevert) return;
    runAction(async () => {
      const result = await $api<MetadataRevertResult>(`metadata-edits/${item.id}/revert`, { method: 'POST' });
      await removeItem(item);
      notify(revertSummary(result.revertedFields, result.skippedFields));
    }, "The edit wasn't reverted. Try again in a moment.");
  }

  const namePreviews = ref<Record<number, { loading: boolean; data: MetadataNamePreview | null; failed: boolean }>>({});

  async function loadNamePreview(item: MetadataQueueItem) {
    if (namePreviews.value[item.id]) return;
    namePreviews.value = { ...namePreviews.value, [item.id]: { loading: true, data: null, failed: false } };
    try {
      const data = await $api<MetadataNamePreview>(`metadata-edits/${item.id}/name-preview`);
      namePreviews.value = { ...namePreviews.value, [item.id]: { loading: false, data, failed: false } };
    } catch {
      namePreviews.value = { ...namePreviews.value, [item.id]: { loading: false, data: null, failed: true } };
    }
  }

  function retryNamePreview(item: MetadataQueueItem) {
    namePreviews.value = Object.fromEntries(Object.entries(namePreviews.value).filter(([id]) => Number(id) !== item.id));
    loadNamePreview(item);
  }

  const hasNames = (item: MetadataQueueItem) => (item.fields & MetadataField.CharacterNames) !== 0;
  /** Only an edit that adds names has surfaces to parse with; a removal-only edit has nothing to preview. */
  const addsNames = (item: MetadataQueueItem) => !!item.characterNameChecks?.length;

  watch(
    [selected, isReviewer],
    ([item, reviewer]) => {
      if (item && reviewer && addsNames(item)) loadNamePreview(item);
    },
    { immediate: true }
  );

  const flaggedSurfaces = computed(() => (selected.value?.characterNameChecks ?? []).filter((s) => s.status === 'flagged').map((s) => s.surface));
  const confirmers = computed(() => (selected.value?.checks ?? []).filter((c) => c.verdict === MetadataEditVerdict.Confirm));
  const doubters = computed(() => (selected.value?.checks ?? []).filter((c) => c.verdict === MetadataEditVerdict.Doubt));
  const orderedChecks = computed(() => [...confirmers.value, ...doubters.value]);
  const checksSummary = computed(() => {
    const parts: string[] = [];
    if (confirmers.value.length) parts.push(`${confirmers.value.length} looks right`);
    if (doubters.value.length) parts.push(`${doubters.value.length} ${parts.length ? "doesn't" : "doesn't look right"}`);
    return parts.join(', ');
  });

  function confirmedText(count: number): string {
    return count ? lookedRightText(count) : 'Nobody has checked this yet.';
  }

  function onKeydown(event: KeyboardEvent) {
    if (event.ctrlKey || event.metaKey || event.altKey) return;
    if (event.key === 'Escape' && panel.value) {
      panel.value = null;
      return;
    }
    if (isTypingTarget(event.target) || document.querySelector('.p-dialog')) return;
    const action = queueShortcut(event.key, mode.value);
    if (!action) return;
    if (action === 'reject' || action === 'doubt') event.preventDefault();
    if (action === 'next') move(1);
    else if (action === 'prev') move(-1);
    else if (action === 'approve') approve();
    else if (action === 'reject') openReject();
    else if (action === 'confirm') confirmEdit();
    else if (action === 'doubt') openDoubt();
    else if (action === 'markReviewed') markReviewed();
    else if (action === 'revert') revert();
  }

  onMounted(() => window.addEventListener('keydown', onKeydown));
  onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown));

  const SORTS = computed<{ value: QueueSort; label: string }[]>(() => [
    { value: 'new', label: 'Newest' },
    { value: 'confirmed', label: 'Most said looks right' },
    ...(isReviewer.value ? [{ value: 'doubted' as const, label: 'Most doubted' }] : []),
  ]);

  const sectionLabel = 'm-0 text-xs font-semibold text-muted-color';
  const cardClass = 'rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900';
  const kbdClass = 'hidden sm:inline-block ml-1 px-1 text-[11px] leading-4 rounded border border-b-2 border-current/30 font-mono opacity-80';
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-end justify-between gap-3">
      <div class="flex flex-col gap-1 max-w-3xl">
        <h1 class="m-0 text-xl md:text-2xl font-bold">Review queue</h1>
        <p class="m-0 text-sm text-muted-color">
          Suggested edits waiting for an editor approval. You can help by checking them and vouching for their accuracy. <br />
          You can find every edit in <NuxtLink to="/changes">Recent changes</NuxtLink>.
        </p>
      </div>
      <p class="hidden sm:flex m-0 flex-wrap items-center gap-x-2 text-xs text-muted-color" aria-hidden="true">
        <span><kbd :class="kbdClass">J</kbd><kbd :class="kbdClass">K</kbd> move</span>
        <template v-if="mode === 'review'">
          <span><kbd :class="kbdClass">A</kbd> approve</span>
          <span><kbd :class="kbdClass">R</kbd> reject</span>
        </template>
        <template v-else-if="mode === 'unreviewed'">
          <span><kbd :class="kbdClass">M</kbd> mark reviewed</span>
          <span><kbd :class="kbdClass">R</kbd> revert</span>
        </template>
        <template v-else>
          <span><kbd :class="kbdClass">L</kbd> looks right</span>
          <span><kbd :class="kbdClass">D</kbd> doesn't look right</span>
        </template>
        <span><kbd :class="kbdClass">S</kbd> skip</span>
      </p>
    </div>

    <Tabs v-if="isReviewer" :value="tab" scrollable :show-navigators="false" @update:value="setTab($event as QueueTab)">
      <TabList>
        <Tab value="pending" class="whitespace-nowrap">
          <span class="sm:hidden">Waiting</span><span class="hidden sm:inline">Waiting for review</span>
          <span v-if="counts.pending != null" class="ml-1 text-xs tabular-nums text-muted-color">{{ counts.pending.toLocaleString() }}</span>
        </Tab>
        <Tab value="unreviewed" class="whitespace-nowrap">
          <span class="sm:hidden">Unreviewed</span><span class="hidden sm:inline">Live, unreviewed</span>
          <span v-if="counts.unreviewed != null" class="ml-1 text-xs tabular-nums text-muted-color">{{ counts.unreviewed.toLocaleString() }}</span>
        </Tab>
      </TabList>
    </Tabs>

    <div v-if="deckFilter" class="flex flex-wrap items-center gap-2 text-sm">
      <span class="text-muted-color">Showing edits to one deck.</span>
      <NuxtLink to="/review">Show the whole queue</NuxtLink>
    </div>

    <div class="grid grid-cols-1 lg:grid-cols-[20rem_minmax(0,1fr)] gap-4 items-start">
      <div :class="[cardClass, 'order-2 lg:order-1']">
        <div class="p-3 flex flex-col gap-2 border-b border-surface-200 dark:border-surface-700">
          <div v-if="isReviewer" class="flex flex-wrap gap-1.5" role="group" aria-label="Filter by risk">
            <button type="button" :class="toggleChipClass(tier === null)" :aria-pressed="tier === null" @click="tier = null">All</button>
            <button
              v-for="(label, value) in TIER_LABELS"
              :key="label"
              type="button"
              :class="toggleChipClass(tier === value)"
              :aria-pressed="tier === value"
              @click="tier = value"
            >
              {{ label }}
            </button>
          </div>
          <div class="flex items-center gap-2 text-sm">
            <label for="queue-sort" class="text-muted-color">Sort</label>
            <Select id="queue-sort" v-model="sort" :options="SORTS" option-label="label" option-value="value" size="small" class="grow" />
          </div>
          <label v-if="doneCount" class="flex items-center gap-2 text-sm cursor-pointer">
            <Checkbox v-model="showDone" binary input-id="queue-show-done" />
            <span>{{ mode === 'check' ? 'Show edits you made or checked' : 'Show your own edits' }} ({{ doneCount }})</span>
          </label>
        </div>

        <div v-if="!loaded && !loadFailed" class="p-3 flex flex-col gap-2" aria-busy="true">
          <Skeleton v-for="i in 5" :key="i" height="3.5rem" />
        </div>
        <div v-else-if="loadFailed && !items.length" class="p-4 flex flex-col items-start gap-2 text-sm">
          <p class="m-0">The queue didn't load.</p>
          <Button label="Retry" icon="pi pi-refresh" size="small" @click="load()" />
        </div>
        <p v-else-if="!items.length" class="m-0 p-6 text-center text-sm text-muted-color">
          {{
            mode === 'unreviewed'
              ? 'Every live edit has been reviewed.'
              : tier !== null
                ? 'Nothing waiting at this risk level.'
                : 'Nothing is waiting for review.'
          }}
        </p>
        <div v-else-if="!ordered.length && hasMore && !loadFailed" class="p-3 flex flex-col gap-2" aria-busy="true">
          <Skeleton v-for="i in 5" :key="i" height="3.5rem" />
        </div>
        <div v-else-if="!ordered.length" class="p-6 flex flex-col items-center gap-2 text-center text-sm">
          <span class="text-muted-color">{{ mode === 'check' ? "You've checked everything waiting here." : 'Only your own edits are waiting.' }}</span>
          <Button label="Show them anyway" size="small" severity="secondary" text @click="showDone = true" />
        </div>
        <ul v-else class="list-none m-0 p-0 lg:max-h-[70vh] lg:overflow-y-auto" aria-label="Pending edits">
          <li v-for="group in groups" :key="group.key" class="border-t border-surface-200 dark:border-surface-700 first:border-t-0">
            <div class="flex items-center gap-2.5 px-3 pt-2.5 pb-1">
              <img :src="coverUrl(group.deck.coverName, 'xs')" alt="" class="w-7 h-10 object-cover rounded shrink-0" loading="lazy" />
              <span class="min-w-0 flex-1 font-medium text-sm truncate" v-bind="japaneseTextAttrs(localiseTitle(group.deck))">{{
                localiseTitle(group.deck)
              }}</span>
              <span v-if="group.items.length > 1" class="text-xs text-muted-color tabular-nums" :aria-label="`${group.items.length} edits`">{{
                group.items.length
              }}</span>
            </div>
            <ul class="list-none m-0 p-0 pb-1.5">
              <li v-for="item in group.items" :key="item.id">
                <button
                  type="button"
                  class="w-full text-left flex flex-col gap-0.5 pl-12 pr-3 py-1.5 border-l-2 cursor-pointer focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-primary-500"
                  :class="
                    item.id === selected?.id
                      ? 'bg-primary-50 dark:bg-primary-900/30 border-l-primary-500'
                      : 'border-l-transparent hover:bg-surface-50 dark:hover:bg-surface-800'
                  "
                  :aria-current="item.id === selected?.id ? 'true' : undefined"
                  @click="select(item)"
                >
                  <span class="text-sm truncate">{{ fieldsFromFlags(item.fields).map(fieldLabel).join(', ') }}</span>
                  <span class="flex flex-wrap gap-x-2 text-xs text-muted-color">
                    <span class="truncate max-w-32">{{ item.author.isYou ? 'You' : item.author.displayName }}</span>
                    <span v-if="isReviewer && item.tier != null">{{ tierLabel(item.tier) }}</span>
                    <span class="tabular-nums">{{ formatDateOmitCurrentYear(item.createdAt) }}</span>
                    <span v-if="item.confirmCount">{{ item.confirmCount }} said looks right</span>
                    <span v-if="item.myCheck" class="text-green-700 dark:text-green-400">Checked</span>
                  </span>
                </button>
              </li>
            </ul>
          </li>
          <li v-if="items.length < total" class="p-2 flex justify-center border-t border-surface-200 dark:border-surface-700">
            <Button label="Load more" size="small" severity="secondary" text :loading="loading" @click="load(true)" />
          </li>
        </ul>
      </div>

      <div ref="detailRef" :class="[cardClass, 'order-1 lg:order-2 min-w-0 scroll-mt-4 flex flex-col', selected ? 'max-lg:min-h-dvh' : '']">
        <div v-if="!loaded && !loadFailed" class="p-4 flex flex-col gap-3" aria-busy="true">
          <Skeleton height="5rem" />
          <Skeleton height="10rem" />
        </div>
        <div v-else-if="!selected" class="p-10 text-center flex flex-col gap-1">
          <template v-if="mode === 'unreviewed'">
            <strong>Every live edit has been reviewed.</strong>
            <span class="text-sm text-muted-color">Edits that go live without review will show up here.</span>
          </template>
          <template v-else>
            <strong>Nothing to do here.</strong>
            <span class="text-sm text-muted-color">New suggestions will show up here.</span>
          </template>
        </div>
        <template v-else>
          <div class="flex gap-4 items-start p-5 sm:px-6 border-b border-surface-200 dark:border-surface-700">
            <img :src="coverUrl(selected.deck.coverName, 'sm')" alt="" class="w-14 h-20 object-cover rounded shrink-0" />
            <div class="min-w-0 flex flex-col gap-1">
              <div class="flex flex-wrap items-center gap-2">
                <h2 class="m-0 text-base font-bold [overflow-wrap:anywhere]" v-bind="japaneseTextAttrs(localiseTitle(selected.deck))">
                  {{ localiseTitle(selected.deck) }}
                </h2>
                <Tag v-if="mode === 'unreviewed'" value="Live" severity="success" class="!text-xs !py-0" />
                <Tag v-if="isReviewer && selected.tier != null" :value="tierLabel(selected.tier)" severity="secondary" class="!text-xs !py-0" />
              </div>
              <div class="flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-muted-color">
                <span>{{ getMediaTypeText(selected.deck.mediaType) }}</span>
                <span v-if="selected.deck.releaseYear">{{ selected.deck.releaseYear }}</span>
                <NuxtLink :to="`/decks/media/${selected.deckId}/detail`">Deck page</NuxtLink>
                <NuxtLink :to="`/decks/media/${selected.deckId}/history`">Deck history</NuxtLink>
              </div>
            </div>
          </div>

          <div class="p-5 sm:p-6 flex flex-col gap-6">
            <section class="flex flex-col gap-2" aria-labelledby="changes-heading">
              <h3 id="changes-heading" :class="sectionLabel">Changes</h3>
              <div v-if="choosesChanges" class="flex flex-col divide-y divide-surface-200 dark:divide-surface-700">
                <div v-for="change in orderedChanges" :key="change.field" class="flex gap-2.5 py-2 first:pt-0 last:pb-0">
                  <Checkbox
                    :model-value="isIncluded(selected, change.field)"
                    binary
                    :input-id="`include-${selected.id}-${change.field}`"
                    :aria-label="`Approve the ${fieldLabel(change.field).toLowerCase()} change`"
                    class="mt-0.5"
                    @update:model-value="toggleIncluded(selected, change.field)"
                  />
                  <div class="min-w-0 flex-1" :class="isIncluded(selected, change.field) ? '' : 'opacity-50'">
                    <MetadataChangeList
                      :changes="[change]"
                      :excluded-items="isIncluded(selected, change.field) ? (excludedItems[selected.id] ?? {}) : undefined"
                      @toggle-item="(field, key) => toggleItem(selected!, field, key)"
                    />
                  </div>
                </div>
              </div>
              <MetadataChangeList
                v-else
                :changes="selected.changes"
                :excluded-items="canChoose ? (excludedItems[selected.id] ?? {}) : undefined"
                @toggle-item="(field, key) => toggleItem(selected!, field, key)"
              />
              <div v-if="canChoose && exclusion.leftOut" class="flex flex-col gap-1.5 mt-1">
                <label for="exclude-note" class="text-xs font-semibold text-muted-color">
                  Reason for leaving out {{ leftOutPhrase(exclusion) }}, sent to {{ selected.author.displayName }}
                </label>
                <div class="flex flex-wrap gap-1.5">
                  <button
                    v-for="reason in REJECT_REASONS"
                    :key="reason"
                    type="button"
                    :class="toggleChipClass(noteHasReason(excludeNote, reason))"
                    :aria-pressed="noteHasReason(excludeNote, reason)"
                    @click="excludeNote = toggleNoteReason(excludeNote, reason)"
                  >
                    {{ reason }}
                  </button>
                </div>
                <InputText id="exclude-note" v-model="excludeNote" maxlength="500" placeholder="Or write your own" class="w-full" />
              </div>
            </section>

            <template v-if="hasNames(selected)">
              <MetadataNameCheck v-if="selected.characterNameChecks?.length" :surfaces="selected.characterNameChecks" title="Automatic checks" />
              <div v-if="isReviewer && addsNames(selected)" class="rounded-md border border-surface-200 dark:border-surface-700 overflow-hidden text-sm">
                <div class="flex flex-wrap items-center justify-between gap-2 px-3 py-2 bg-surface-100 dark:bg-surface-800">
                  <span class="font-medium">Segmentation preview</span>
                  <span v-if="namePreviews[selected.id]?.data" class="text-xs text-muted-color tabular-nums">
                    Changes {{ namePreviews[selected.id]!.data!.linesChanged }} of {{ namePreviews[selected.id]!.data!.linesChecked }} lines checked
                  </span>
                </div>
                <p v-if="!namePreviews[selected.id] || namePreviews[selected.id]!.loading" class="m-0 px-3 py-2 text-xs text-muted-color">
                  <i class="pi pi-spin pi-spinner text-xs mr-1" aria-hidden="true" />Parsing lines from the deck with and without these names...
                </p>
                <div v-else-if="namePreviews[selected.id]!.failed" class="px-3 py-2 flex items-center gap-2 text-xs">
                  <span>The preview didn't load.</span>
                  <Button label="Retry" size="small" text @click="retryNamePreview(selected)" />
                </div>
                <template v-else>
                  <div
                    v-for="(example, i) in namePreviews[selected.id]!.data!.examples"
                    :key="i"
                    class="px-3 py-2 border-t border-surface-200 dark:border-surface-700 flex flex-col gap-0.5"
                  >
                    <span lang="ja" class="ja-general">{{ example.line }}</span>
                    <span class="text-xs text-muted-color" lang="ja">Now: {{ example.before }}</span>
                    <span class="text-xs" lang="ja">With this edit: {{ example.after }}</span>
                  </div>
                  <p
                    v-if="!namePreviews[selected.id]!.data!.examples.length"
                    class="m-0 px-3 py-2 text-xs text-muted-color border-t border-surface-200 dark:border-surface-700"
                  >
                    No checked line reads differently with these names.
                  </p>
                </template>
              </div>
              <p v-if="mode === 'review'" class="m-0 text-xs text-muted-color">Approving will re-analyse this deck within the hour.</p>
            </template>

            <div v-if="selected.franchiseEffect" class="rounded-md px-3 py-2 text-sm bg-amber-50 text-amber-900 dark:bg-amber-900/20 dark:text-amber-200">
              <strong>Franchise effect.</strong>
              <template v-if="selected.franchiseEffect.addedDeckTitle && selected.franchiseEffect.into">
                This deck already joined
                <NuxtLink
                  :to="`/franchise/${selected.franchiseEffect.into.franchiseId}`"
                  target="_blank"
                  class="!text-inherit font-medium underline decoration-1 underline-offset-2 hover:decoration-2"
                  v-bind="japaneseTextAttrs(localiseTitle(selected.franchiseEffect.into))"
                  >{{ localiseTitle(selected.franchiseEffect.into) }}</NuxtLink
                >
                ({{ deckCountText(selected.franchiseEffect.into.deckCount) }}), so approving will add
                <span class="font-medium" v-bind="japaneseTextAttrs(selected.franchiseEffect.addedDeckTitle)">{{
                  selected.franchiseEffect.addedDeckTitle
                }}</span>
                to it instead. The edit will move to that deck's history.
              </template>
              <template v-else>
                {{ selected.franchiseEffect.into ? 'Will merge the franchise' : 'Will add this deck to the franchise' }}
                <template v-for="(franchise, i) in selected.franchiseEffect.joining" :key="franchise.franchiseId"
                  ><template v-if="i > 0"> and </template
                  ><NuxtLink
                    :to="`/franchise/${franchise.franchiseId}`"
                    target="_blank"
                    class="!text-inherit font-medium underline decoration-1 underline-offset-2 hover:decoration-2"
                    v-bind="japaneseTextAttrs(localiseTitle(franchise))"
                    >{{ localiseTitle(franchise) }}</NuxtLink
                  >
                  ({{ deckCountText(franchise.deckCount) }})</template
                ><template v-if="selected.franchiseEffect.into">
                  into
                  <NuxtLink
                    :to="`/franchise/${selected.franchiseEffect.into.franchiseId}`"
                    target="_blank"
                    class="!text-inherit font-medium underline decoration-1 underline-offset-2 hover:decoration-2"
                    v-bind="japaneseTextAttrs(localiseTitle(selected.franchiseEffect.into))"
                    >{{ localiseTitle(selected.franchiseEffect.into) }}</NuxtLink
                  >
                  ({{ deckCountText(selected.franchiseEffect.into.deckCount) }}). Franchise pages and study decks built from them will change.</template
                ><template v-else>.</template>
              </template>
            </div>

            <div
              v-for="conflict in isReviewer ? (selected.conflicts ?? []) : []"
              :key="conflict.field"
              class="rounded-md border border-amber-300 dark:border-amber-700 bg-amber-50 dark:bg-amber-900/20 p-3 flex flex-col gap-2"
              role="radiogroup"
              :aria-label="`${fieldLabel(conflict.field)} conflict`"
            >
              <strong class="text-sm">{{ fieldLabel(conflict.field) }} changed after this edit was written</strong>
              <template v-if="isListField(conflict.field)">
                <ul class="list-none m-0 p-0 flex flex-col gap-1 text-sm">
                  <li v-for="clash in clashesFor(conflict)" :key="clash.label" class="[overflow-wrap:anywhere]">
                    <b v-bind="japaneseTextAttrs(clash.label)">{{ clash.label }}</b>
                    <span class="text-muted-color ml-1">now</span> <span v-bind="japaneseTextAttrs(clash.current)">{{ clash.current }}</span>
                    <span class="text-muted-color ml-1">this edit</span> <span v-bind="japaneseTextAttrs(clash.edit)">{{ clash.edit }}</span>
                  </li>
                </ul>
                <div class="flex flex-wrap gap-2">
                  <label
                    v-for="option in CONFLICT_OPTIONS"
                    :key="option"
                    class="flex gap-2 items-center rounded-md border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 px-3 py-2 cursor-pointer text-sm"
                  >
                    <RadioButton
                      :model-value="resolutionFor(selected, conflict)"
                      :value="option"
                      :name="`conflict-${selected.id}-${conflict.field}`"
                      @update:model-value="setResolution(selected, conflict.field, option)"
                    />
                    <span>{{ option === 'current' ? 'Keep current value' : "Use this edit's value" }}</span>
                  </label>
                </div>
                <span class="text-xs text-muted-color">The edit's other changes to this list will be applied either way.</span>
              </template>
              <template v-else>
                <span class="text-xs">Pick the value to keep:</span>
                <label
                  v-for="option in CONFLICT_OPTIONS"
                  :key="option"
                  class="flex gap-2 items-start rounded-md border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 px-3 py-2 cursor-pointer text-sm"
                >
                  <RadioButton
                    :model-value="resolutionFor(selected, conflict)"
                    :value="option"
                    :name="`conflict-${selected.id}-${conflict.field}`"
                    @update:model-value="setResolution(selected, conflict.field, option)"
                  />
                  <span class="min-w-0 [overflow-wrap:anywhere]">
                    <b v-bind="japaneseTextAttrs(describeValue(conflict.field, option === 'current' ? conflict.current : conflict.editAfter))">{{
                      describeValue(conflict.field, option === 'current' ? conflict.current : conflict.editAfter)
                    }}</b>
                    <span class="text-xs text-muted-color ml-1">{{ option === 'current' ? 'current value' : 'this edit' }}</span>
                  </span>
                </label>
              </template>
            </div>

            <section class="flex flex-col gap-2 text-sm [overflow-wrap:anywhere]" aria-labelledby="source-heading">
              <h3 id="source-heading" :class="sectionLabel">
                {{ selected.sources.length > 1 ? `${selected.sources.length} sources` : 'Source' }}
              </h3>
              <a v-for="source in selected.sources" :key="source" :href="source" target="_blank" rel="noopener nofollow ugc" class="self-start">{{ source }}</a>
              <span v-if="!selected.sources.length" class="text-muted-color">No source given.</span>
            </section>

            <section class="flex flex-col gap-2" aria-labelledby="author-heading">
              <h3 id="author-heading" :class="sectionLabel">Submitted by</h3>
              <div class="flex flex-wrap items-center gap-x-2.5 gap-y-1">
                <span
                  class="w-8 h-8 shrink-0 rounded-full grid place-items-center text-sm font-bold bg-primary-100 text-primary-800 dark:bg-primary-900/50 dark:text-primary-100"
                  aria-hidden="true"
                >
                  {{ avatarCharacter(null, selected.author.displayName) }}
                </span>
                <strong class="text-sm">{{ selected.author.isYou ? 'You' : selected.author.displayName }}</strong>
                <Tooltip
                  v-if="isReviewer && selected.authorRecord"
                  :content="`${selected.authorRecord.accepted} accepted, ${selected.authorRecord.reverted} reverted`"
                >
                  <Tag :value="contributorLevelLabel(selected.authorRecord.level)" severity="secondary" class="!text-xs !py-0 cursor-help" />
                </Tooltip>
                <span class="text-xs text-muted-color tabular-nums">{{ formatDateTime(selected.createdAt) }}</span>
              </div>
              <p
                v-if="selected.note"
                class="m-0 ml-[2.625rem] border-l-2 border-surface-300 dark:border-surface-600 pl-3 text-sm whitespace-pre-line [overflow-wrap:anywhere]"
              >
                {{ selected.note }}
              </p>
            </section>

            <section class="flex flex-col gap-2" aria-labelledby="checks-heading">
              <h3 id="checks-heading" :class="sectionLabel">
                Checks <span v-if="isReviewer && orderedChecks.length" class="ml-1 font-normal tabular-nums">{{ checksSummary }}</span>
              </h3>
              <ul v-if="isReviewer && orderedChecks.length" class="m-0 p-0 list-none flex flex-col gap-1">
                <li
                  v-for="check in orderedChecks"
                  :key="`${check.verdict}-${check.displayName}`"
                  :class="[
                    'flex gap-2.5 rounded-md',
                    check.verdict === MetadataEditVerdict.Doubt ? 'items-start -mx-2.5 px-2.5 py-2 bg-amber-50 dark:bg-amber-900/20' : 'items-center py-1',
                  ]"
                >
                  <span
                    class="w-8 h-8 shrink-0 rounded-full grid place-items-center text-sm font-bold bg-surface-100 text-surface-700 dark:bg-surface-800 dark:text-surface-200"
                    aria-hidden="true"
                  >
                    {{ avatarCharacter(null, check.displayName) }}
                  </span>
                  <span class="flex flex-col gap-1 min-w-0 flex-1">
                    <span class="flex flex-wrap items-center gap-x-2 gap-y-1">
                      <strong class="text-sm">{{ check.displayName }}</strong>
                      <Tooltip :content="`${check.accepted} accepted, ${check.matched} matched, ${check.missed} missed`">
                        <Tag :value="contributorLevelLabel(check.level)" severity="secondary" class="!text-xs !py-0 cursor-help" />
                      </Tooltip>
                      <span
                        v-if="check.verdict === MetadataEditVerdict.Confirm"
                        class="ml-auto inline-flex items-center gap-1 text-xs font-semibold text-green-700 dark:text-green-400"
                      >
                        <i class="pi pi-check text-[0.7rem]" aria-hidden="true" />Looks right
                      </span>
                      <span v-else class="ml-auto inline-flex items-center gap-1 text-xs font-semibold text-amber-800 dark:text-amber-300">
                        <i class="pi pi-flag text-[0.7rem]" aria-hidden="true" />Doesn't look right
                      </span>
                    </span>
                    <template v-if="check.verdict === MetadataEditVerdict.Doubt">
                      <span v-if="check.fields" class="mt-0.5 text-xs text-amber-900 dark:text-amber-200"
                        >On: {{ fieldPhrase(fieldsFromFlags(check.fields)).toLowerCase() }}</span
                      >
                      <p
                        v-if="check.note"
                        class="m-0 border-l-2 border-amber-300 dark:border-amber-700 pl-2 text-sm [overflow-wrap:anywhere] whitespace-pre-line"
                      >
                        {{ check.note }}
                      </p>
                    </template>
                  </span>
                </li>
              </ul>
              <p v-else-if="isReviewer && selected.checks" class="m-0 text-sm text-muted-color">Nobody has checked this yet.</p>
              <p v-else class="m-0 text-sm text-muted-color">{{ confirmedText(selected.confirmCount) }}</p>
            </section>
          </div>

          <div v-if="panel === 'doubt'" class="px-5 sm:px-6 py-4 border-t border-surface-200 dark:border-surface-700 flex flex-col gap-2">
            <template v-if="selected.changes.length > 1">
              <span id="doubt-fields-label" class="text-xs font-semibold text-muted-color">Which changes look wrong?</span>
              <div class="flex flex-wrap gap-1.5" role="group" aria-labelledby="doubt-fields-label">
                <button
                  v-for="change in orderedChanges"
                  :key="change.field"
                  type="button"
                  :class="toggleChipClass(doubtFields.includes(change.field))"
                  :aria-pressed="doubtFields.includes(change.field)"
                  @click="toggleDoubtField(change.field)"
                >
                  {{ fieldLabel(change.field) }}
                </button>
              </div>
            </template>
            <span id="doubt-label" class="text-xs font-semibold text-muted-color">What looks wrong?</span>
            <div class="flex flex-wrap gap-1.5" role="group" aria-labelledby="doubt-label">
              <button
                v-for="reason in DOUBT_REASONS"
                :key="reason"
                type="button"
                data-doubt-reason
                :class="toggleChipClass(noteHasReason(doubtNote, reason))"
                :aria-pressed="noteHasReason(doubtNote, reason)"
                @click="doubtNote = toggleNoteReason(doubtNote, reason)"
              >
                {{ reason }}
              </button>
            </div>
            <label for="doubt-note" class="text-xs font-semibold text-muted-color">What did you find? (only editors see this)</label>
            <InputText
              id="doubt-note"
              v-model="doubtNote"
              maxlength="500"
              placeholder="For example, the VNDB page lists a different date"
              class="w-full"
              @keydown.enter.prevent="sendDoubt"
            />
            <div class="flex flex-wrap gap-2">
              <Button label="Send to editors" :disabled="!doubtNote.trim() || !doubtFields.length" :loading="busy" @click="sendDoubt" />
              <Button label="Cancel" severity="secondary" text @click="panel = null" />
            </div>
            <p class="m-0 text-xs text-muted-color">The author won't see who sent it.</p>
          </div>

          <div v-if="panel === 'reject'" class="px-5 sm:px-6 py-4 border-t border-surface-200 dark:border-surface-700 flex flex-col gap-2">
            <label for="reject-note" class="text-xs font-semibold text-muted-color">Reason, sent to {{ selected.author.displayName }}</label>
            <div class="flex flex-wrap gap-1.5">
              <button
                v-for="reason in REJECT_REASONS"
                :key="reason"
                type="button"
                :class="toggleChipClass(noteHasReason(rejectNote, reason))"
                :aria-pressed="noteHasReason(rejectNote, reason)"
                @click="rejectNote = toggleNoteReason(rejectNote, reason)"
              >
                {{ reason }}
              </button>
            </div>
            <InputText id="reject-note" v-model="rejectNote" maxlength="500" placeholder="Or write your own" class="w-full" @keydown.enter.prevent="reject" />
            <div class="flex flex-wrap gap-2">
              <Button label="Reject edit" severity="danger" :disabled="!rejectNote.trim()" :loading="busy" @click="reject" />
              <Button label="Cancel" severity="secondary" text @click="panel = null" />
            </div>
          </div>

          <p v-if="actionError && panel" class="m-0 px-5 sm:px-6 pb-2 text-sm text-red-600 dark:text-red-400" role="alert">{{ actionError }}</p>

          <div
            v-if="!panel"
            ref="actionBarRef"
            class="sticky bottom-0 z-10 mt-auto flex flex-col gap-2 rounded-b-lg border-t border-surface-200 bg-surface-0 px-5 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] sm:px-6 dark:border-surface-700 dark:bg-surface-900"
          >
            <p v-if="actionError" class="m-0 text-sm text-red-600 dark:text-red-400" role="alert">{{ actionError }}</p>
            <div class="flex flex-wrap items-center gap-2">
              <template v-if="mode === 'unreviewed'">
                <Button v-if="selected.canMarkReviewed" :loading="busy" @click="markReviewed">Mark reviewed <kbd :class="kbdClass">M</kbd></Button>
                <span v-else-if="selected.author.isYou" class="text-sm text-muted-color">You can't mark your own edit as reviewed.</span>
                <Button v-if="selected.canRevert" severity="danger" outlined :disabled="busy" @click="revert">Revert <kbd :class="kbdClass">R</kbd></Button>
              </template>
              <template v-else-if="isReviewer">
                <template v-if="selected.canApprove">
                  <Button :loading="busy" :disabled="!canApproveSelection" @click="approve()">{{ approveLabel }} <kbd :class="kbdClass">A</kbd></Button>
                  <Button v-if="flaggedSurfaces.length" severity="secondary" :disabled="busy || !canApproveSelection" @click="approve(flaggedSurfaces)">
                    <span
                      >Approve without <span lang="ja" class="ja-general">{{ flaggedSurfaces.join(', ') }}</span></span
                    >
                  </Button>
                  <Button severity="danger" outlined :disabled="busy" @click="openReject">Reject <kbd :class="kbdClass">R</kbd></Button>
                </template>
                <span v-else-if="selected.author.isYou" class="text-sm text-muted-color">Someone else has to review your own edit.</span>
                <span v-else class="text-sm text-muted-color">Only editors can approve this kind of change.</span>
              </template>
              <template v-else-if="selected.myCheck">
                <Tag
                  :value="selected.myCheck.verdict === MetadataEditVerdict.Confirm ? 'You said it looks right' : 'You said it doesn\'t look right'"
                  :severity="selected.myCheck.verdict === MetadataEditVerdict.Confirm ? 'success' : 'warn'"
                />
                <Button label="Withdraw" severity="secondary" text :loading="busy" @click="withdrawCheck" />
              </template>
              <template v-else-if="selected.canCheck">
                <p class="m-0 w-full text-xs text-muted-color">Help the editors decide by checking the edits against their sources.</p>
                <Button severity="success" outlined :loading="busy" @click="confirmEdit">Looks right <kbd :class="kbdClass">L</kbd></Button>
                <Button severity="secondary" outlined :disabled="busy" @click="openDoubt">Doesn't look right <kbd :class="kbdClass">D</kbd></Button>
              </template>
              <template v-else>
                <span class="text-sm text-muted-color">{{ selected.checkBlockedReason ?? "You can't check this edit." }}</span>
              </template>
            </div>
            <div class="flex items-center gap-2">
              <Button severity="secondary" text :disabled="position <= 1" @click="move(-1)">Previous <kbd :class="kbdClass">K</kbd></Button>
              <span class="text-xs tabular-nums text-muted-color">{{ position }} of {{ reachableTotal }}</span>
              <Button severity="secondary" text class="ml-auto" @click="move(1)">{{ stepLabel }} <kbd :class="kbdClass">S</kbd></Button>
            </div>
          </div>
        </template>
      </div>
    </div>
  </div>
</template>
