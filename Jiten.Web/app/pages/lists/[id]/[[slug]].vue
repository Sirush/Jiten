<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { CommunityListStatus, DeckStatus } from '~/types';
  import type { CommunityListDetail, CommunityListItem } from '~/types/types';
  import {
    COMMUNITY_LIST_FORBIDDEN_CONTENT,
    COMMUNITY_LIST_ITEM_PAGE_SIZE,
    COMMUNITY_LIST_MAX_REPORT,
    type CommunityListItemSort,
    communityListItemAnchor,
    communityListPath,
    unlistConfirmation,
  } from '~/composables/useCommunityLists';
  import { applyMediaListChange, onMediaListChange, useMediaListApi } from '~/composables/useMediaListSync';
  import { createCookieState } from '~/stores/jitenStore';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { DECK_STATUS_DISPLAY_ORDER as STATUS_ORDER, getDeckStatusColourClass, getDeckStatusText } from '~/utils/deckStatusMapper';
  import { getGenreText } from '~/utils/genreMapper';
  import { isMissingResource } from '~/utils/missingResource';
  import { formatDateLong } from '~/utils/formatDateLong';
  import { formatRelativeTime } from '~/utils/relativeTime';

  definePageMeta({
    validate: (route) => /^\d+$/.test(String(route.params.id)),
  });

  const VIEW_DWELL_MS = 10_000;

  const route = useRoute();
  const router = useRouter();
  const toast = useToast();
  const confirm = useConfirm();
  const authStore = useAuthStore();
  const jitenStore = useJitenStore();
  const { $api } = useNuxtApp();
  const lists = useCommunityLists();
  const { postDeckStatus } = useMediaListApi();
  const localiseTitle = useLocaliseTitle();

  const listId = Number(route.params.id);
  const itemSort = ref<CommunityListItemSort>(route.query.sort === 'coverage' ? 'coverage' : 'curator');
  const {
    data: list,
    error,
    ready,
  } = useApiFetch<CommunityListDetail>(`lists/${listId}`, {
    query: { sort: itemSort.value === 'coverage' ? 'coverage' : undefined },
  });
  await ready;

  if (isMissingResource(error.value, list.value)) throw createError({ statusCode: 404, statusMessage: 'List not found', fatal: true });

  const displayMode = createCookieState<'cards' | 'covers' | 'rows'>('community-list-display', 'cards');
  const DISPLAY_OPTIONS = [
    { value: 'cards', icon: 'material-symbols:view-agenda-outline', label: 'Card view' },
    { value: 'covers', icon: 'material-symbols:grid-view', label: 'Compact view' },
    { value: 'rows', icon: 'material-symbols:table-rows', label: 'List view' },
  ];

  const items = ref<CommunityListItem[]>(list.value?.items.data ?? []);
  const totalItems = ref(list.value?.items.totalItems ?? 0);
  const offset = ref(0);
  const itemsLoading = ref(false);
  const planning = ref(new Set<number>());

  const showViewerData = computed(() => authStore.isAuthenticated && !!list.value?.coverage);
  const isPublic = computed(() => list.value?.status == null || list.value.status === CommunityListStatus.Public);

  async function loadPage(newOffset: number) {
    itemsLoading.value = true;
    try {
      const page = await lists.items(listId, newOffset, itemSort.value);
      items.value = page.data;
      totalItems.value = page.totalItems;
      offset.value = newOffset;
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not load these titles'), life: 5000 });
    } finally {
      itemsLoading.value = false;
    }
  }

  const SORT_OPTIONS: { value: CommunityListItemSort; label: string; shortLabel: string }[] = [
    { value: 'curator', label: 'List order', shortLabel: 'List order' },
    { value: 'coverage', label: 'Highest coverage first', shortLabel: 'Coverage' },
  ];

  function setSort(sort: CommunityListItemSort) {
    if (sort === itemSort.value) return;
    itemSort.value = sort;
    router.replace({ query: { ...route.query, sort: sort === 'coverage' ? 'coverage' : undefined, page: undefined } });
    loadPage(0);
  }

  const coverageValues = computed(() => list.value?.coverage?.values ?? []);
  const hasCoverage = computed(() => coverageValues.value.some((v) => v > 0));
  const wholePercent = (percent: number) => `${Math.round(percent)}%`;

  /** Past this many titles the slices are thinner than their gaps, so the strip becomes one gradient instead of a node per title. */
  const MAX_STRIP_SLICES = 60;
  const stripGradient = computed(() => {
    const values = coverageValues.value;
    if (values.length <= MAX_STRIP_SLICES) return null;
    const stops = values.map((value, i) => {
      const colour = getCoverageColour(value);
      return `${colour} ${((i / values.length) * 100).toFixed(2)}% ${(((i + 1) / values.length) * 100).toFixed(2)}%`;
    });
    return `linear-gradient(to right, ${stops.join(', ')})`;
  });
  const canSortByCoverage = computed(() => showViewerData.value && hasCoverage.value && totalItems.value > 1);

  const currentPage = computed(() => Math.floor(offset.value / COMMUNITY_LIST_ITEM_PAGE_SIZE) + 1);
  const totalPages = computed(() => Math.max(1, Math.ceil(totalItems.value / COMMUNITY_LIST_ITEM_PAGE_SIZE)));

  function onPageChange(event: { first: number }) {
    router.replace({ query: { ...route.query, page: event.first > 0 ? String(event.first / COMMUNITY_LIST_ITEM_PAGE_SIZE + 1) : undefined } });
    loadPage(event.first);
    if (import.meta.client) window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  const viewerStatusCounts = ref(list.value?.statusCounts ?? null);
  const statusCounts = computed(() =>
    (viewerStatusCounts.value ?? []).filter((c) => c.count > 0).sort((a, b) => STATUS_ORDER.indexOf(a.status) - STATUS_ORDER.indexOf(b.status))
  );

  function bumpStatusCount(from: DeckStatus | undefined, to: DeckStatus) {
    const counts = viewerStatusCounts.value;
    if (!counts) return;
    if (from !== undefined && from !== DeckStatus.None) {
      const previous = counts.find((c) => c.status === from);
      if (previous) previous.count = Math.max(0, previous.count - 1);
    }
    if (to === DeckStatus.None) return;
    const next = counts.find((c) => c.status === to);
    if (next) next.count++;
    else counts.push({ status: to, count: 1 });
  }

  async function addToPlanning(item: CommunityListItem) {
    planning.value.add(item.deck.deckId);
    try {
      await postDeckStatus(item.deck, DeckStatus.Planning);
    } finally {
      planning.value.delete(item.deck.deckId);
    }
  }

  onMediaListChange((change) => {
    const item = items.value.find((i) => i.deck.deckId === change.deckId);
    if (!item) return;
    if (change.status !== undefined) bumpStatusCount(item.deck.status, change.status);
    item.deck = applyMediaListChange(item.deck, change);
  });

  const favouriteBusy = ref(false);
  async function toggleFavourite() {
    if (!list.value) return;
    if (!authStore.isAuthenticated) {
      await navigateTo({ path: '/login', query: { redirect: route.fullPath } });
      return;
    }
    favouriteBusy.value = true;
    try {
      const result = list.value.isFavourited ? await lists.unfavourite(listId) : await lists.favourite(listId);
      list.value.isFavourited = result.favourited;
      list.value.favouriteCount = result.favouriteCount;
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not update your favourites'), life: 5000 });
    } finally {
      favouriteBusy.value = false;
    }
  }

  const reportOpen = ref(false);
  const reportReason = ref('');
  const reportSending = ref(false);
  async function sendReport() {
    const reason = reportReason.value.trim();
    if (!reason) return;
    reportSending.value = true;
    try {
      await lists.report(listId, reason);
      reportOpen.value = false;
      reportReason.value = '';
      toast.add({ severity: 'success', summary: 'Report sent to the moderators.', life: 4000 });
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not send the report'), life: 5000 });
    } finally {
      reportSending.value = false;
    }
  }

  const adminBusy = ref(false);
  async function setModeration(action: 'unlist' | 'restore') {
    adminBusy.value = true;
    try {
      await $api(`admin/community-lists/${listId}/${action}`, { method: 'POST' });
      if (list.value) list.value.status = action === 'unlist' ? CommunityListStatus.Unlisted : CommunityListStatus.Public;
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, 'Could not change the list'), life: 5000 });
    } finally {
      adminBusy.value = false;
    }
  }

  function confirmUnlist() {
    confirm.require(unlistConfirmation(() => setModeration('unlist')));
  }

  function anchoredPosition(hash: string) {
    const match = /^#item-(\d+)$/.exec(hash);
    const position = match ? Number(match[1]) : NaN;
    return position >= 1 && position <= totalItems.value ? position : null;
  }

  /** Item anchors use curator ranks, so a linked item off the loaded page brings up its page in curator order. */
  async function revealAnchoredItem(hash: string) {
    const position = anchoredPosition(hash);
    if (position == null) return;
    if (!items.value.some((i) => i.position === position)) {
      const pageOffset = Math.floor((position - 1) / COMMUNITY_LIST_ITEM_PAGE_SIZE) * COMMUNITY_LIST_ITEM_PAGE_SIZE;
      itemSort.value = 'curator';
      router.replace({
        query: { ...route.query, sort: undefined, page: pageOffset > 0 ? String(pageOffset / COMMUNITY_LIST_ITEM_PAGE_SIZE + 1) : undefined },
        hash,
      });
      await loadPage(pageOffset);
    }
    await nextTick();
    document.getElementById(communityListItemAnchor(position))?.scrollIntoView({ block: 'start' });
  }

  watch(
    () => route.hash,
    (hash) => revealAnchoredItem(hash)
  );

  const canonicalPath = computed(() => (list.value ? communityListPath(list.value) : `/lists/${listId}`));

  let viewTimer: ReturnType<typeof setTimeout> | null = null;
  onBeforeUnmount(() => {
    if (viewTimer) clearTimeout(viewTimer);
  });

  onMounted(() => {
    if (list.value && String(route.params.slug ?? '') !== list.value.slug) router.replace({ path: canonicalPath.value, query: route.query, hash: route.hash });

    const requestedPage = Number(route.query.page);
    if (anchoredPosition(route.hash) != null) revealAnchoredItem(route.hash);
    else if (Number.isInteger(requestedPage) && requestedPage > 1) loadPage((requestedPage - 1) * COMMUNITY_LIST_ITEM_PAGE_SIZE);

    if (list.value && !list.value.isOwner) viewTimer = setTimeout(() => $api(`lists/${listId}/view`, { method: 'POST' }).catch(() => {}), VIEW_DWELL_MS);
  });

  const metaDescription = computed(() => {
    const l = list.value;
    if (!l) return '';
    const base = l.description?.trim() || `${l.itemCount} Japanese titles picked by ${l.ownerDisplayName ?? 'a Jiten user'}.`;
    return base.length <= 160 ? base : base.slice(0, 157).replace(/\s+\S*$/, '') + '…';
  });

  useSeoMeta({
    title: () => list.value?.name ?? 'List',
    description: metaDescription,
    ogTitle: () => `${list.value?.name ?? 'List'}, a list on Jiten`,
    ogDescription: metaDescription,
    robots: () => (isPublic.value ? undefined : 'noindex, nofollow'),
  });

  if (import.meta.server && list.value && isPublic.value) {
    defineOgImage('CommunityListOgImage', {
      name: list.value.name,
      description: list.value.description,
      ownerDisplayName: list.value.ownerDisplayName,
      itemCount: list.value.itemCount,
      coverNames: list.value.previewCoverNames,
    });
  }

  useHead(() => ({
    link: [{ rel: 'canonical', href: `https://jiten.moe${canonicalPath.value}` }],
    script: list.value
      ? [
          {
            type: 'application/ld+json',
            innerHTML: JSON.stringify({
              '@context': 'https://schema.org',
              '@type': 'ItemList',
              name: list.value.name,
              description: list.value.description ?? undefined,
              numberOfItems: list.value.itemCount,
              itemListElement: items.value.map((item) => ({
                '@type': 'ListItem',
                position: item.position,
                name: localiseTitle(item.deck),
                url: `https://jiten.moe/decks/media/${item.deck.deckId}/detail`,
              })),
            }),
          },
        ]
      : [],
  }));
