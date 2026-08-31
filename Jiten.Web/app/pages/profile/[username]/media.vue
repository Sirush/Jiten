<script setup lang="ts">
  import { type Deck, DisplayStyle, DeckStatus, MediaType, DeckDownloadType, DeckOrder, SortOrder } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useDisplayStyleStore } from '~/stores/displayStyleStore';
  import { storeToRefs } from 'pinia';
  import { getDeckStatusText } from '~/utils/deckStatusMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { type DeckSortOption, deckSortMeta, deckSortOption, deckSortOrdering, sortDecks } from '~/utils/deckSorting';
  import { LazyHydrateMediaDeckCard, LazyHydrateMediaDeckCompactView, LazyHydrateMediaDeckTableView } from '~/utils/lazyHydratedComponents';
  import { coverUrl } from '~/utils/coverImage';
  import { useConfirm } from 'primevue/useconfirm';

  const route = useRoute();
  const router = useRouter();
  const { $api } = useNuxtApp();
  const auth = useAuthStore();
  const localiseTitle = useLocaliseTitle();
  const displayStore = useDisplayStyleStore();
  const { displayStyle } = storeToRefs(displayStore);

  const targetUsername = computed(() => route.params.username as string);
  const isOwnProfile = computed(() => auth.isAuthenticated && auth.user?.userName?.toLowerCase() === targetUsername.value.toLowerCase());

  const { data: decks, status, error } = await useApiFetch<Deck[]>(() => `user/profile/${targetUsername.value}/media-list`, { watch: [targetUsername] });

  const isLoading = computed(() => status.value === 'pending');
  const notAvailable = computed(() => (error.value as any)?.statusCode === 404);

  // Status groups in reading order; only non-empty ones get a tab.
  const statusOrder: DeckStatus[] = [DeckStatus.Ongoing, DeckStatus.Completed, DeckStatus.Planning, DeckStatus.Dropped];

  // Media-type filter and sorting are client-side; the full list is already loaded.
  const mediaTypeFilter = ref<MediaType | null>(parseTypeQuery(route.query.type));
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

  const mediaTypeOptions = computed(() => {
    const present = [...presentTypes.value].sort((a, b) => a - b);
    return [{ label: 'All types', value: null as MediaType | null }, ...present.map((t) => ({ label: getMediaTypeText(t), value: t as MediaType | null }))];
  });

  const audioVisualTypes = [MediaType.Anime, MediaType.Drama, MediaType.Movie, MediaType.Audio, MediaType.YouTube];
  const sentenceLengthTypes = [MediaType.Novel, MediaType.NonFiction, MediaType.VideoGame, MediaType.VisualNovel, MediaType.WebNovel];

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
    // Coverage is always the viewer's own, even when browsing someone else's list.
    if (auth.isAuthenticated) general.push('uCoverage', 'coverage', 'uTotalCoverage', 'totalCoverage');
    if (types.some((t) => sentenceLengthTypes.includes(t))) general.push('sentenceLength');
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

  // Runs before the query-sync watchers exist, so a stale key falls back without a redirect on load.
  if (!sortGroups.value.some((g) => g.items.some((i) => i.value === sortBy.value))) {
    sortBy.value = 'title';
    sortOrder.value = deckSortMeta.title!.default;
  }

  const sortOrderLabel = computed(() => {
    const meta = deckSortMeta[sortBy.value];
    if (!meta) return sortOrder.value === SortOrder.Ascending ? 'Ascending' : 'Descending';
    return sortOrder.value === SortOrder.Ascending ? meta.asc : meta.desc;
  });

  watch(sortGroups, (groups) => {
    const available = new Set(groups.flatMap((g) => g.items.map((i) => i.value)));
    if (!available.has(sortBy.value)) sortBy.value = 'title';
  });

  watch(sortBy, (value) => {
    sortOrder.value = deckSortMeta[value]?.default ?? SortOrder.Ascending;
  });

  watch([sortBy, sortOrder, mediaTypeFilter], () => {
    router.replace({
      query: {
        ...route.query,
        sortBy: sortBy.value === 'title' ? undefined : sortBy.value,
        sortOrder: sortBy.value === 'title' && sortOrder.value === deckSortMeta.title!.default ? undefined : sortOrder.value,
        type: mediaTypeFilter.value ?? undefined,
      },
    });
  });

  const filteredDecks = computed(() => {
    const list = decks.value ?? [];
    return mediaTypeFilter.value === null ? list : list.filter((d) => d.mediaType === mediaTypeFilter.value);
  });

  const sortedDecks = computed(() => sortDecks(filteredDecks.value, sortBy.value, sortOrder.value));

  const groups = computed(() =>
    statusOrder
      .map((s) => ({ status: s, label: getDeckStatusText(s), decks: sortedDecks.value.filter((d) => d.status === s) }))
      .filter((g) => g.decks.length > 0)
  );

  const selectedTabRaw = ref<string>('');
  const activeGroup = computed(() => {
    const g = groups.value;
    if (!g.length) return undefined;
    return g.find((x) => x.status.toString() === selectedTabRaw.value) ?? g[0];
  });
  const selectedTab = computed({
    get: () => activeGroup.value?.status.toString() ?? '',
    set: (v: string) => {
      selectedTabRaw.value = v;
    },
  });

  // Keep the local list in sync when a card mutates its own status/favourite, so it re-buckets across tabs.
  function updateDeckInList(updated: Deck) {
    if (!decks.value) return;
    const i = decks.value.findIndex((d) => d.deckId === updated.deckId);
    if (i !== -1) decks.value[i] = updated;
  }

  // ---- Bulk edit mode (own profile only) ----
  const toast = useToast();
  const confirm = useConfirm();

  const editMode = ref(false);
  const selected = ref<number[]>([]);
  const bulkBusy = ref(false);
  const statusMenu = ref();
  const exportMenu = ref();
  const exporting = ref(false);

  watch([() => activeGroup.value?.status, editMode], () => {
    selected.value = [];
  });

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

  const statusMenuItems = computed(() => [
    ...[DeckStatus.Planning, DeckStatus.Ongoing, DeckStatus.Completed, DeckStatus.Dropped].map((s) => ({
      label: getDeckStatusText(s),
      command: () => bulkSetStatus(s),
    })),
    { separator: true },
    { label: 'Clear status', command: () => bulkSetStatus(DeckStatus.None) },
  ]);

  async function runBulk(deckIds: number[], body: Record<string, unknown>): Promise<{ affected: number; skipped: number } | null> {
    bulkBusy.value = true;
    try {
      return await $api<{ affected: number; skipped: number }>('user/deck-preferences/bulk', {
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

  async function bulkSetStatus(status: DeckStatus) {
    const ids = new Set(selected.value);
    const result = await runBulk(selected.value, { status });
    if (!result || !decks.value) return;

    decks.value =
      status === DeckStatus.None ? decks.value.filter((d) => !ids.has(d.deckId)) : decks.value.map((d) => (ids.has(d.deckId) ? { ...d, status } : d));

    selected.value = [];
    toast.add({ severity: 'success', summary: 'Status updated', detail: `${result.affected} titles updated.`, life: 4000 });
  }

  async function bulkFavourite(isFavourite: boolean) {
    const ids = new Set(selected.value);
    const result = await runBulk(selected.value, { isFavourite });
    if (!result || !decks.value) return;

    decks.value = decks.value.map((d) => (ids.has(d.deckId) ? { ...d, isFavourite } : d));
    selected.value = [];
    toast.add({
      severity: 'success',
      summary: isFavourite ? 'Favourited' : 'Unfavourited',
      detail: `${result.affected} titles updated.`,
      life: 4000,
    });
  }

  function bulkRemove() {
    confirm.require({
      message: `Remove ${selected.value.length} titles from your list? Their status will be cleared.`,
      header: 'Remove from list',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: async () => {
        const ids = new Set(selected.value);
        const result = await runBulk(selected.value, { remove: true });
        if (!result || !decks.value) return;

        decks.value = decks.value.filter((d) => !ids.has(d.deckId));
        selected.value = [];
        toast.add({ severity: 'success', summary: 'Removed', detail: `${result.affected} titles removed from your list.`, life: 4000 });
      },
    });
  }

  const exportMenuItems = [
    { label: 'CSV', icon: 'pi pi-file-export', command: () => exportList('csv') },
    { label: 'JSON', icon: 'pi pi-file-export', command: () => exportList('json') },
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

  // ---- Per-row actions menu ----
  const rowMenuOpenFor = ref<number | null>(null);

  function toggleRowMenu(deckId: number) {
    rowMenuOpenFor.value = rowMenuOpenFor.value === deckId ? null : deckId;
  }

  function closeRowMenu() {
    rowMenuOpenFor.value = null;
  }

  watch([() => activeGroup.value?.status, editMode], closeRowMenu);

  const statusActionMeta: { status: DeckStatus; icon: string }[] = [
    { status: DeckStatus.Planning, icon: 'pi pi-bookmark' },
    { status: DeckStatus.Ongoing, icon: 'pi pi-play' },
    { status: DeckStatus.Completed, icon: 'pi pi-check-circle' },
    { status: DeckStatus.Dropped, icon: 'pi pi-times-circle' },
  ];

  function getRowActions(deck: Deck) {
    const actions: { label: string; icon: string; action: () => void; severity?: string }[] = statusActionMeta
      .filter((m) => m.status !== deck.status)
      .map((m) => ({ label: `Move to ${getDeckStatusText(m.status)}`, icon: m.icon, action: () => setDeckStatus(deck, m.status) }));

    actions.push({
      label: deck.isFavourite ? 'Unfavourite' : 'Favourite',
      icon: deck.isFavourite ? 'pi pi-star-fill' : 'pi pi-star',
      action: () => toggleDeckFavourite(deck),
    });
    actions.push({ label: 'Remove from list', icon: 'pi pi-trash', severity: 'danger', action: () => confirmRemoveDeck(deck) });

    return actions;
  }

  async function setDeckStatus(deck: Deck, status: DeckStatus) {
    const result = await runBulk([deck.deckId], { status });
    if (!result || !decks.value) return;

    decks.value = decks.value.map((d) => (d.deckId === deck.deckId ? { ...d, status } : d));
    selected.value = selected.value.filter((id) => id !== deck.deckId);
  }

  async function toggleDeckFavourite(deck: Deck) {
    const isFavourite = !deck.isFavourite;
    const result = await runBulk([deck.deckId], { isFavourite });
    if (!result || !decks.value) return;

    decks.value = decks.value.map((d) => (d.deckId === deck.deckId ? { ...d, isFavourite } : d));
  }

  function confirmRemoveDeck(deck: Deck) {
    confirm.require({
      message: `Remove "${localiseTitle(deck)}" from your list?`,
      header: 'Remove from list',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Remove',
      rejectLabel: 'Cancel',
      acceptProps: { severity: 'danger' },
      accept: async () => {
        const result = await runBulk([deck.deckId], { remove: true });
        if (!result || !decks.value) return;

        decks.value = decks.value.filter((d) => d.deckId !== deck.deckId);
        selected.value = selected.value.filter((id) => id !== deck.deckId);
      },
    });
  }

  const editStatusSeverity: Record<number, string> = {
    [DeckStatus.Planning]: 'secondary',
    [DeckStatus.Ongoing]: 'warn',
    [DeckStatus.Completed]: 'success',
    [DeckStatus.Dropped]: 'danger',
  };

  const bulkBarActions = computed((): { label: string; icon: string; severity: string; run: (e: Event) => void }[] => [
    { label: 'Set status', icon: 'pi pi-tag', severity: 'secondary', run: (e: Event) => statusMenu.value?.toggle(e) },
    { label: 'Favourite', icon: 'pi pi-star', severity: 'secondary', run: () => bulkFavourite(true) },
    { label: 'Unfavourite', icon: 'pi pi-star-fill', severity: 'secondary', run: () => bulkFavourite(false) },
    { label: 'Remove', icon: 'pi pi-trash', severity: 'danger', run: () => bulkRemove() },
  ]);

  const sentenceMediaTypes = [MediaType.Novel, MediaType.NonFiction, MediaType.VideoGame, MediaType.VisualNovel, MediaType.WebNovel];
  const downloadVisible = ref(false);
  const downloadMediaList = ref<{ apiBase: string; title: string; totalWords: number; hasExampleSentences: boolean } | null>(null);

  const openDownload = () => {
    const g = activeGroup.value;
    if (!g) return;
    const apiBase = `user/profile/${targetUsername.value}/media-list/${g.status}`;

    downloadMediaList.value = {
      apiBase,
      title: `${targetUsername.value} - ${g.label}`,
      totalWords: g.decks.reduce((sum, d) => sum + (d.uniqueWordCount || 0), 0),
      hasExampleSentences: g.decks.some((d) => sentenceMediaTypes.includes(d.mediaType)),
    };
    downloadVisible.value = true;

    $api<number>(`${apiBase}/vocabulary-count`, {
      method: 'POST',
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
    title: `${targetUsername.value} - Media List`,
    meta: [{ name: 'description', content: `Tracked media list for ${targetUsername.value}` }],
  }));
</script>

<template>
  <div class="container mx-auto px-4 py-6 flex flex-col gap-4" :class="{ 'pb-24': editMode }" @click="closeRowMenu">
    <div v-if="isLoading" class="flex justify-center items-center min-h-[50vh]">
      <ProgressSpinner />
    </div>

    <div v-else-if="notAvailable" class="text-center py-16">
      <Card>
        <template #content>
          <div class="flex flex-col items-center gap-4">
            <Icon name="material-symbols:lock" size="4rem" class="text-surface-400" />
            <h2 class="text-xl font-semibold">This media list is private</h2>
            <p class="text-surface-500 dark:text-surface-400">This user has chosen to keep their media list private.</p>
            <NuxtLink :to="`/profile/${targetUsername}`">
              <Button label="Back to Profile" icon="pi pi-arrow-left" />
            </NuxtLink>
          </div>
        </template>
      </Card>
    </div>

    <div v-else-if="error" class="text-center py-8">
      <Message severity="error">Failed to load media list.</Message>
    </div>

    <template v-else>
      <div class="flex items-center gap-2">
        <NuxtLink :to="`/profile/${targetUsername}`" class="flex items-center gap-1 text-primary hover:underline">
          <Icon name="material-symbols:arrow-back" />
          Back to Profile
        </NuxtLink>
      </div>

      <h1 class="text-2xl md:text-3xl font-bold">{{ isOwnProfile ? 'My Media List' : `${targetUsername}'s Media List` }}</h1>

      <div v-if="groups.length === 0" class="text-center py-12">
        <Message severity="info">No tracked media yet. Set a status on a title to see it here.</Message>
      </div>

      <template v-else>
        <Tabs v-model:value="selectedTab" :show-navigators="false">
          <TabList class="flex-wrap">
            <Tab v-for="g in groups" :key="g.status" :value="g.status.toString()">
              {{ g.label }}
              <span class="text-xs text-surface-400 ml-1">{{ g.decks.length }}</span>
            </Tab>
          </TabList>
        </Tabs>

        <!-- Toolbar -->
        <div class="flex justify-between items-center gap-3 flex-wrap">
          <div class="flex flex-wrap items-center gap-2">
            <Button v-if="activeGroup" :label="`Download ${activeGroup.label} vocab`" icon="pi pi-download" size="small" @click="openDownload" />
            <Button
              v-if="isOwnProfile"
              :label="editMode ? 'Done editing' : 'Edit list'"
              :icon="editMode ? 'pi pi-check' : 'pi pi-pencil'"
              size="small"
              :severity="editMode ? undefined : 'secondary'"
              @click="editMode = !editMode"
            />
            <Button
              v-if="isOwnProfile"
              label="Export full list"
              icon="pi pi-file-export"
              size="small"
              severity="secondary"
              :disabled="exporting"
              @click="exportMenu?.toggle($event)"
            />
            <Menu ref="exportMenu" :model="exportMenuItems" popup />
          </div>
          <div class="w-full flex flex-wrap items-center gap-2 sm:w-auto sm:ml-auto sm:justify-end">
            <div class="w-full flex items-center gap-2 sm:w-auto">
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
                class="flex-1 sm:flex-none sm:w-44"
                scroll-height="50vh"
              >
                <template #optiongroup="{ option }">
                  <div class="text-xs font-semibold text-surface-500 dark:text-surface-400 py-0.5 px-1">{{ option.label }}</div>
                </template>
              </Select>
              <Button
                v-tooltip.top="sortOrderLabel"
                :icon="sortOrder === SortOrder.Ascending ? 'pi pi-arrow-up' : 'pi pi-arrow-down'"
                :aria-label="`Sort order: ${sortOrderLabel}`"
                size="small"
                severity="secondary"
                @click="sortOrder = sortOrder === SortOrder.Ascending ? SortOrder.Descending : SortOrder.Ascending"
              />
            </div>
            <div v-if="mediaTypeOptions.length > 2 || !editMode" class="w-full flex items-center gap-2 sm:w-auto">
              <Select
                v-if="mediaTypeOptions.length > 2"
                v-model="mediaTypeFilter"
                :options="mediaTypeOptions"
                option-label="label"
                option-value="value"
                placeholder="All types"
                aria-label="Filter by media type"
                size="small"
                class="flex-1 sm:flex-none sm:w-40"
              />
              <div v-if="!editMode" class="ml-auto flex items-center sm:ml-0">
                <DisplayStyleSelector />
              </div>
            </div>
          </div>
        </div>

        <!-- Bulk edit mode -->
        <template v-if="editMode && activeGroup">
          <div class="flex items-center gap-3 px-3 py-2 -mb-2 text-sm text-surface-500 dark:text-surface-400">
            <Checkbox :model-value="allSelected" :binary="true" @change="toggleSelectAll" />
            <span class="text-xs cursor-pointer select-none" @click="toggleSelectAll">
              {{ selected.length > 0 ? `${selected.length} selected` : `Select all (${activeGroup.decks.length})` }}
            </span>
          </div>

          <div
            class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm divide-y divide-surface-100 dark:divide-surface-800"
          >
            <label
              v-for="deck in activeGroup.decks"
              :key="deck.deckId"
              class="flex cursor-pointer select-none items-center gap-2 sm:gap-3 px-3 py-2 sm:py-2.5 transition-colors first:rounded-t-xl last:rounded-b-xl hover:bg-surface-50 dark:hover:bg-surface-800/50"
              :class="{ 'bg-primary-50 dark:bg-primary-900/20': selected.includes(deck.deckId) }"
            >
              <Checkbox :model-value="selected.includes(deck.deckId)" :binary="true" @update:model-value="toggleSelected(deck.deckId)" />
              <img :src="coverUrl(deck.coverName)" alt="" class="h-12 w-8 flex-none rounded-xs object-cover" loading="lazy" />
              <div class="min-w-0 flex-1">
                <div class="truncate font-medium">{{ localiseTitle(deck) }}</div>
                <div class="text-xs text-surface-500 dark:text-surface-400">{{ getMediaTypeText(deck.mediaType) }}</div>
              </div>
              <i v-if="deck.isFavourite" class="pi pi-star-fill text-sm text-amber-400" aria-hidden="true" />
              <Tag
                v-if="deck.status != null && deck.status !== DeckStatus.None"
                :value="getDeckStatusText(deck.status)"
                :severity="editStatusSeverity[deck.status]"
                class="shrink-0 !text-xs"
              />
              <div class="relative shrink-0" @click.prevent.stop>
                <Button icon="pi pi-ellipsis-v" text rounded size="small" severity="secondary" class="!w-8 !h-8" @click="toggleRowMenu(deck.deckId)" />

                <Transition name="fade">
                  <div
                    v-if="rowMenuOpenFor === deck.deckId"
                    class="absolute right-0 top-full mt-1 z-30 min-w-52 w-max max-w-[calc(100vw-1rem)] rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-lg py-1"
                  >
                    <button
                      v-for="action in getRowActions(deck)"
                      :key="action.label"
                      class="w-full flex items-center gap-2 px-3 py-2 text-sm whitespace-nowrap hover:bg-surface-100 dark:hover:bg-surface-800 transition-colors text-left cursor-pointer"
                      :class="action.severity === 'danger' ? 'text-red-600 dark:text-red-400' : ''"
                      @click="
                        action.action();
                        closeRowMenu();
                      "
                    >
                      <i :class="action.icon" class="text-xs" />
                      {{ action.label }}
                    </button>
                  </div>
                </Transition>
              </div>
            </label>
          </div>
        </template>

        <template v-else-if="activeGroup">
          <!-- Card view -->
          <div v-if="displayStyle === DisplayStyle.Card" class="flex flex-col gap-2">
            <LazyHydrateMediaDeckCard
              v-for="(deck, index) in activeGroup.decks"
              :key="deck.deckId"
              :deck="deck"
              :lazy-cover="index >= 3"
              :class="index >= 3 ? '[content-visibility:auto] [contain-intrinsic-size:auto_30rem] p-1 -m-1' : ''"
              @update:deck="updateDeckInList"
            />
          </div>

          <!-- Compact view -->
          <div v-else-if="displayStyle === DisplayStyle.Compact" class="flex flex-wrap gap-4 justify-center">
            <LazyHydrateMediaDeckCompactView v-for="(deck, index) in activeGroup.decks" :key="deck.deckId" :deck="deck" :lazy-cover="index >= 12" />
          </div>

          <!-- Table view -->
          <div v-else-if="displayStyle === DisplayStyle.Table" class="flex flex-col gap-0.5">
            <LazyHydrateMediaDeckTableView v-for="(deck, index) in activeGroup.decks" :key="deck.deckId" :deck="deck" :lazy-render="index >= 12" />
          </div>
        </template>
      </template>
    </template>

    <!-- Bulk action bar -->
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
            <template v-for="action in bulkBarActions" :key="action.label">
              <Button
                :icon="action.icon"
                :label="action.label"
                size="small"
                :severity="action.severity"
                :loading="bulkBusy"
                class="!hidden sm:!inline-flex"
                @click="action.run($event)"
              />
              <Button :icon="action.icon" size="small" :severity="action.severity" :loading="bulkBusy" class="sm:!hidden" @click="action.run($event)" />
            </template>
          </div>
        </div>
      </div>
    </Transition>
    <Menu ref="statusMenu" :model="statusMenuItems" popup />

    <MediaDeckDownloadDialog
      v-if="downloadMediaList"
      :key="downloadMediaList.apiBase"
      :media-list="downloadMediaList"
      :visible="downloadVisible"
      @update:visible="downloadVisible = $event"
    />
  </div>
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
