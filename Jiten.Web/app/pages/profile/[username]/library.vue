<script setup lang="ts">
  import { type MediaListDeck, type MediaListEntrySummary, DisplayStyle, DeckStatus, MediaType, DeckDownloadType, DeckOrder, SortOrder } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { createCookieState } from '~/stores/jitenStore';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';
  import { storeToRefs } from 'pinia';
  import { getDeckStatusColourClass, getDeckStatusText, isInProgressStatus } from '~/utils/deckStatusMapper';
  import { getMediaTypePluralText, getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { type DeckSortOption, deckSortMeta, deckSortOption, deckSortOrdering, sortDecks } from '~/utils/deckSorting';
  import { LazyHydrateMediaDeckCard, LazyHydrateMediaDeckCompactView, LazyHydrateMediaDeckTableView } from '~/utils/lazyHydratedComponents';
  import { coverUrl } from '~/utils/coverImage';
  import { useConfirm } from 'primevue/useconfirm';
  import { formatReadDate } from '~/utils/mediaListEntry';
  import type { EntryDialogField } from '~/composables/useMediaListSync';
  import { normaliseTitleQuery, titleMatches } from '~/utils/titleSearch';
  import { displayDeckId, isMainEntry } from '~/utils/displayDeck';

  const route = useRoute();
  const router = useRouter();
  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const displayStore = useDisplayStyleStore();
  const { displayStyle } = storeToRefs(displayStore);

  const { handle: targetUsername, isOwner: isOwnProfile, viewAsQuery, apiPath } = useProfileContext();

  const {
    data: decks,
    status,
    error,
  } = await useApiFetch<MediaListDeck[]>(() => apiPath('media-list'), {
    query: viewAsQuery.value,
  });

  const isLoading = computed(() => status.value === 'pending');
  const notAvailable = computed(() => apiStatusCode(error.value) === 404);

  /** Tab order; only non-empty statuses get a tab. */
  const statusOrder: DeckStatus[] = [DeckStatus.Ongoing, DeckStatus.Paused, DeckStatus.Completed, DeckStatus.Planning, DeckStatus.Dropped];

  const STATUS_ACTIONS: { status: DeckStatus; icon: string; severity: string }[] = [
    { status: DeckStatus.Planning, icon: 'pi pi-bookmark', severity: 'secondary' },
    { status: DeckStatus.Ongoing, icon: 'pi pi-play', severity: 'warn' },
    { status: DeckStatus.Paused, icon: 'pi pi-pause', severity: 'info' },
    { status: DeckStatus.Completed, icon: 'pi pi-check-circle', severity: 'success' },
    { status: DeckStatus.Dropped, icon: 'pi pi-times-circle', severity: 'danger' },
  ];
  const statusSeverity = (status: DeckStatus) => STATUS_ACTIONS.find((a) => a.status === status)?.severity;

  /** Client-side, like title search and sorting; the full list is already loaded. */
  const mediaTypeFilter = ref<MediaType | null>(parseTypeQuery(route.query.type));
  const titleSearch = ref(typeof route.query.q === 'string' ? route.query.q : '');
  const sortBy = ref<string>(parseSortByQuery(route.query.sortBy));
  const sortOrder = ref<SortOrder>(parseSortOrderQuery(route.query.sortOrder) ?? deckSortMeta[sortBy.value]!.default);

  function parseTypeQuery(value: unknown): MediaType | null {
    const parsed = Number(Array.isArray(value) ? value[0] : value);
    const known = Object.values(MediaType).filter((v) => typeof v === 'number') as MediaType[];
    return known.includes(parsed as MediaType) ? (parsed as MediaType) : null;
  }

  function parseSortByQuery(value: unknown): string {
    const key = Array.isArray(value) ? value[0] : value;
    return typeof key === 'string' && key in deckSortMeta ? key : 'title';
  }

  function parseSortOrderQuery(value: unknown): SortOrder | null {
    const parsed = Number(Array.isArray(value) ? value[0] : value);
    return parsed === SortOrder.Ascending || parsed === SortOrder.Descending ? (parsed as SortOrder) : null;
  }

  const presentTypes = computed(() => new Set((decks.value ?? []).map((d) => d.mediaType)));

  /** Facet counts follow the active status tab and title search but ignore the type filter itself, so every option shows what picking it would yield. */
  const typeCounts = computed(() => {
    const status = activeGroup.value?.status;
    const counts = new Map<MediaType, number>();
    for (const d of decks.value ?? []) {
      if (status === null ? !isListed(d) : status !== undefined && shownStatus(d) !== status) continue;
      if (searchKey.value && !titleMatches(d, searchKey.value)) continue;
      counts.set(d.mediaType, (counts.get(d.mediaType) ?? 0) + 1);
    }
    return counts;
  });

  const mediaTypeOptions = computed(() => {
    const present = [...presentTypes.value].sort((a, b) => a - b);
    const counts = typeCounts.value;
    const total = [...counts.values()].reduce((sum, n) => sum + n, 0);
    return [
      { label: 'All types', value: null as MediaType | null, count: total },
      ...present.map((t) => ({ label: getMediaTypeText(t), value: t as MediaType | null, count: counts.get(t) ?? 0 })),
    ];
  });

  const audioVisualTypes = [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube];
  /** Types with example sentences, which is also what makes the sentence length sort meaningful. */
  const sentenceMediaTypes = [
    MediaType.Novel,
    MediaType.NonFiction,
    MediaType.VideoGame,
    MediaType.VisualNovel,
    MediaType.WebNovel,
    MediaType.Anime,
    MediaType.Drama,
    MediaType.Movie,
    MediaType.YouTube,
  ];

  /** Coverage sorts are always the viewer's own, even on someone else's list. */
  const sortGroups = computed(() => {
    const types = [...presentTypes.value];

    const general = [
      'title',
      'difficulty',
      'subdeckCount',
      'extRating',
      'uKanji',
      'uWordCount',
      'wordCount',
      'uKanjiOnce',
      'communityVotes',
      'releaseDate',
      'addedDate',
    ];
    if ((decks.value ?? []).some((d) => d.listEntry?.lastCompletedOn)) general.push('finishedDate');
    if (auth.isAuthenticated) general.push('uCoverage', 'coverage', 'uTotalCoverage', 'totalCoverage');
    if (types.some((t) => sentenceMediaTypes.includes(t))) general.push('sentenceLength');
    general.sort((a, b) => deckSortOrdering.indexOf(a) - deckSortOrdering.indexOf(b));

    const groups: { label: string; items: DeckSortOption[] }[] = [{ label: 'General', items: general.map(deckSortOption) }];

    if (types.some((t) => !audioVisualTypes.includes(t))) {
      groups.push({ label: 'Novel', items: ['charCount', 'dialoguePercentage'].map(deckSortOption) });
    }
    if (types.some((t) => audioVisualTypes.includes(t))) {
      groups.push({ label: 'Audio-Video', items: ['speechSpeed', 'speechDuration'].map(deckSortOption) });
    }

    return groups;
  });

  /** First runs before the query-sync watchers exist, so a stale key falls back without a redirect on load. */
  function resetUnavailableSort() {
    if (sortGroups.value.some((g) => g.items.some((i) => i.value === sortBy.value))) return;
    sortBy.value = 'title';
    sortOrder.value = deckSortMeta.title!.default;
  }
  watch(sortGroups, resetUnavailableSort, { immediate: true });

  const sortOrderLabel = computed(() => {
    const meta = deckSortMeta[sortBy.value];
    if (!meta) return sortOrder.value === SortOrder.Ascending ? 'Ascending' : 'Descending';
    return sortOrder.value === SortOrder.Ascending ? meta.asc : meta.desc;
  });

  watch(sortBy, (value) => {
    sortOrder.value = deckSortMeta[value]?.default ?? SortOrder.Ascending;
  });

  watch([sortBy, sortOrder, mediaTypeFilter, titleSearch], () => {
    router.replace({
      query: {
        ...route.query,
        sortBy: sortBy.value === 'title' ? undefined : sortBy.value,
        sortOrder: sortBy.value === 'title' && sortOrder.value === deckSortMeta.title!.default ? undefined : sortOrder.value,
        type: mediaTypeFilter.value ?? undefined,
        q: titleSearch.value.trim() || undefined,
      },
    });
  });

  const typedDecks = computed(() => {
    const list = decks.value ?? [];
    return mediaTypeFilter.value === null ? list : list.filter((d) => d.mediaType === mediaTypeFilter.value);
  });

  const searchKey = computed(() => normaliseTitleQuery(titleSearch.value));
  const filteredDecks = computed(() => (searchKey.value ? typedDecks.value.filter((d) => titleMatches(d, searchKey.value)) : typedDecks.value));

  const sortedDecks = computed(() => sortDecks(filteredDecks.value, sortBy.value, sortOrder.value));

  const heldStatuses = reactive(new Map<number, DeckStatus | undefined>());
  function holdInTab(deckId: number, open: boolean) {
    if (open) heldStatuses.set(deckId, decks.value?.find((d) => d.deckId === deckId)?.status);
    else heldStatuses.delete(deckId);
  }

  const shownStatus = (d: MediaListDeck) => (heldStatuses.has(d.deckId) ? heldStatuses.get(d.deckId) : d.status);

  type StatusGroup = { key: string; status: DeckStatus | null; label: string; decks: MediaListDeck[] };

  const isListed = (d: MediaListDeck) => statusOrder.includes(shownStatus(d)!);

  /** Tabs come from the unfiltered list, so the type filter and search only change counts and never add or remove tabs. */
  const statusGroups = computed<StatusGroup[]>(() =>
    statusOrder
      .filter((s) => (decks.value ?? []).some((d) => shownStatus(d) === s))
      .map((s) => ({
        key: s.toString(),
        status: s,
        label: getDeckStatusText(s),
        decks: sortedDecks.value.filter((d) => shownStatus(d) === s),
      }))
  );

  /** The All tab only appears once there are two statuses to combine. */
  const groups = computed<StatusGroup[]>(() => {
    if (statusGroups.value.length < 2) return statusGroups.value;
    return [{ key: 'all', status: null, label: 'All', decks: sortedDecks.value.filter(isListed) }, ...statusGroups.value];
  });

  const selectedTabRaw = ref<string>('');
  /** Defaults to the first status rather than All; only a title search hands an empty tab over to the first tab with matches, and clearing it returns to the chosen tab. */
  const activeGroup = computed(() => {
    const g = groups.value;
    if (!g.length) return undefined;
    const chosen = g.find((x) => x.key === selectedTabRaw.value) ?? statusGroups.value[0]!;
    if (!searchKey.value || chosen.decks.length) return chosen;
    return g.find((x) => x.decks.length) ?? chosen;
  });
  const emptyTabMessage = computed(() => {
    const status = activeGroup.value?.status ? `${activeGroup.value.label.toLowerCase()} ` : '';
    const subject = mediaTypeFilter.value === null ? 'titles' : getMediaTypePluralText(mediaTypeFilter.value);
    const query = titleSearch.value.trim();
    return `No ${status}${subject}${query ? ` match "${query}"` : ''}.`;
  });

  const selectedTab = computed({
    get: () => activeGroup.value?.key ?? '',
    set: (v: string) => {
      selectedTabRaw.value = v;
    },
  });

  function updateDeckInList(updated: MediaListDeck) {
    if (!decks.value) return;
    const replaced = replaceDeck(decks.value, updated);
    if (replaced !== decks.value) decks.value = replaced;
  }

  const toast = useToast();
  const confirm = useConfirm();

  const editMode = ref(false);
  const libraryView = createCookieState<'list' | 'cards'>('library-view', 'list');
  const showListRows = computed(() => editMode.value || libraryView.value === 'list');
  const canEditRow = computed(() => isOwnProfile.value && !editMode.value);
  /** A viewer whose coverage hasn't been computed gets zeros, which would read as 0% on every row. */
  const showViewerCoverage = computed(() => auth.isAuthenticated && !isOwnProfile.value && !!decks.value?.some((d) => d.coverage > 0));
  const viewOptions = [
    { label: 'List', value: 'list' },
    { label: 'Cards', value: 'cards' },
  ];
  const selected = ref<number[]>([]);
  const bulkBusy = ref(false);
  const statusMenu = ref();
  const visibilityMenu = ref();
  const exportMenu = ref();
  const exporting = ref(false);

  function toggleSelected(deckId: number) {
    selected.value = selected.value.includes(deckId) ? selected.value.filter((id) => id !== deckId) : [...selected.value, deckId];
  }

  const allSelected = computed(() => {
    const count = activeGroup.value?.decks.length ?? 0;
    return count > 0 && selected.value.length === count;
  });

  function toggleSelectAll() {
    selected.value = allSelected.value ? [] : (activeGroup.value?.decks ?? []).map((d) => d.deckId);
  }

  const statusMenuItems = [
    ...STATUS_ACTIONS.map(({ status }) => ({
      label: getDeckStatusText(status),
      command: () => bulkSetStatus(status),
    })),
    { separator: true },
    { label: 'Clear status', command: () => bulkSetStatus(DeckStatus.None) },
  ];

  function confirmDanger(header: string, message: string, acceptLabel: string, accept: () => unknown) {
    confirm.require({
      message,
      header,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel,
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      rejectProps: { severity: 'secondary' },
      accept,
    });
  }

  type BulkResult = { affected: number; skipped: number; listEntries?: Record<number, MediaListEntrySummary> | null };

  async function runBulk(deckIds: number[], body: Record<string, unknown>): Promise<BulkResult | null> {
    bulkBusy.value = true;
    try {
      return await $api<BulkResult>('user/deck-preferences/bulk', {
        method: 'POST',
        body: { deckIds, ...body },
      });
    } catch {
      toast.add({ severity: 'error', summary: 'Edit failed', detail: 'Your list is unchanged. Please try again.', life: 6000 });
      return null;
    } finally {
      bulkBusy.value = false;
    }
  }

  function bulkSetStatus(status: DeckStatus) {
    if (status !== DeckStatus.None) {
      void applyBulkStatus(status);
      return;
    }
    confirmDanger('Clear status', `Clear the status of ${selected.value.length} titles? Their dates and character counts will be deleted too.`, 'Clear', () =>
      applyBulkStatus(status)
    );
  }

  /** A `patch` returning null drops the row from the list. */
  async function runBulkOnSelection(body: Record<string, unknown>, patch: (deck: MediaListDeck, result: BulkResult) => MediaListDeck | null, summary: string) {
    const ids = new Set(selected.value);
    const result = await runBulk(selected.value, body);
    if (!result || !decks.value) return;

    decks.value = decks.value.flatMap((d) => {
      if (!ids.has(d.deckId)) return [d];
      const next = patch(d, result);
      return next ? [next] : [];
    });
    selected.value = [];
    toast.add({ severity: 'success', summary, detail: `${result.affected} titles updated.`, life: 4000 });
  }

  const applyBulkStatus = (status: DeckStatus) =>
    runBulkOnSelection(
      { status },
      (d, result) => (status === DeckStatus.None ? null : { ...d, status, listEntry: result.listEntries?.[d.deckId] ?? null }),
      'Status updated'
    );

  async function setFavourite(deckIds: number[], isFavourite: boolean) {
    const ids = new Set(deckIds);
    const result = await runBulk(deckIds, { isFavourite });
    if (!result || !decks.value) return null;

    decks.value = decks.value.map((d) => (ids.has(d.deckId) ? { ...d, isFavourite } : d));
    return result;
  }

  async function removeDecks(deckIds: number[]) {
    const ids = new Set(deckIds);
    const result = await runBulk(deckIds, { remove: true });
    if (!result || !decks.value) return null;

    decks.value = decks.value.filter((d) => !ids.has(d.deckId));
    selected.value = selected.value.filter((id) => !ids.has(id));
    return result;
  }

  const bulkFavourite = (isFavourite: boolean) =>
    runBulkOnSelection({ isFavourite }, (d) => ({ ...d, isFavourite }), isFavourite ? 'Favourited' : 'Unfavourited');

  const bulkSetPrivate = (isPrivate: boolean) =>
    runBulkOnSelection({ isPrivate }, (d) => ({ ...d, isPrivate }), isPrivate ? 'Hidden from your profile' : 'Shown on your profile');

  const visibilityMenuItems = [
    { label: 'Hide from profile', icon: 'pi pi-eye-slash', command: () => bulkSetPrivate(true) },
    { label: 'Show on profile', icon: 'pi pi-eye', command: () => bulkSetPrivate(false) },
  ];

  function bulkRemove() {
    confirmDanger(
      'Remove from list',
      `Remove ${selected.value.length} titles from your list? Their status, dates and character counts will be cleared.`,
      'Remove',
      async () => {
        const result = await removeDecks(selected.value);
        if (result) toast.add({ severity: 'success', summary: 'Removed', detail: `${result.affected} titles removed from your list.`, life: 4000 });
      }
    );
  }

  const exportMenuItems = [
    {
      label: 'Export full list',
      items: [
        { label: 'CSV', icon: 'pi pi-file-export', command: () => exportList('csv') },
        { label: 'JSON', icon: 'pi pi-file-export', command: () => exportList('json') },
      ],
    },
  ];

  async function exportList(format: 'csv' | 'json') {
    exporting.value = true;
    try {
      const blob = await $api<Blob>(`user/media-list/export?format=${format}`, { responseType: 'blob' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `jiten-media-list.${format}`;
      a.click();
      URL.revokeObjectURL(url);
    } catch {
      toast.add({ severity: 'error', summary: 'Export failed', detail: 'Could not export your media list. Please try again.', life: 6000 });
    } finally {
      exporting.value = false;
    }
  }

  const canLogProgress = (deck: MediaListDeck) => isOwnProfile.value && isInProgressStatus(deck.status);
  const canPlan = (deck: MediaListDeck) => !isOwnProfile.value && auth.isAuthenticated && deck.viewerStatus != null;

  const { postDeckStatus } = useMediaListApi();
  const planning = ref(new Set<number>());

  async function addToPlanning(deck: MediaListDeck) {
    planning.value.add(deck.deckId);
    try {
      await postDeckStatus(deck, DeckStatus.Planning);
    } finally {
      planning.value.delete(deck.deckId);
    }
  }

  onMediaListChange((change) => {
    if (!isOwnProfile.value) {
      const viewed = change.status === undefined ? undefined : decks.value?.find((d) => d.deckId === change.deckId);
      if (viewed) updateDeckInList({ ...viewed, viewerStatus: change.status });
      return;
    }
    const deck = decks.value?.find((d) => d.deckId === change.deckId);
    if (!deck) return;
    const updated = applyMediaListChange(deck, change);
    if (updated === deck) return;
    if (updated.status !== deck.status) selected.value = selected.value.filter((id) => id !== deck.deckId);
    updateDeckInList(updated);
  });

  const rowMenuOpenFor = ref<number | null>(null);
  const rowMenuDeck = computed(() => decks.value?.find((d) => d.deckId === rowMenuOpenFor.value) ?? null);
  const rowFlow = useStatusFlow(() => rowMenuDeck.value!, { close: closeRowMenu, isOpen: () => rowMenuOpenFor.value != null });

  const { menu: rowMenu, rememberTrigger: rememberRowTrigger, restoreFocus: restoreRowFocus, focusList: focusRowMenu } = useMenuFocus();

  function toggleRowMenu(deckId: number, event: Event) {
    if (rowMenuOpenFor.value === deckId) return closeRowMenu();
    closeRowMenu();
    rememberRowTrigger(event);
    rowMenuOpenFor.value = deckId;
    holdInTab(deckId, true);
    void focusRowMenu();
  }

  function closeRowMenu() {
    if (rowMenuOpenFor.value == null) return;
    restoreRowFocus();
    holdInTab(rowMenuOpenFor.value, false);
    rowMenuOpenFor.value = null;
    rowFlow.reset();
  }

  watch(rowFlow.step, (step) => {
    if (step === 'list' && rowMenuOpenFor.value != null) void focusRowMenu();
  });

  watch([() => activeGroup.value?.status, editMode], () => {
    selected.value = [];
    closeRowMenu();
  });

  const openEntryDialog = useEntryDialog();

  function openRowHistory(deck: MediaListDeck, field: EntryDialogField | null = null) {
    closeRowMenu();
    openEntryDialog(deck, field);
  }

  function getRowActions(deck: MediaListDeck) {
    const actions: { label: string; icon: string; action: () => void; severity?: string; keepOpen?: boolean; isStatus?: boolean }[] = STATUS_ACTIONS.filter(
      (m) => m.status !== deck.status
    ).map((m) => ({
      label: `Move to ${getDeckStatusText(m.status)}`,
      icon: m.icon,
      action: () => rowFlow.choose(m.status),
      keepOpen: true,
      isStatus: true,
    }));
    actions.unshift({ label: 'Your log', icon: 'pi pi-book', action: () => openRowHistory(deck) });

    actions.push({
      label: deck.isFavourite ? 'Unfavourite' : 'Favourite',
      icon: deck.isFavourite ? 'pi pi-star-fill' : 'pi pi-star',
      action: () => setFavourite([deck.deckId], !deck.isFavourite),
    });
    actions.push({
      label: deck.isPrivate ? 'Show on profile' : 'Hide from profile',
      icon: deck.isPrivate ? 'pi pi-eye' : 'pi pi-eye-slash',
      action: () => deckDetails.toggleHidden(deck),
    });
    if (deck.isPinned || !deck.isPrivate)
      actions.push({
        label: deck.isPinned ? 'Unpin from profile' : 'Pin to profile',
        icon: 'pi pi-thumbtack',
        action: () => toggleProfilePin(deck),
      });
    actions.push({ label: 'Add to community list', icon: 'pi pi-list', action: () => openListPicker([deck]) });
    actions.push({ label: 'Remove from list', icon: 'pi pi-trash', severity: 'danger', action: () => confirmRemoveDeck(deck) });

    return actions;
  }

  const profilePins = useProfilePins();
  const deckDetails = useDeckDetails();

  /** A pin at the cap can replace another title, so every row's flag is resynced from the pin list. */
  async function toggleProfilePin(deck: MediaListDeck) {
    if (!(await deckDetails.togglePinned(deck, localiseTitle(deck), !deck.isPinned)) || !decks.value) return;
    const pinnedIds = profilePins.pinnedIds.value;
    if (pinnedIds) decks.value = decks.value.map((d) => (d.isPinned === pinnedIds.includes(d.deckId) ? d : { ...d, isPinned: pinnedIds.includes(d.deckId) }));
  }

  function confirmRemoveDeck(deck: MediaListDeck) {
    confirmDanger('Remove from list', `Remove "${localiseTitle(deck)}" from your list? Its dates and character counts will be deleted too.`, 'Remove', () =>
      removeDecks([deck.deckId])
    );
  }

  const bulkBarActions = computed((): { label: string; icon: string; severity: string; run: (e: Event) => void }[] => [
    { label: 'Set status', icon: 'pi pi-tag', severity: 'secondary', run: (e: Event) => statusMenu.value?.toggle(e) },
    { label: 'Favourite', icon: 'pi pi-star', severity: 'secondary', run: () => bulkFavourite(true) },
    { label: 'Unfavourite', icon: 'pi pi-star-fill', severity: 'secondary', run: () => bulkFavourite(false) },
    { label: 'Profile visibility', icon: 'pi pi-eye-slash', severity: 'secondary', run: (e: Event) => visibilityMenu.value?.toggle(e) },
    { label: 'Add to community list', icon: 'pi pi-list', severity: 'secondary', run: () => openListPicker(selectedDecks()) },
    { label: 'Remove', icon: 'pi pi-trash', severity: 'danger', run: () => bulkRemove() },
  ]);

  const listPickerVisible = ref(false);
  const listPickerDeckIds = ref<number[]>([]);
  const listPickerTitle = ref<string | null>(null);

  const selectedDecks = () => {
    const ids = new Set(selected.value);
    return (decks.value ?? []).filter((d) => ids.has(d.deckId));
  };

  /** Community lists hold series, so a volume is added as its series. */
  function openListPicker(picked: MediaListDeck[]) {
    listPickerDeckIds.value = [...new Set(picked.map(displayDeckId))];
    listPickerTitle.value = picked.length === 1 && isMainEntry(picked[0]!) ? localiseTitle(picked[0]!) : null;
    listPickerVisible.value = true;
  }

  const downloadVisible = ref(false);
  const downloadMediaList = ref<{
    apiBase: string;
    title: string;
    totalWords: number;
    hasExampleSentences: boolean;
    query: Record<string, string | undefined>;
  } | null>(null);

  const openDownload = () => {
    const g = activeGroup.value;
    if (!g || g.status === null) return;
    const apiBase = apiPath(`media-list/${g.status}`);

    downloadMediaList.value = {
      apiBase,
      title: `${targetUsername.value} - ${g.label}`,
      totalWords: g.decks.reduce((sum, d) => sum + (d.uniqueWordCount || 0), 0),
      hasExampleSentences: g.decks.some((d) => sentenceMediaTypes.includes(d.mediaType)),
      query: viewAsQuery.value,
    };
    downloadVisible.value = true;

    $api<number>(`${apiBase}/vocabulary-count`, {
      method: 'POST',
      query: viewAsQuery.value,
      body: { downloadType: DeckDownloadType.Full, order: DeckOrder.DeckFrequency, minFrequency: 0, maxFrequency: 0 },
    })
      .then((real) => {
        if (typeof real === 'number' && real > 0 && downloadMediaList.value?.apiBase === apiBase) {
          downloadMediaList.value = { ...downloadMediaList.value, totalWords: real };
        }
      })
      .catch(() => {});
  };

  useHead(() => ({
    title: `${targetUsername.value} - Library`,
  }));
</script>

<template>
  <ProfileTabGate section="library">
    <div class="flex flex-col gap-4" :class="{ 'pb-24': editMode }" @click="closeRowMenu">
      <div v-if="isLoading && !decks" class="flex flex-col gap-4 lg:flex-row lg:items-start" aria-busy="true">
        <div class="flex gap-1 lg:w-52 lg:shrink-0 lg:flex-col">
          <Skeleton v-for="i in 5" :key="i" height="2.25rem" class="max-lg:!w-24" />
        </div>
        <div class="flex min-w-0 flex-1 flex-col gap-3">
          <Skeleton width="12rem" height="2rem" />
          <div
            class="divide-y divide-surface-100 rounded-xl border border-surface-200 bg-surface-0 dark:divide-surface-800 dark:border-surface-700 dark:bg-surface-900"
          >
            <div v-for="i in 6" :key="i" class="flex items-center gap-3 px-3 py-2.5">
              <Skeleton width="2.5rem" height="3.5rem" />
              <div class="flex flex-1 flex-col gap-1.5">
                <Skeleton width="45%" height="1rem" />
                <Skeleton width="25%" height="0.75rem" />
              </div>
            </div>
          </div>
        </div>
      </div>

      <ProfileLockedSection v-else-if="notAvailable" section="library" />

      <div v-else-if="error" class="text-center py-8">
        <Message severity="error">Failed to load media list.</Message>
      </div>

      <template v-else>
        <h2 class="sr-only">Library</h2>

        <div v-if="!decks?.length" class="text-center py-12">
          <Message severity="info">No tracked media yet. Set a status on a title to see it here.</Message>
        </div>

        <template v-else>
          <div class="flex flex-col gap-4 lg:flex-row lg:items-start">
            <aside class="flex flex-col gap-4 lg:sticky lg:top-4 lg:w-52 lg:shrink-0 lg:gap-0" aria-label="Library filters">
              <div class="hidden px-3 pb-1 text-xs font-semibold text-surface-500 lg:block dark:text-surface-400">Status</div>
              <nav aria-label="Status" class="scrollbar-styled -mx-1 flex gap-1 overflow-x-auto px-1 lg:mx-0 lg:flex-col lg:overflow-visible lg:px-0">
                <button
                  v-for="g in groups"
                  :key="g.key"
                  type="button"
                  :aria-current="selectedTab === g.key ? 'true' : undefined"
                  class="flex shrink-0 cursor-pointer items-center justify-between gap-3 rounded-md px-3 py-2 text-sm transition-colors max-lg:border"
                  :class="
                    selectedTab === g.key
                      ? 'bg-primary-50 font-semibold text-primary-700 max-lg:border-primary-300 dark:bg-primary-900/30 dark:text-primary-200 max-lg:dark:border-primary-700'
                      : g.decks.length
                        ? 'text-surface-700 hover:bg-surface-100 max-lg:border-surface-200 dark:text-surface-200 dark:hover:bg-surface-800 max-lg:dark:border-surface-700'
                        : 'text-muted-color hover:bg-surface-100 max-lg:border-surface-200 dark:hover:bg-surface-800 max-lg:dark:border-surface-700'
                  "
                  @click="selectedTab = g.key"
                >
                  <span>{{ g.label }}</span>
                  <span class="text-xs tabular-nums text-muted-color">{{ g.decks.length }}</span>
                </button>
              </nav>

              <div
                class="mb-1 mt-3 hidden border-t border-surface-200 px-3 pt-3 text-xs font-semibold text-surface-500 lg:block dark:border-surface-700 dark:text-surface-400"
              >
                Sort and filter
              </div>
              <div class="grid grid-cols-[minmax(0,1fr)_auto] gap-2 sm:grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)] lg:grid-cols-[minmax(0,1fr)_auto]">
                <Select
                  v-model="sortBy"
                  :options="sortGroups"
                  option-label="label"
                  option-value="value"
                  option-group-label="label"
                  option-group-children="items"
                  placeholder="Sort by"
                  aria-label="Sort by"
                  size="small"
                  scroll-height="50vh"
                >
                  <template #optiongroup="{ option }">
                    <div class="text-xs font-semibold text-surface-500 dark:text-surface-400 py-0.5 px-1">{{ option.label }}</div>
                  </template>
                </Select>
                <Tooltip :content="sortOrderLabel">
                  <Button
                    :icon="sortOrder === SortOrder.Ascending ? 'pi pi-arrow-up' : 'pi pi-arrow-down'"
                    :aria-label="`Sort order: ${sortOrderLabel}`"
                    size="small"
                    severity="secondary"
                    @click="sortOrder = sortOrder === SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending"
                  />
                </Tooltip>
                <Select
                  v-if="mediaTypeOptions.length > 2"
                  v-model="mediaTypeFilter"
                  :options="mediaTypeOptions"
                  option-label="label"
                  option-value="value"
                  placeholder="All types"
                  aria-label="Filter by media type"
                  size="small"
                  scroll-height="50vh"
                  class="col-span-2 sm:col-span-1 lg:col-span-2"
                >
                  <template #option="{ option }">
                    <div class="flex w-full items-center justify-between gap-3" :class="{ 'text-muted-color': option.count === 0 }">
                      <span>{{ option.label }}</span>
                      <span class="text-xs tabular-nums text-muted-color">{{ option.count }}</span>
                    </div>
                  </template>
                </Select>
                <IconField class="col-span-2 sm:col-span-3 lg:col-span-2">
                  <InputIcon class="pi pi-search" />
                  <InputText v-model="titleSearch" placeholder="Search titles" aria-label="Search titles" size="small" class="w-full" />
                </IconField>
              </div>
            </aside>

            <div class="flex min-w-0 flex-1 flex-col gap-3">
              <div class="flex flex-wrap items-center gap-2">
                <template v-if="activeGroup">
                  <Tooltip v-if="activeGroup.status === null" content="Pick a status to download its vocab">
                    <span tabindex="0"><Button label="Download vocab" icon="pi pi-download" size="small" outlined disabled /></span>
                  </Tooltip>
                  <Button v-else :label="`Download ${activeGroup.label} vocab`" icon="pi pi-download" size="small" outlined @click="openDownload" />
                </template>
                <div class="ml-auto flex items-center gap-2">
                  <template v-if="isOwnProfile">
                    <Button
                      :label="editMode ? 'Done' : 'Edit'"
                      :icon="editMode ? 'pi pi-check' : 'pi pi-pencil'"
                      size="small"
                      :text="!editMode"
                      :severity="editMode ? undefined : 'secondary'"
                      @click="editMode = !editMode"
                    />
                    <Button
                      icon="pi pi-ellipsis-h"
                      aria-label="More list actions"
                      aria-haspopup="true"
                      size="small"
                      text
                      severity="secondary"
                      :loading="exporting"
                      @click="exportMenu?.toggle($event)"
                    />
                    <Menu ref="exportMenu" :model="exportMenuItems" popup />
                  </template>
                  <SelectButton
                    v-if="!editMode"
                    v-model="libraryView"
                    :options="viewOptions"
                    option-label="label"
                    option-value="value"
                    :allow-empty="false"
                    aria-label="Library layout"
                    size="small"
                  />
                  <DisplayStyleSelector v-if="!editMode && libraryView === 'cards'" />
                </div>
              </div>

              <p v-if="activeGroup && !activeGroup.decks.length" class="py-10 text-center text-sm text-muted-color">{{ emptyTabMessage }}</p>
              <template v-else-if="showListRows && activeGroup">
                <div v-if="editMode" class="flex items-center gap-3 px-3 py-2 -mb-2 text-sm text-surface-500 dark:text-surface-400">
                  <Checkbox :model-value="allSelected" :binary="true" @change="toggleSelectAll" />
                  <span class="text-xs cursor-pointer select-none" @click="toggleSelectAll">
                    {{ selected.length > 0 ? `${selected.length} selected` : `Select all (${activeGroup.decks.length})` }}
                  </span>
                </div>

                <div
                  class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm divide-y divide-surface-100 dark:divide-surface-800"
                >
                  <component
                    :is="editMode ? 'label' : 'div'"
                    v-for="deck in activeGroup.decks"
                    :key="deck.deckId"
                    class="flex flex-wrap items-center gap-x-2 gap-y-1.5 sm:flex-nowrap sm:gap-3 px-3 py-2 sm:py-2.5 transition-colors first:rounded-t-xl last:rounded-b-xl hover:bg-surface-50 dark:hover:bg-surface-800/50"
                    :class="[
                      editMode ? 'cursor-pointer select-none' : '',
                      { 'bg-primary-50 dark:bg-primary-900/20': editMode && selected.includes(deck.deckId) },
                    ]"
                  >
                    <Checkbox v-if="editMode" :model-value="selected.includes(deck.deckId)" :binary="true" @update:model-value="toggleSelected(deck.deckId)" />
                    <NuxtLink v-if="!editMode" :to="`/decks/media/${deck.deckId}/detail`" class="flex-none" tabindex="-1" aria-hidden="true">
                      <img :src="coverUrl(deck.coverName, 'xs')" alt="" class="h-14 w-10 rounded-xs object-cover" loading="lazy" />
                    </NuxtLink>
                    <img v-else :src="coverUrl(deck.coverName, 'xs')" alt="" class="h-12 w-8 flex-none rounded-xs object-cover" loading="lazy" />
                    <div class="min-w-0 flex-1">
                      <NuxtLink
                        v-if="!editMode"
                        :to="`/decks/media/${deck.deckId}/detail`"
                        class="block truncate font-medium !text-surface-900 !no-underline hover:!text-primary-600 dark:!text-surface-0 dark:hover:!text-primary-300"
                        v-bind="japaneseTextAttrs(localiseTitle(deck))"
                        >{{ localiseTitle(deck) }}</NuxtLink
                      >
                      <div v-else class="truncate font-medium" v-bind="japaneseTextAttrs(localiseTitle(deck))">{{ localiseTitle(deck) }}</div>
                      <div class="flex flex-wrap gap-x-3 text-xs text-surface-500 dark:text-surface-400">
                        <span v-if="activeGroup.status === null && !editMode && deck.status" class="font-medium text-surface-600 dark:text-surface-300">{{
                          getDeckStatusText(deck.status)
                        }}</span>
                        <span>{{ getMediaTypeText(deck.mediaType) }}</span>
                        <span v-if="isInProgressStatus(deck.status) && deck.listEntry?.startedOn" class="whitespace-nowrap"
                          >Started {{ formatReadDate(deck.listEntry.startedOn) }}</span
                        >
                        <span v-else-if="deck.status === DeckStatus.Completed && deck.listEntry?.finishedOn" class="whitespace-nowrap"
                          >Finished {{ formatReadDate(deck.listEntry.finishedOn) }}</span
                        >
                      </div>
                      <ProfileViewerCoverage v-if="showViewerCoverage" :deck="deck" class="mt-1 max-w-64" />
                    </div>
                    <div class="flex items-center gap-2 empty:hidden max-sm:order-last max-sm:basis-full max-sm:pl-12 sm:shrink-0 sm:gap-3">
                      <span v-if="canLogProgress(deck)" class="shrink-0" @click.prevent.stop>
                        <MediaListProgressButton :deck="deck" />
                      </span>
                      <div v-else-if="canPlan(deck)" class="flex w-28 shrink-0 justify-end">
                        <span v-if="deck.viewerStatus !== DeckStatus.None" class="flex flex-col items-end text-xs leading-tight">
                          <span class="text-muted-color">Your status</span>
                          <span class="font-semibold" :class="getDeckStatusColourClass(deck.viewerStatus!)">{{ getDeckStatusText(deck.viewerStatus!) }}</span>
                        </span>
                        <Button
                          v-else
                          icon="pi pi-plus"
                          label="Planning"
                          size="small"
                          outlined
                          class="w-full"
                          :loading="planning.has(deck.deckId)"
                          :aria-label="`Add ${localiseTitle(deck)} to your Planning list`"
                          @click.prevent.stop="addToPlanning(deck)"
                        />
                      </div>
                      <DeckScoreChip
                        v-if="deck.score != null"
                        :score="deck.score"
                        :editable="canEditRow"
                        class="bg-surface-100 text-xs dark:bg-surface-800"
                        hover-class="hover:bg-surface-200 dark:hover:bg-surface-700"
                        @edit="openRowHistory(deck, 'score')"
                      />
                      <Tooltip v-if="deck.note" :content="deck.note">
                        <button
                          v-if="canEditRow"
                          type="button"
                          class="shrink-0 rounded p-1 cursor-pointer hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors"
                          :aria-label="`Note: ${deck.note}`"
                          @click.prevent.stop="openRowHistory(deck, 'note')"
                        >
                          <i class="pi pi-file-edit text-sm text-surface-500" aria-hidden="true" />
                        </button>
                        <i v-else class="pi pi-file-edit text-sm text-surface-500" :aria-label="`Note: ${deck.note}`" />
                      </Tooltip>
                      <Tooltip v-if="deck.isPrivate" content="Hidden from your profile">
                        <i class="pi pi-eye-slash text-sm text-surface-500" aria-label="Hidden from your profile" />
                      </Tooltip>
                      <Tooltip v-if="deck.isPinned" content="Pinned to your profile">
                        <i class="pi pi-thumbtack text-sm text-primary-600 dark:text-primary-400" aria-label="Pinned to your profile" />
                      </Tooltip>
                      <i v-if="deck.isFavourite" class="pi pi-star-fill text-sm text-amber-400" aria-hidden="true" />
                      <Tag
                        v-if="editMode && deck.status != null && deck.status !== DeckStatus.None"
                        :value="getDeckStatusText(deck.status)"
                        :severity="statusSeverity(deck.status)"
                        class="shrink-0 !text-xs"
                      />
                    </div>
                    <div v-if="isOwnProfile" class="relative shrink-0" @click.prevent.stop @keydown.escape="closeRowMenu">
                      <Button
                        icon="pi pi-ellipsis-v"
                        text
                        rounded
                        size="small"
                        severity="secondary"
                        class="!w-8 !h-8"
                        :aria-label="`Actions for ${localiseTitle(deck)}`"
                        aria-haspopup="menu"
                        :aria-expanded="rowMenuOpenFor === deck.deckId"
                        @click="toggleRowMenu(deck.deckId, $event)"
                      />

                      <Transition name="fade">
                        <div
                          v-if="rowMenuOpenFor === deck.deckId"
                          :ref="(el) => (rowMenu = el as HTMLElement | null)"
                          class="absolute right-0 top-full mt-1 z-30 min-w-52 w-max max-w-[calc(100vw-1rem)] rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-lg py-1"
                        >
                          <div :class="{ 'p-1': rowFlow.step.value !== 'list' && rowFlow.step.value !== 'progress' }">
                            <StatusFlowSteps :deck="deck" :flow="rowFlow" back-label="Back to actions" @open-history="openRowHistory(deck)">
                              <button
                                v-for="action in getRowActions(deck)"
                                :key="action.label"
                                class="w-full flex items-center gap-2 px-3 py-2 text-sm whitespace-nowrap hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors text-left cursor-pointer disabled:opacity-50 disabled:cursor-default"
                                :class="action.severity === 'danger' ? 'text-red-600 dark:text-red-400' : ''"
                                :disabled="action.isStatus && rowFlow.busy.value"
                                @click="
                                  action.action();
                                  if (!action.keepOpen) closeRowMenu();
                                "
                              >
                                <i :class="action.icon" class="text-xs" />
                                {{ action.label }}
                              </button>
                            </StatusFlowSteps>
                          </div>
                        </div>
                      </Transition>
                    </div>
                  </component>
                </div>
              </template>

              <template v-else-if="activeGroup">
                <div v-if="displayStyle === DisplayStyle.Card" class="flex flex-col gap-2">
                  <LazyHydrateMediaDeckCard
                    v-for="(deck, index) in activeGroup.decks"
                    :key="deck.deckId"
                    :deck="deck"
                    :lazy-cover="index >= 3"
                    :class="index >= 3 ? '[content-visibility:auto] [contain-intrinsic-size:auto_30rem] p-1 -m-1' : ''"
                    :read-only-list="!isOwnProfile"
                    show-score
                    @update:deck="updateDeckInList"
                    @status-menu="holdInTab(deck.deckId, $event)"
                  >
                    <template #status-actions="{ close, openScore, rowClass }">
                      <DeckProfileActions :deck="deck" :row-class="rowClass" @open-score="openScore" @close="close" />
                    </template>
                  </LazyHydrateMediaDeckCard>
                </div>

                <div v-else-if="displayStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center">
                  <LazyHydrateMediaDeckCompactView v-for="(deck, index) in activeGroup.decks" :key="deck.deckId" :deck="deck" :lazy-cover="index >= 12" />
                </div>

                <div v-else-if="displayStyle === DisplayStyle.Table" class="flex flex-col gap-0.5">
                  <LazyHydrateMediaDeckTableView
                    v-for="(deck, index) in activeGroup.decks"
                    :key="deck.deckId"
                    :deck="deck"
                    :lazy-render="index >= 12"
                    :log-progress="isOwnProfile"
                  />
                </div>
              </template>
            </div>
          </div>
        </template>
      </template>

      <Transition name="slide-up">
        <div
          v-if="editMode && selected.length > 0"
          class="fixed bottom-0 left-0 right-0 z-40 bg-surface-0 dark:bg-surface-900 border-t border-surface-200 dark:border-surface-700 shadow-[0_-4px_12px_rgba(0,0,0,0.1)] px-4 py-3"
        >
          <div class="container mx-auto flex items-center justify-between gap-3">
            <div class="flex items-center gap-2">
              <span class="text-sm font-medium">{{ selected.length }} selected</span>
              <Button label="Clear" text size="small" severity="secondary" @click="selected = []" />
            </div>
            <div class="flex gap-2 flex-wrap justify-end">
              <Button
                v-for="action in bulkBarActions"
                :key="action.label"
                :icon="action.icon"
                :label="action.label"
                :aria-label="action.label"
                size="small"
                :severity="action.severity"
                :loading="bulkBusy"
                :pt="{ label: { class: 'max-sm:hidden' } }"
                @click="action.run($event)"
              />
            </div>
          </div>
        </div>
      </Transition>
      <Menu ref="statusMenu" :model="statusMenuItems" popup />
      <Menu ref="visibilityMenu" :model="visibilityMenuItems" popup />
      <CommunityListAddDialog v-if="isOwnProfile" v-model:visible="listPickerVisible" :deck-ids="listPickerDeckIds" :title="listPickerTitle" />

      <MediaDeckDownloadDialog
        v-if="downloadMediaList"
        :key="downloadMediaList.apiBase"
        :media-list="downloadMediaList"
        :visible="downloadVisible"
        @update:visible="downloadVisible = $event"
      />
    </div>
  </ProfileTabGate>
</template>

<style scoped>
  .fade-enter-active,
  .fade-leave-active {
    transition: opacity 0.15s;
  }
  .fade-enter-from,
  .fade-leave-to {
    opacity: 0;
  }

  .slide-up-enter-active {
    transition:
      transform 0.2s ease-out,
      opacity 0.2s ease-out;
  }
  .slide-up-leave-active {
    transition:
      transform 0.15s ease-in,
      opacity 0.15s ease-in;
  }
  .slide-up-enter-from,
  .slide-up-leave-to {
    transform: translateY(100%);
    opacity: 0;
  }
</style>