</script>

<template>
  <div v-if="list" class="container mx-auto flex flex-col gap-4 p-2 md:p-4">
    <nav aria-label="Breadcrumb" class="flex items-center gap-2 text-sm text-muted-color">
      <NuxtLink to="/lists">Lists</NuxtLink>
      <span aria-hidden="true">/</span>
      <span class="truncate">{{ list.name }}</span>
    </nav>

    <Message v-if="list.status === CommunityListStatus.Draft" severity="secondary" :closable="false">
      This list is a draft. Only you can see it until you publish it.
    </Message>
    <Message v-else-if="list.status === CommunityListStatus.Unlisted" severity="warn" :closable="false">
      This list was unlisted by moderation. Only you and the moderators can see it.
    </Message>

    <section class="flex flex-col gap-4 rounded-lg border border-surface-200 bg-surface-0 p-4 md:p-6 dark:border-surface-700 dark:bg-surface-900">
      <div class="flex flex-col gap-1.5">
        <h1 class="m-0 text-2xl leading-tight font-bold md:text-3xl" v-bind="japaneseTextAttrs(list.name)">{{ list.name }}</h1>
        <div class="flex flex-wrap items-center gap-x-4 gap-y-1 text-sm text-muted-color">
          <span v-if="list.ownerDisplayName" class="flex items-center gap-1.5" :aria-label="`By ${list.ownerDisplayName}`">
            <i class="pi pi-user text-xs" aria-hidden="true" />
            <span class="font-semibold text-surface-800 dark:text-surface-100">{{ list.ownerDisplayName }}</span>
          </span>
          <span class="flex items-center gap-1.5">
            <i class="pi pi-book text-xs" aria-hidden="true" />
            {{ list.itemCount.toLocaleString() }} {{ list.itemCount === 1 ? 'title' : 'titles' }}
          </span>
          <ClientOnly>
            <Tooltip :content="`Updated ${formatDateLong(list.updatedAt)}`">
              <span class="flex items-center gap-1.5" :aria-label="`Updated ${formatRelativeTime(list.updatedAt)}`">
                <i class="pi pi-clock text-xs" aria-hidden="true" />
                {{ formatRelativeTime(list.updatedAt) }}
              </span>
            </Tooltip>
          </ClientOnly>
        </div>
      </div>

      <p v-if="list.description" class="m-0 max-w-[68ch] whitespace-pre-line leading-relaxed" v-bind="japaneseTextAttrs(list.description)">
        {{ list.description }}
      </p>

      <div v-if="list.genres.length || list.tags.length" class="flex flex-col gap-2">
        <div v-if="list.genres.length" class="flex flex-wrap items-center gap-2">
          <span class="w-14 text-xs font-bold tracking-wider text-muted-color uppercase">Genres</span>
          <NuxtLink
            v-for="genre in list.genres"
            :key="genre"
            :to="{ path: '/lists', query: { genres: String(genre) } }"
            class="rounded-md bg-purple-100 px-2.5 py-0.5 text-sm text-purple-800! no-underline! hover:bg-purple-200 dark:bg-purple-900/50 dark:text-purple-200! dark:hover:bg-purple-900"
          >
            {{ getGenreText(genre) }}
          </NuxtLink>
        </div>
        <div v-if="list.tags.length" class="flex flex-wrap items-center gap-2">
          <span class="w-14 text-xs font-bold tracking-wider text-muted-color uppercase">Tags</span>
          <NuxtLink
            v-for="tag in list.tags"
            :key="tag.tagId"
            :to="{ path: '/lists', query: { tags: String(tag.tagId) } }"
            class="rounded-md bg-surface-100 px-2.5 py-0.5 text-sm text-surface-700! no-underline! hover:bg-surface-200 dark:bg-surface-800 dark:text-surface-200! dark:hover:bg-surface-700"
          >
            {{ tag.name }}
          </NuxtLink>
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <button
          v-if="!list.isOwner && isPublic"
          type="button"
          class="inline-flex h-8 cursor-pointer items-center gap-2 rounded-md px-3 text-sm font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-purple-500 disabled:cursor-wait disabled:opacity-60"
          :class="
            list.isFavourited
              ? 'bg-purple-100 text-purple-800 hover:bg-purple-200 dark:bg-purple-900/50 dark:text-purple-100 dark:hover:bg-purple-900/80'
              : 'bg-surface-100 text-surface-700 hover:bg-purple-50 hover:text-purple-700 dark:bg-surface-800 dark:text-surface-200 dark:hover:bg-purple-900/40 dark:hover:text-purple-200'
          "
          :disabled="favouriteBusy"
          :aria-pressed="list.isFavourited"
          @click="toggleFavourite"
        >
          <i :class="list.isFavourited ? 'pi pi-heart-fill text-purple-600 dark:text-purple-300' : 'pi pi-heart'" aria-hidden="true" />
          <span>{{ list.isFavourited ? 'Favourited' : 'Favourite' }}</span>
          <span
            class="rounded-full px-1.5 text-xs font-bold leading-5 tabular-nums"
            :class="list.isFavourited ? 'bg-purple-200 dark:bg-purple-800' : 'bg-surface-200 dark:bg-surface-700'"
            >{{ list.favouriteCount.toLocaleString() }}</span
          >
        </button>
        <span v-else class="flex items-center gap-1.5 px-1 text-sm text-muted-color">
          <i class="pi pi-heart" aria-hidden="true" />{{ list.favouriteCount.toLocaleString() }}
          {{ list.favouriteCount === 1 ? 'favourite' : 'favourites' }}
        </span>
        <ShareButton v-if="isPublic" :path="canonicalPath" :title="list.name" />
        <CommunityListShareImageButton v-if="isPublic || list.status === CommunityListStatus.Draft" :list="list" />
        <Button v-if="list.canEdit" label="Edit" icon="pi pi-pencil" text size="small" @click="navigateTo(`/lists/${list.listId}/edit`)" />
        <span class="flex-1" />
        <template v-if="authStore.isAdmin && jitenStore.displayAdminFunctions && !list.isOwner">
          <Button
            v-if="list.status !== CommunityListStatus.Unlisted"
            label="Unlist"
            icon="pi pi-eye-slash"
            severity="danger"
            text
            size="small"
            :loading="adminBusy"
            @click="confirmUnlist"
          />
          <Button v-else label="Restore" icon="pi pi-eye" severity="secondary" text size="small" :loading="adminBusy" @click="setModeration('restore')" />
        </template>
        <Button
          v-if="authStore.isAuthenticated && !list.isOwner && isPublic"
          label="Report"
          icon="pi pi-flag"
          severity="secondary"
          text
          size="small"
          @click="reportOpen = true"
        />
      </div>

      <div
        v-if="showViewerData && list.coverage"
        class="grid grid-cols-1 gap-x-8 gap-y-5 border-t border-surface-200 pt-4 md:grid-cols-2 dark:border-surface-700"
      >
        <div class="flex flex-col gap-2">
          <span class="text-sm font-semibold text-surface-700 dark:text-surface-200">Your coverage</span>
          <template v-if="hasCoverage">
            <div
              role="img"
              :aria-label="`Your coverage of each title, from ${wholePercent(coverageValues[0]!)} down to ${wholePercent(coverageValues.at(-1)!)}`"
              class="flex h-2.5 gap-0.5 overflow-hidden rounded-full"
              :style="stripGradient ? { backgroundImage: stripGradient } : undefined"
            >
              <template v-if="!stripGradient">
                <div v-for="(value, i) in coverageValues" :key="i" class="flex-1" :style="{ backgroundColor: getCoverageColour(value) }" />
              </template>
            </div>
            <div class="flex justify-between text-xs text-muted-color tabular-nums" aria-hidden="true">
              <span>{{ wholePercent(coverageValues[0]!) }}</span>
              <span v-if="coverageValues.length > 1">{{ wholePercent(coverageValues.at(-1)!) }}</span>
            </div>
            <span v-if="list.coverage.easiest && coverageValues.length > 1" class="flex flex-wrap items-baseline gap-x-1.5 text-sm">
              <span class="text-muted-color">Easiest for you:</span>
              <NuxtLink :to="`/decks/media/${list.coverage.easiest.deckId}/detail`" v-bind="japaneseTextAttrs(localiseTitle(list.coverage.easiest))">{{
                localiseTitle(list.coverage.easiest)
              }}</NuxtLink>
              <span class="text-muted-color tabular-nums">{{ wholePercent(list.coverage.easiest.coverage) }}</span>
            </span>
          </template>
          <span v-else class="text-sm text-muted-color">Track some words and you'll see how much of each title you can read.</span>
        </div>
        <div class="flex flex-col gap-2">
          <span class="text-sm font-semibold text-surface-700 dark:text-surface-200">On your list</span>
          <div v-if="statusCounts.length" class="flex flex-wrap gap-x-4 gap-y-1">
            <span
              v-for="count in statusCounts"
              :key="count.status"
              class="flex items-center gap-1.5 text-sm font-semibold"
              :class="getDeckStatusColourClass(count.status)"
            >
              <i class="pi pi-flag-fill text-xs" aria-hidden="true" />{{ getDeckStatusText(count.status) }} {{ count.count }}
            </span>
          </div>
          <span v-else class="text-sm text-muted-color">None of these titles are on your list yet.</span>
        </div>
      </div>
    </section>

    <div class="flex flex-wrap items-center gap-x-3 gap-y-2 px-1 text-sm text-muted-color">
      <span v-if="totalItems > 0">
        {{
          totalItems > COMMUNITY_LIST_ITEM_PAGE_SIZE
            ? `Showing ${offset + 1} to ${Math.min(offset + COMMUNITY_LIST_ITEM_PAGE_SIZE, totalItems)} of ${totalItems}`
            : `${totalItems} ${totalItems === 1 ? 'title' : 'titles'}`
        }}
      </span>
      <div class="ml-auto flex shrink-0 items-center gap-2">
        <SelectButton
          v-if="canSortByCoverage"
          :model-value="itemSort"
          :options="SORT_OPTIONS"
          option-label="label"
          option-value="value"
          data-key="value"
          :allow-empty="false"
          aria-label="Order"
          @update:model-value="setSort"
        >
          <template #option="{ option }">
            <span class="sm:hidden">{{ option.shortLabel }}</span
            ><span class="hidden sm:inline">{{ option.label }}</span>
          </template>
        </SelectButton>
        <SelectButton v-model="displayMode" :options="DISPLAY_OPTIONS" option-value="value" data-key="value" :allow-empty="false" aria-label="Display">
          <template #option="{ option }">
            <Tooltip :content="option.label">
              <Icon :name="option.icon" size="1.25em" :aria-label="option.label" />
            </Tooltip>
          </template>
        </SelectButton>
      </div>
    </div>

    <div v-if="totalItems === 0" class="rounded-lg border border-dashed border-surface-300 px-4 py-10 text-center text-muted-color dark:border-surface-600">
      <p class="m-0">This list has no titles yet.</p>
      <Button v-if="list.canEdit" label="Add titles" icon="pi pi-plus" text class="mt-2" @click="navigateTo(`/lists/${list.listId}/edit`)" />
    </div>

    <ol
      v-else-if="displayMode === 'covers'"
      class="m-0 flex list-none flex-wrap justify-center gap-4 p-0 transition-opacity duration-200"
      :class="{ 'opacity-60 delay-200': itemsLoading }"
      :aria-busy="itemsLoading"
    >
      <li v-for="item in items" :id="communityListItemAnchor(item.position)" :key="item.deck.deckId" class="group/item relative scroll-mt-4">
        <MediaDeckCompactView :deck="item.deck" lazy-cover />
        <span
          class="pointer-events-none absolute top-1 left-1 z-20 rounded bg-black/70 px-1.5 text-xs leading-5 font-bold text-white tabular-nums transition-opacity group-hover/item:opacity-0"
          aria-hidden="true"
          >{{ item.position }}</span
        >
      </li>
    </ol>

    <ol
      v-else
      class="m-0 list-none p-0 transition-opacity duration-200"
      :class="[
        displayMode === 'rows'
          ? 'divide-y divide-surface-200 overflow-hidden rounded-lg border border-surface-200 bg-surface-0 dark:divide-surface-700 dark:border-surface-700 dark:bg-surface-900'
          : 'flex flex-col gap-2',
        { 'opacity-60 delay-200': itemsLoading },
      ]"
      :aria-busy="itemsLoading"
    >
      <CommunityListItemCard
        v-for="item in items"
        :key="item.deck.deckId"
        :item="item"
        :compact="displayMode === 'rows'"
        :show-viewer-data="showViewerData"
        :planning="planning.has(item.deck.deckId)"
        @plan="addToPlanning(item)"
      />
    </ol>

    <Paginator
      v-if="totalPages > 1"
      :rows="COMMUNITY_LIST_ITEM_PAGE_SIZE"
      :total-records="totalItems"
      :first="offset"
      :aria-label="`Page ${currentPage} of ${totalPages}`"
      @page="onPageChange"
    />

    <Dialog v-model:visible="reportOpen" modal header="Report this list to the moderators" :draggable="false" class="w-[30rem] max-w-[calc(100vw-2rem)]">
      <form class="flex flex-col gap-2" @submit.prevent="sendReport">
        <div class="mb-2 flex flex-col gap-2 text-sm">
          <p class="m-0">Reports go to Jiten's moderation. The list's owner cannot see who reported it.</p>
          <p class="m-0">Report a list if its name, description or notes contain:</p>
          <ul class="m-0 flex list-disc flex-col gap-0.5 pl-5">
            <li v-for="rule in COMMUNITY_LIST_FORBIDDEN_CONTENT" :key="rule">{{ rule }}</li>
          </ul>
          <p class="m-0 text-muted-color">
            Disagreeing with the picks or their order isn't a reason to report.
          </p>
        </div>
        <label for="reportReason" class="text-sm font-semibold">What's the problem?</label>
        <Textarea id="reportReason" v-model="reportReason" rows="4" :maxlength="COMMUNITY_LIST_MAX_REPORT" auto-resize class="w-full" />
        <span class="self-end text-xs text-muted-color tabular-nums">{{ reportReason.length }} / {{ COMMUNITY_LIST_MAX_REPORT }}</span>
        <div class="flex justify-end gap-2">
          <Button type="button" label="Cancel" severity="secondary" text @click="reportOpen = false" />
          <Button type="submit" label="Send report" :loading="reportSending" :disabled="!reportReason.trim()" />
        </div>
      </form>
    </Dialog>
  </div>
</template>
