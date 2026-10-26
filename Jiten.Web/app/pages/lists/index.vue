<script setup lang="ts">
  import type { CommunityListBrowsePage, CommunityListFacets, CommunityListQuota, CommunityListSummary } from '~/types/types';
  import type { CommunityListSort } from '~/composables/useCommunityLists';
  import { debounce } from 'perfect-debounce';
  import { parseNumberArray } from '~/utils/queryParams';

  type SortKey = 'top' | 'popular-week' | 'popular-month' | 'newest' | 'updated' | 'random';

  const PAGE_SIZE = 24;
  const STRIP_SIZE = 10;
  const SORT_KEYS: SortKey[] = ['top', 'popular-week', 'popular-month', 'newest', 'updated', 'random'];
  type PopularWindow = 'popular-week' | 'popular-month';
  const SORT_TABS: { label: string; value: CommunityListSort }[] = [
    { label: 'Top', value: 'top' },
    { label: 'Popular', value: 'popular' },
    { label: 'Newest', value: 'newest' },
    { label: 'Updated', value: 'updated' },
    { label: 'Random', value: 'random' },
  ];
  const POPULAR_WINDOWS: { label: string; value: PopularWindow }[] = [
    { label: '7 days', value: 'popular-week' },
    { label: '30 days', value: 'popular-month' },
  ];

  const route = useRoute();
  const router = useRouter();
  const authStore = useAuthStore();
  const lists = useCommunityLists();

  const DESCRIPTION = 'Community media lists made and shared by Jiten users.';

  useSeoMeta({
    title: 'Community lists of Japanese media',
    description: DESCRIPTION,
  });

  if (import.meta.server) {
    defineOgImage('PageOgImage', { title: 'Community lists', description: DESCRIPTION });
  }

  const tabs = ['browse', 'mine', 'favourited'] as const;
  type TabKey = (typeof tabs)[number];
  const TAB_OPTIONS: { value: TabKey; label: string }[] = [
    { value: 'browse', label: 'Browse' },
    { value: 'mine', label: 'My lists' },
    { value: 'favourited', label: 'Favourited' },
  ];
  const activeTab = ref<TabKey>(tabs.includes(route.query.tab as TabKey) && authStore.isAuthenticated ? (route.query.tab as TabKey) : 'browse');

  const sortKey = ref<SortKey>(SORT_KEYS.includes(route.query.sort as SortKey) ? (route.query.sort as SortKey) : 'top');
  const lastPopularWindow = ref<PopularWindow>(sortKey.value === 'popular-month' ? 'popular-month' : 'popular-week');
  const sortTab = computed<CommunityListSort>({
    get: () => (sortKey.value.startsWith('popular') ? 'popular' : (sortKey.value as CommunityListSort)),
    set: (tab) => (sortKey.value = tab === 'popular' ? lastPopularWindow.value : tab),
  });
  const newSeed = () => Math.floor(Math.random() * 2_000_000_000) + 1;
  const querySeed = Number(route.query.seed) > 0 ? Number(route.query.seed) : undefined;
  // Shared with the client payload, so a seed picked during SSR doesn't reshuffle the page on hydration.
  const seed = useState<number | undefined>('community-lists-seed', () => querySeed ?? (sortKey.value === 'random' ? newSeed() : undefined));
  if (querySeed) seed.value = querySeed;
  const searchQuery = ref(typeof route.query.q === 'string' ? route.query.q : '');
  const debouncedSearch = ref(searchQuery.value);
  const includeGenres = ref<number[]>(parseNumberArray(route.query.genres));
  const excludeGenres = ref<number[]>(parseNumberArray(route.query.excludeGenres));
  const includeTags = ref<number[]>(parseNumberArray(route.query.tags));
  const excludeTags = ref<number[]>(parseNumberArray(route.query.excludeTags));
  const offset = ref(Number(route.query.page) > 1 ? (Number(route.query.page) - 1) * PAGE_SIZE : 0);

  watch(
    searchQuery,
    debounce((value: string) => (debouncedSearch.value = value), 300)
  );

  const csv = (ids: number[]) => (ids.length ? ids.join(',') : undefined);

  const browseQuery = {
    sort: sortTab,
    days: computed(() => (sortKey.value === 'popular-month' ? 30 : sortKey.value === 'popular-week' ? 7 : undefined)),
    seed: computed(() => (sortKey.value === 'random' ? seed.value : undefined)),
    q: computed(() => debouncedSearch.value.trim() || undefined),
    genres: computed(() => csv(includeGenres.value)),
    excludeGenres: computed(() => csv(excludeGenres.value)),
    tags: computed(() => csv(includeTags.value)),
    excludeTags: computed(() => csv(excludeTags.value)),
    offset,
    limit: PAGE_SIZE,
  };

  const { data: page, status: pageStatus, error: pageError } = useApiFetch<CommunityListBrowsePage>('lists', { query: browseQuery });

  const hasFilters = computed(
    () => !!debouncedSearch.value.trim() || includeGenres.value.length + excludeGenres.value.length + includeTags.value.length + excludeTags.value.length > 0
  );
  const stripsVisible = computed(() => activeTab.value === 'browse' && !hasFilters.value && offset.value === 0);
  const {
    data: newStrip,
    status: newStripStatus,
    execute: loadNewStrip,
  } = useApiFetch<CommunityListBrowsePage>('lists', {
    query: { sort: 'newest', limit: STRIP_SIZE },
    immediate: stripsVisible.value,
  });
  const {
    data: popularStrip,
    status: popularStripStatus,
    execute: loadPopularStrip,
  } = useApiFetch<CommunityListBrowsePage>('lists', {
    query: { sort: 'popular', days: 7, limit: STRIP_SIZE },
    immediate: stripsVisible.value,
  });
  watch(stripsVisible, (visible) => {
    if (!visible) return;
    if (!newStrip.value) loadNewStrip();
    if (!popularStrip.value) loadPopularStrip();
  });

  const strips = computed(() =>
    !stripsVisible.value
      ? []
      : [
          { key: 'new', title: 'New lists', sort: 'newest' as SortKey, page: newStrip.value, status: newStripStatus.value },
          { key: 'popular', title: 'Popular this week', sort: 'popular-week' as SortKey, page: popularStrip.value, status: popularStripStatus.value },
        ]
          .map(({ page, status, ...strip }) => ({ ...strip, lists: page?.data ?? [], loading: !page && status !== 'error' }))
          .filter((strip) => strip.loading || strip.lists.length > 0)
  );

  const facets = ref<CommunityListFacets | null>(null);
  async function loadFacets() {
    try {
      facets.value = await lists.facets({
        q: debouncedSearch.value,
        genres: includeGenres.value,
        excludeGenres: excludeGenres.value,
        tags: includeTags.value,
        excludeTags: excludeTags.value,
      });
    } catch {
      facets.value = null;
    }
  }

  function shuffle() {
    seed.value = newSeed();
    offset.value = 0;
  }

  watch(sortKey, (key) => {
    offset.value = 0;
    if (key === 'popular-week' || key === 'popular-month') lastPopularWindow.value = key;
    if (key === 'random' && !seed.value) shuffle();
  });
  watch([debouncedSearch, includeGenres, excludeGenres, includeTags, excludeTags], () => {
    offset.value = 0;
    loadFacets();
  });

  watch([activeTab, sortKey, seed, debouncedSearch, includeGenres, excludeGenres, includeTags, excludeTags, offset], () => {
    const query: Record<string, string> = {};
    if (activeTab.value !== 'browse') query.tab = activeTab.value;
    if (sortKey.value !== 'top') query.sort = sortKey.value;
    if (sortKey.value === 'random' && seed.value) query.seed = String(seed.value);
    if (debouncedSearch.value.trim()) query.q = debouncedSearch.value.trim();
    if (includeGenres.value.length) query.genres = includeGenres.value.join(',');
    if (excludeGenres.value.length) query.excludeGenres = excludeGenres.value.join(',');
    if (includeTags.value.length) query.tags = includeTags.value.join(',');
    if (excludeTags.value.length) query.excludeTags = excludeTags.value.join(',');
    if (offset.value > 0) query.page = String(offset.value / PAGE_SIZE + 1);
    router.replace({ query });
  });

  function onPageChange(event: { first: number }) {
    offset.value = event.first;
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  const mine = ref<CommunityListSummary[] | null>(null);
  const quota = ref<CommunityListQuota | null>(null);
  const favourited = ref<CommunityListSummary[] | null>(null);
  const favouritedTotal = ref(0);
  const loadingMoreFavourited = ref(false);
  const tabError = ref(false);
  const tabLists = computed(() => (activeTab.value === 'mine' ? mine.value : favourited.value));

  async function loadTab(tab: TabKey) {
    if (tab === 'browse' || !authStore.isAuthenticated) return;
    tabError.value = false;
    try {
      if (tab === 'mine') {
        const result = await lists.mine();
        mine.value = result.lists;
        quota.value = result.quota;
      } else {
        const result = await lists.favourited(0);
        favourited.value = result.data;
        favouritedTotal.value = result.totalItems;
      }
    } catch {
      tabError.value = true;
    }
  }
  watch(activeTab, loadTab);

  async function loadMoreFavourited() {
    if (!favourited.value || loadingMoreFavourited.value) return;
    loadingMoreFavourited.value = true;
    try {
      const result = await lists.favourited(favourited.value.length);
      const seen = new Set(favourited.value.map((l) => l.listId));
      favourited.value = [...favourited.value, ...result.data.filter((l) => !seen.has(l.listId))];
      favouritedTotal.value = result.totalItems;
    } catch {
      tabError.value = true;
    } finally {
      loadingMoreFavourited.value = false;
    }
  }

  onMounted(() => {
    loadFacets();
    loadTab(activeTab.value);
    if (authStore.isAuthenticated && activeTab.value !== 'mine')
      lists
        .quota()
        .then((q) => (quota.value = q))
        .catch(() => {});
  });

  function createList() {
    if (!authStore.isAuthenticated) {
      navigateTo({ path: '/login', query: { redirect: '/lists/new' } });
      return;
    }
    navigateTo('/lists/new');
  }
</script>

<template>
  <div class="container mx-auto flex flex-col gap-4 p-2 md:p-4">
    <div class="flex flex-wrap items-end gap-x-6 gap-y-1" :class="{ 'border-b border-surface-200 dark:border-surface-700': authStore.isAuthenticated }">
      <div class="flex flex-col gap-1 pb-2">
        <h1 class="m-0 text-2xl leading-tight font-bold">Community lists</h1>
        <p v-if="!authStore.isAuthenticated" class="m-0 text-sm text-muted-color">Community media lists made by users.</p>
      </div>
      <nav v-if="authStore.isAuthenticated" aria-label="Lists" class="order-3 -mb-px flex w-full gap-1 sm:order-2 sm:w-auto">
        <button
          v-for="tab in TAB_OPTIONS"
          :key="tab.value"
          type="button"
          :aria-pressed="activeTab === tab.value"
          class="cursor-pointer border-b-2 px-3 pt-1 pb-2 text-sm font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-purple-500"
          :class="
            activeTab === tab.value
              ? 'border-purple-500 text-purple-700 dark:text-purple-300'
              : 'border-transparent text-muted-color hover:text-surface-800 dark:hover:text-surface-100'
          "
          @click="activeTab = tab.value"
        >
          {{ tab.label }}
        </button>
      </nav>
      <div class="order-2 ml-auto flex items-center gap-3 pb-2 sm:order-3">
        <span v-if="activeTab === 'mine' && quota && mine?.length" class="text-sm whitespace-nowrap text-muted-color">
          {{ quota.listCount }} / {{ quota.maxLists }} lists
        </span>
        <Tooltip v-if="quota && !quota.canCreate" :content="`You've reached your limit of ${quota.maxLists} lists.`">
          <span><Button label="Create list" icon="pi pi-plus" disabled /></span>
        </Tooltip>
        <Button v-else label="Create list" icon="pi pi-plus" @click="createList" />
      </div>
    </div>

    <template v-if="activeTab === 'browse'">
      <div v-if="strips.length" class="grid grid-cols-1 gap-x-6 gap-y-4" :class="{ 'md:grid-cols-2': strips.length > 1 }">
        <section v-for="strip in strips" :key="strip.key" :aria-labelledby="`${strip.key}-lists-heading`" class="flex min-w-0 flex-col gap-2">
          <div class="flex items-baseline justify-between gap-2">
            <h2 :id="`${strip.key}-lists-heading`" class="m-0 text-base font-semibold">{{ strip.title }}</h2>
            <button
              v-if="sortKey !== strip.sort"
              type="button"
              class="cursor-pointer text-sm text-purple-700 hover:underline focus-visible:outline-2 focus-visible:outline-purple-500 dark:text-purple-300"
              @click="sortKey = strip.sort"
            >
              See all
            </button>
          </div>
          <div v-if="strip.loading" class="-mx-2 flex gap-3 overflow-hidden px-2 pb-2">
            <CommunityListCardSkeleton v-for="n in 3" :key="n" compact />
          </div>
          <div v-else class="scrollbar-styled -mx-2 flex gap-3 overflow-x-auto px-2 pb-2">
            <CommunityListCard v-for="list in strip.lists" :key="list.listId" :list="list" compact />
          </div>
        </section>
      </div>

      <div class="flex flex-col gap-3 sm:flex-row">
        <SearchInput
          v-model="searchQuery"
          placeholder="Search lists..."
          aria-label="Search lists"
          search-icon-class="pi pi-search"
          clear-icon-class="pi pi-times"
          class="min-w-0 flex-1"
        />
        <div class="min-w-0 sm:w-60">
          <RequestGenreTagFilter
            v-model:include-genres="includeGenres"
            v-model:exclude-genres="excludeGenres"
            v-model:include-tags="includeTags"
            v-model:exclude-tags="excludeTags"
            :genre-counts="facets?.genres"
            :tag-counts="facets?.tags"
            :result-count="page?.totalItems ?? null"
            noun="list"
            noun-plural="lists"
          />
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-2">
        <div class="max-w-full overflow-x-auto [scrollbar-width:none]">
          <SelectButton
            v-model="sortTab"
            :options="SORT_TABS"
            option-label="label"
            option-value="value"
            data-key="value"
            :allow-empty="false"
            aria-label="Sort"
            class="flex-nowrap whitespace-nowrap max-sm:[&_.p-togglebutton]:p-0.5! max-sm:[&_.p-togglebutton]:text-sm! max-sm:[&_.p-togglebutton-content]:px-1!"
          />
        </div>
        <SelectButton
          v-if="sortTab === 'popular'"
          v-model="sortKey"
          :options="POPULAR_WINDOWS"
          option-label="label"
          option-value="value"
          data-key="value"
          :allow-empty="false"
          aria-label="Popular over"
          size="small"
        />
        <Button v-else-if="sortTab === 'random'" label="Shuffle" icon="pi pi-refresh" severity="secondary" outlined size="small" @click="shuffle" />
      </div>

      <div v-if="pageStatus === 'pending' && !page" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
        <CommunityListCardSkeleton v-for="n in 8" :key="n" />
      </div>
      <Message v-else-if="pageError && !page" severity="error" :closable="false">The lists didn't load. Refresh the page to try again.</Message>
      <div
        v-else-if="page && page.data.length === 0"
        class="rounded-lg border border-dashed border-surface-300 px-4 py-10 text-center text-muted-color dark:border-surface-600"
      >
        <p v-if="hasFilters" class="m-0">No list matches these filters.</p>
        <template v-else>
          <p class="m-0">No one has published a list yet.</p>
          <Button label="Create the first one" icon="pi pi-plus" text class="mt-2" @click="createList" />
        </template>
      </div>
      <template v-else-if="page">
        <div
          class="grid grid-cols-1 gap-4 transition-opacity duration-200 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4"
          :class="{ 'opacity-60 delay-200': pageStatus === 'pending' }"
        >
          <CommunityListCard v-for="list in page.data" :key="list.listId" :list="list" />
        </div>
        <Paginator v-if="page.totalItems > PAGE_SIZE" :rows="PAGE_SIZE" :total-records="page.totalItems" :first="offset" @page="onPageChange" />
      </template>
    </template>

    <template v-else>
      <Message v-if="tabError && !tabLists" severity="error" :closable="false">These lists didn't load. Refresh the page to try again.</Message>
      <div v-else-if="!tabLists" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
        <CommunityListCardSkeleton v-for="n in 4" :key="n" />
      </div>
      <template v-else-if="activeTab === 'mine'">
        <div v-if="mine && mine.length" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
          <CommunityListCard v-for="list in mine" :key="list.listId" :list="list" />
        </div>
        <div v-else class="rounded-lg border border-dashed border-surface-300 px-4 py-10 text-center text-muted-color dark:border-surface-600">
          <p class="m-0">You haven't made a list yet.</p>
          <Button label="Create a list" icon="pi pi-plus" text class="mt-2" @click="createList" />
        </div>
      </template>
      <template v-else>
        <div v-if="favourited && favourited.length" class="grid grid-cols-1 gap-4 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-4">
          <CommunityListCard v-for="list in favourited" :key="list.listId" :list="list" />
        </div>
        <div v-if="favourited && favourited.length < favouritedTotal" class="flex justify-center">
          <Button label="Show more" severity="secondary" outlined :loading="loadingMoreFavourited" @click="loadMoreFavourited" />
        </div>
        <div v-else class="rounded-lg border border-dashed border-surface-300 px-4 py-10 text-center text-muted-color dark:border-surface-600">
          <p class="m-0">Lists you favourite will show up here.</p>
          <Button label="Browse lists" text class="mt-2" @click="activeTab = 'browse'" />
        </div>
      </template>
    </template>
  </div>
</template>
