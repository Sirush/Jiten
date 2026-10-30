<script setup lang="ts">
  import { MediaType, RequestKind, RequestStatus } from '~/types';
  import type { MediaRequestDto } from '~/types/types';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { getRequestStatusText, getRequestStatusSeverity } from '~/utils/requestStatusMapper';
  import { getRequestKindText, getRequestKindIcon } from '~/utils/requestKindMapper';
  import { getLinkTypeText, resolveLinkType } from '~/utils/linkTypeMapper';
  import { getGenreText } from '~/utils/genreMapper';
  import type { RequestFacets, BoostBalance, RequestGenreTagFilter } from '~/composables/useMediaRequests';
  import { formatDateOmitCurrentYear } from '~/utils/dateFormat';

  definePageMeta({
    middleware: ['auth'],
  });

  useHead({ title: 'Media Requests' });

  const {
    requests,
    totalCount,
    isLoading,
    fetchRequests,
    toggleUpvote,
    subscribe,
    unsubscribe,
    fetchMyQuota,
    fetchBoostBalance,
    fetchFacets,
    error: apiError,
  } = useMediaRequests();
  const facets = ref<RequestFacets | null>(null);

  const { isPlus, hasFeature } = useJitenPlus();
  const canBoost = computed(() => hasFeature('request-boosts'));
  const boostBalance = ref<BoostBalance | null>(null);
  const showBoostBalance = computed(() => canBoost.value && boostBalance.value !== null);

  function isBoostable(request: MediaRequestDto) {
    return request.status === RequestStatus.Open || request.status === RequestStatus.InProgress;
  }

  function withCount(label: string, count: number | undefined) {
    return count === undefined ? label : `${label} (${count})`;
  }

  const authStore = useAuthStore();
  const quota = ref<MediaRequestQuota | null>(null);
  const toast = useToast();
  const router = useRouter();
  const route = useRoute();

  const limit = 20;

  function parseTabFromQuery() {
    if (route.query.tab === 'mine') return 1;
    if (route.query.tab === 'contributions') return 2;
    if (route.query.tab === 'voted') return 3;
    return 0;
  }
  function parseTypeFromQuery() {
    return route.query.type !== undefined ? (Number(route.query.type) as MediaType) : undefined;
  }
  function parseStatusFromQuery() {
    if (route.query.status === 'all') return undefined;
    if (route.query.status !== undefined) return Number(route.query.status) as RequestStatus;
    return parseTabFromQuery() === 0 ? RequestStatus.Open : undefined;
  }
  function parseSortFromQuery() {
    return typeof route.query.sort === 'string' ? route.query.sort : 'votes';
  }
  function parseOffsetFromQuery() {
    return route.query.page ? (Number(route.query.page) - 1) * limit : 0;
  }
  function parseKindFromQuery() {
    return route.query.kind !== undefined ? (Number(route.query.kind) as RequestKind) : undefined;
  }

  const activeTab = ref(parseTabFromQuery());
  const selectedMediaType = ref<MediaType | undefined>(parseTypeFromQuery());
  const selectedStatus = ref<RequestStatus | undefined>(parseStatusFromQuery());
  const selectedKind = ref<RequestKind | undefined>(parseKindFromQuery());
  const sortBy = ref(parseSortFromQuery());
  const offset = ref(parseOffsetFromQuery());

  const mediaTypeOptions = computed(() => [
    { label: withCount('All media types', facets.value?.mediaTypeTotal), value: undefined },
    ...Object.values(MediaType)
      .filter((v) => typeof v === 'number')
      .map((v) => ({
        label: withCount(getMediaTypeText(v as MediaType), facets.value ? (facets.value.mediaTypes[String(v)] ?? 0) : undefined),
        value: v as MediaType,
      })),
  ]);

  const statusOptions = computed(() => [
    { label: withCount('All statuses', facets.value?.statusTotal), value: undefined },
    { label: withCount('Open', facets.value ? (facets.value.statuses[String(RequestStatus.Open)] ?? 0) : undefined), value: RequestStatus.Open },
    {
      label: withCount('In Progress', facets.value ? (facets.value.statuses[String(RequestStatus.InProgress)] ?? 0) : undefined),
      value: RequestStatus.InProgress,
    },
    { label: withCount('Completed', facets.value ? (facets.value.statuses[String(RequestStatus.Completed)] ?? 0) : undefined), value: RequestStatus.Completed },
    { label: withCount('Rejected', facets.value ? (facets.value.statuses[String(RequestStatus.Rejected)] ?? 0) : undefined), value: RequestStatus.Rejected },
  ]);

  const kindOptions = computed(() => [
    { label: withCount('All request types', facets.value?.kindTotal), value: undefined },
    { label: withCount('New requests', facets.value ? (facets.value.kinds[String(RequestKind.New)] ?? 0) : undefined), value: RequestKind.New },
    { label: withCount('Update requests', facets.value ? (facets.value.kinds[String(RequestKind.Update)] ?? 0) : undefined), value: RequestKind.Update },
  ]);

  const sortOptions = [
    { label: 'Most Voted', value: 'votes' },
    { label: 'Newest', value: 'recent' },
    { label: 'Last Completed', value: 'completed' },
  ];

  const attachmentOptions = computed(() => [
    { label: withCount('Any attachments', facets.value?.attachmentTotal), value: undefined },
    { label: withCount('Has attachments', facets.value?.attachmentsYes), value: 'yes' },
    { label: withCount('No attachments', facets.value?.attachmentsNo), value: 'no' },
  ]);

  const showAttachmentSplit = computed(
    () => facets.value !== null && (selectedStatus.value === RequestStatus.Open || selectedStatus.value === RequestStatus.InProgress)
  );

  function parseAttachmentsFromQuery() {
    const v = route.query.attachments;
    return v === 'yes' || v === 'no' ? v : undefined;
  }

  const selectedAttachments = ref<string | undefined>(parseAttachmentsFromQuery());

  function parseIdsFromQuery(key: string): number[] {
    const raw = route.query[key];
    if (typeof raw !== 'string' || !raw) return [];
    return raw
      .split(',')
      .map(Number)
      .filter((id) => Number.isInteger(id) && id > 0);
  }

  const includeGenres = ref<number[]>(parseIdsFromQuery('genres'));
  const excludeGenres = ref<number[]>(parseIdsFromQuery('excludeGenres'));
  const includeTags = ref<number[]>(parseIdsFromQuery('tags'));
  const excludeTags = ref<number[]>(parseIdsFromQuery('excludeTags'));
  const genreTagFilter = computed<RequestGenreTagFilter>(() => ({
    includeGenres: includeGenres.value,
    excludeGenres: excludeGenres.value,
    includeTags: includeTags.value,
    excludeTags: excludeTags.value,
  }));
  const excludeOwnRequests = ref(route.query.excludeOwn === '1');

  const searchQuery = ref(typeof route.query.search === 'string' ? route.query.search : '');
  const debouncedSearch = ref(searchQuery.value);
  let searchTimeout: ReturnType<typeof setTimeout>;
  watch(searchQuery, (val) => {
    clearTimeout(searchTimeout);
    searchTimeout = setTimeout(() => {
      debouncedSearch.value = val;
    }, 300);
  });

  const isMine = computed(() => activeTab.value === 1);
  const isContributed = computed(() => activeTab.value === 2);
  const isVoted = computed(() => activeTab.value === 3);
  const canExcludeOwn = computed(() => isContributed.value || isVoted.value);

  const displaySections = computed(() => {
    return [{ title: '', items: requests.value, muted: false }];
  });

  async function loadRequests() {
    await fetchRequests({
      mediaType: selectedMediaType.value,
      status: selectedStatus.value,
      kind: selectedKind.value,
      sort: sortBy.value,
      offset: offset.value,
      limit,
      mine: isMine.value || undefined,
      contributed: isContributed.value || undefined,
      voted: isVoted.value || undefined,
      excludeOwn: (canExcludeOwn.value && excludeOwnRequests.value) || undefined,
      search: debouncedSearch.value.trim() || undefined,
      attachments: selectedAttachments.value,
      genreTags: genreTagFilter.value,
    });
  }

  async function loadFacets() {
    const result = await fetchFacets({
      mediaType: selectedMediaType.value,
      status: selectedStatus.value,
      kind: selectedKind.value,
      mine: isMine.value || undefined,
      contributed: isContributed.value || undefined,
      voted: isVoted.value || undefined,
      excludeOwn: (canExcludeOwn.value && excludeOwnRequests.value) || undefined,
      search: debouncedSearch.value.trim() || undefined,
      attachments: selectedAttachments.value,
      genreTags: genreTagFilter.value,
    });
    if (result) facets.value = result;
  }

  watch(activeTab, () => {
    searchQuery.value = '';
    debouncedSearch.value = '';
    selectedStatus.value = activeTab.value === 0 ? RequestStatus.Open : undefined;
  });

  watch(selectedStatus, (status) => {
    if (status === RequestStatus.Completed) sortBy.value = 'completed';
    else if (sortBy.value === 'completed') sortBy.value = 'votes';
  });

  watch([selectedMediaType, selectedStatus, selectedKind, sortBy, activeTab, debouncedSearch, selectedAttachments, excludeOwnRequests, genreTagFilter], () => {
    offset.value = 0;
    loadRequests();
    loadFacets();
    fetchMyQuota().then((q) => {
      quota.value = q;
    });
  });

  watch(offset, () => loadRequests());

  watch([activeTab, selectedMediaType, selectedStatus, selectedKind, sortBy, offset, debouncedSearch, selectedAttachments, excludeOwnRequests, genreTagFilter], () => {
    const query: Record<string, string> = {};
    if (activeTab.value === 1) query.tab = 'mine';
    else if (activeTab.value === 2) query.tab = 'contributions';
    else if (activeTab.value === 3) query.tab = 'voted';
    if (selectedMediaType.value !== undefined) query.type = String(selectedMediaType.value);
    if (selectedKind.value !== undefined) query.kind = String(selectedKind.value);
    if (debouncedSearch.value.trim()) query.search = debouncedSearch.value.trim();
    if (selectedAttachments.value) query.attachments = selectedAttachments.value;
    if (canExcludeOwn.value && excludeOwnRequests.value) query.excludeOwn = '1';
    if (includeGenres.value.length) query.genres = includeGenres.value.join(',');
    if (excludeGenres.value.length) query.excludeGenres = excludeGenres.value.join(',');
    if (includeTags.value.length) query.tags = includeTags.value.join(',');
    if (excludeTags.value.length) query.excludeTags = excludeTags.value.join(',');
    if (selectedStatus.value === undefined) query.status = 'all';
    else if (selectedStatus.value !== RequestStatus.Open) query.status = String(selectedStatus.value);
    if (sortBy.value !== 'votes') query.sort = sortBy.value;
    if (offset.value > 0) query.page = String(offset.value / limit + 1);
    router.replace({ query });
  });

  function toastApiError(summary: string, fallback: string) {
    toast.add({ severity: 'error', summary, detail: extractApiError(apiError.value, fallback), life: 6000 });
  }

  async function handleUpvote(request: MediaRequestDto) {
    const result = await toggleUpvote(request.id);
    if (result) {
      request.hasUserUpvoted = result.upvoted;
      request.upvoteCount = result.upvoteCount;
      request.isSubscribed = result.subscribed;
    } else {
      toastApiError('Vote failed', 'Failed to update your vote. Please try again.');
    }
  }

  async function loadHeaderStats() {
    quota.value = await fetchMyQuota();
    if (isPlus.value) boostBalance.value = await fetchBoostBalance();
  }

  function handleBoosted(request: MediaRequestDto, payload: { boostCount: number; balance: BoostBalance }) {
    request.boostCount = payload.boostCount;
    request.hasUserBoosted = true;
    boostBalance.value = payload.balance;
  }

  async function handleSubscribe(request: MediaRequestDto) {
    if (request.isSubscribed) {
      const success = await unsubscribe(request.id);
      if (success) request.isSubscribed = false;
      else toastApiError('Unsubscribe failed', 'Failed to unsubscribe. Please try again.');
    } else {
      const success = await subscribe(request.id);
      if (success) request.isSubscribed = true;
      else toastApiError('Subscribe failed', 'Failed to subscribe. Please try again.');
    }
  }

  function formatCompletedAt(dateString: string): string {
    const date = new Date(dateString);
    const now = new Date();
    const diffDays = Math.floor((now.getTime() - date.getTime()) / 86400000);
    if (diffDays < 30) return `${diffDays || 1}d ago`;
    return formatDateOmitCurrentYear(date, now);
  }

  function formatTimeAgo(dateString: string): string {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    if (diffDays < 30) return `${diffDays}d ago`;
    const diffMonths = Math.floor(diffDays / 30);
    return `${diffMonths}mo ago`;
  }

  function alternateTitles(request: MediaRequestDto): string | null {
    const seen = new Set([request.title.trim().toLowerCase()]);
    const titles = [request.originalTitle, request.romajiTitle, request.englishTitle].filter((title): title is string => {
      if (!title) return false;
      const key = title.trim().toLowerCase();
      if (seen.has(key)) return false;
      seen.add(key);
      return true;
    });
    return titles.length ? titles.join(' / ') : null;
  }

  // A single-status filter already says the status; a completion date in the meta line says it too.
  function showStatusTag(request: MediaRequestDto) {
    return selectedStatus.value === undefined && !request.completedAt;
  }

  function onPageChange(event: { first: number }) {
    offset.value = event.first;
  }

  onMounted(() => {
    loadRequests();
    loadFacets();
    loadHeaderStats();
  });

  watch(isPlus, (val) => {
    if (val && !boostBalance.value)
      fetchBoostBalance().then((b) => {
        boostBalance.value = b;
      });
  });
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex items-center justify-between mb-6">
      <h1 class="text-2xl font-bold">Media Requests</h1>
      <Button label="New Request" icon="pi pi-plus" @click="router.push('/requests/new')" />
    </div>

    <Tabs v-model:value="activeTab" :show-navigators="false">
      <TabList>
        <Tab :value="0">All Requests</Tab>
        <Tab :value="1">My Requests</Tab>
        <Tab :value="2">My Contributions</Tab>
        <Tab :value="3">Voted On</Tab>
      </TabList>
    </Tabs>

    <div class="my-4 flex flex-col gap-3">
      <div class="flex flex-col sm:flex-row gap-3">
        <SearchInput
          v-model="searchQuery"
          placeholder="Search titles..."
          aria-label="Search titles"
          search-icon-class="pi pi-search"
          clear-icon-class="pi pi-times"
          class="min-w-0 flex-1"
        />
        <Select
          v-model="selectedMediaType"
          :options="mediaTypeOptions"
          option-label="label"
          option-value="value"
          aria-label="Media type"
          scroll-height="24rem"
          class="min-w-0 sm:w-60"
        />
        <Select v-model="sortBy" :options="sortOptions" option-label="label" option-value="value" aria-label="Sort" class="sm:w-52">
          <template #value="{ value }">Sort: {{ sortOptions.find((option) => option.value === value)?.label }}</template>
        </Select>
      </div>
      <div class="grid grid-cols-2 lg:grid-cols-4 gap-3">
        <Select v-model="selectedStatus" :options="statusOptions" option-label="label" option-value="value" aria-label="Status" class="w-full min-w-0" />
        <Select
          v-model="selectedAttachments"
          :options="attachmentOptions"
          option-label="label"
          option-value="value"
          aria-label="Attachments"
          class="w-full min-w-0"
        />
        <Select v-model="selectedKind" :options="kindOptions" option-label="label" option-value="value" aria-label="Request type" class="w-full min-w-0" />
        <div class="min-w-0">
          <RequestGenreTagFilter
            v-model:include-genres="includeGenres"
            v-model:exclude-genres="excludeGenres"
            v-model:include-tags="includeTags"
            v-model:exclude-tags="excludeTags"
            :genre-counts="facets?.genreCounts"
            :tag-counts="facets?.tagCounts"
            :result-count="totalCount"
          />
        </div>
      </div>
      <div v-if="canExcludeOwn" class="flex items-center gap-2">
        <Checkbox v-model="excludeOwnRequests" input-id="excludeOwnRequests" :binary="true" />
        <label for="excludeOwnRequests" class="text-sm cursor-pointer">Exclude my own requests</label>
      </div>
    </div>

    <div class="flex flex-wrap items-center gap-2 mb-4">
      <Tag
        v-if="quota"
        v-tooltip.top="
          quota.isPlus
            ? 'Open requests you currently have across the platform'
            : `Open requests you currently have across the platform. Jiten+ raises this to ${quota.plusLimit} slots.`
        "
        :value="`${quota.activeCount} / ${quota.limit} active slots used`"
        severity="secondary"
      />
      <Tag
        v-if="showBoostBalance"
        v-tooltip.top="'Monthly Jiten+ boosts remaining'"
        :value="`${boostBalance!.remaining} / ${boostBalance!.limit} boosts left this month`"
        severity="secondary"
      />
      <Tooltip v-if="showAttachmentSplit" content="Requests with a file uploaded can be worked on. The rest are waiting for someone to provide the text.">
        <Tag :value="`${facets!.attachmentsYes} ready to process, ${facets!.attachmentsNo} waiting for a file`" severity="secondary" />
      </Tooltip>
    </div>

    <div v-if="isLoading" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <div v-else-if="requests.length === 0" class="text-center py-12 text-muted-color">
      <i class="pi pi-inbox text-4xl mb-3" />
      <template v-if="isMine">
        <p>You haven't made any requests yet.</p>
        <Button label="Make a request" icon="pi pi-arrow-right" icon-pos="right" text class="mt-2" @click="router.push('/requests/new')" />
      </template>
      <template v-else-if="isContributed">
        <p v-if="excludeOwnRequests">No contributions to other people's requests yet.</p>
        <p v-else>You haven't contributed to any requests yet.</p>
      </template>
      <template v-else-if="isVoted">
        <p v-if="excludeOwnRequests">You haven't voted on anyone else's requests yet.</p>
        <p v-else>You haven't voted on any requests yet.</p>
      </template>
      <p v-else>No requests found. Be the first!</p>
    </div>

    <template v-else>
      <template v-for="section in displaySections" :key="section.title">
        <div v-if="section.items.length > 0" class="mb-6" :class="{ 'opacity-50': section.muted }">
          <h3 v-if="section.title" class="text-lg font-semibold mb-3 flex items-center gap-2">
            {{ section.title }}
            <span class="text-sm font-normal text-muted-color">({{ section.items.length }})</span>
          </h3>
          <TransitionGroup name="request-list" tag="div" class="flex flex-col gap-3">
            <NuxtLink v-for="request in section.items" :key="request.id" :to="`/requests/${request.id}`" class="no-underline! text-inherit">
              <Card class="shadow-sm cursor-pointer hover:bg-surface-50 dark:hover:bg-surface-800 transition-colors">
                <template #content>
                  <div class="flex items-start gap-2 md:gap-4">
                    <div @click.prevent>
                      <UpvoteButton
                        :has-upvoted="request.hasUserUpvoted"
                        :upvote-count="request.upvoteCount"
                        :boost-count="request.boostCount"
                        compact
                        @toggle="handleUpvote(request)"
                      />
                    </div>

                    <div class="flex-1 min-w-0 flex flex-col gap-1.5">
                      <div class="flex items-center gap-2 flex-wrap">
                        <span class="font-semibold text-lg">{{ request.title }}</span>
                        <Tag v-if="request.kind === RequestKind.Update" :value="getRequestKindText(request.kind)" :icon="getRequestKindIcon(request.kind)" />
                        <Tag v-if="showStatusTag(request)" :value="getRequestStatusText(request.status)" :severity="getRequestStatusSeverity(request.status)" />
                        <Tag v-if="request.possibleDuplicateDeckId" value="Maybe already on Jiten" severity="warn" icon="pi pi-clone" />
                        <Tag v-else-if="request.possibleDuplicateOfRequestId" value="Possible duplicate" severity="warn" icon="pi pi-clone" />
                      </div>

                      <p v-if="alternateTitles(request)" class="text-sm text-muted-color truncate">{{ alternateTitles(request) }}</p>

                      <p v-if="request.kind === RequestKind.Update" class="text-sm text-muted-color truncate">
                        Update to: {{ request.targetDeckTitle ?? 'media no longer available' }}
                      </p>

                      <div class="flex flex-wrap items-center gap-1">
                        <span class="mr-2 text-sm text-primary-600 dark:text-primary-400">{{ getMediaTypeText(request.mediaType) }}</span>
                        <span
                          v-for="genre in request.genres"
                          :key="genre"
                          class="rounded border border-surface-200 px-1.5 py-px text-xs text-surface-600 dark:border-surface-700 dark:text-surface-300"
                        >
                          {{ getGenreText(genre) }}
                        </span>
                      </div>

                      <div class="flex flex-wrap items-center gap-x-3 gap-y-1 text-sm text-muted-color">
                        <span v-if="resolveLinkType(request.externalUrl, request.externalLinkType)" class="hidden md:flex items-center gap-1">
                          <i class="pi pi-external-link text-xs" />
                          {{ getLinkTypeText(resolveLinkType(request.externalUrl, request.externalLinkType)!) }}
                        </span>
                        <span
                          v-if="request.boostCount > 0"
                          v-tooltip.top="`${request.boostCount} boost${request.boostCount === 1 ? '' : 's'} (+${request.boostCount * 5} votes)`"
                          class="flex items-center gap-1 text-amber-600 dark:text-amber-400 font-medium"
                        >
                          <i class="pi pi-bolt text-xs" />
                          {{ request.boostCount }}
                        </span>
                        <span v-if="request.isOwnRequest && !isMine" class="flex items-center gap-1">
                          <i class="pi pi-user text-xs" />
                          You
                        </span>
                        <span v-else-if="request.requesterName && authStore.isAdmin" class="flex items-center gap-1">
                          <i class="pi pi-user text-xs" />
                          {{ request.requesterName }}
                        </span>
                        <span v-if="!request.completedAt">{{ formatTimeAgo(request.createdAt) }}</span>
                        <span v-if="request.completedAt" class="flex items-center gap-1">
                          <i class="pi pi-check-circle text-xs" />
                          Completed {{ formatCompletedAt(request.completedAt) }}
                        </span>
                      </div>
                    </div>

                    <div class="shrink-0 flex flex-col md:flex-row items-end md:items-center gap-2 md:gap-4">
                      <div v-if="request.commentCount > 0 || request.uploadCount > 0" class="flex items-center gap-3 text-sm text-muted-color">
                        <span v-if="request.commentCount > 0" class="flex items-center gap-1">
                          <i class="pi pi-comments text-xs" />
                          {{ request.commentCount }}
                        </span>
                        <span v-if="request.uploadCount > 0" class="flex items-center gap-1">
                          <i class="pi pi-paperclip text-xs" />
                          {{ request.uploadCount }}
                        </span>
                      </div>
                      <div class="flex flex-col md:flex-row items-center gap-2" @click.prevent>
                        <RequestBoostButton
                          :request-id="request.id"
                          :boost-count="request.boostCount"
                          :has-boosted="request.hasUserBoosted"
                          :boostable="isBoostable(request)"
                          :balance="boostBalance"
                          :auto-fetch-balance="false"
                          compact
                          @boosted="handleBoosted(request, $event)"
                        />
                        <RequestSubscribeButton :is-subscribed="request.isSubscribed" compact @toggle="handleSubscribe(request)" />
                      </div>
                    </div>
                  </div>
                </template>
              </Card>
            </NuxtLink>
          </TransitionGroup>
        </div>
      </template>

      <Paginator v-if="totalCount > limit" :rows="limit" :total-records="totalCount" :first="offset" class="mt-4" @page="onPageChange" />
    </template>
  </div>
</template>

<style>
  .request-list-enter-active,
  .request-list-leave-active {
    transition: opacity 0.2s ease;
  }
  .request-list-enter-from,
  .request-list-leave-to {
    opacity: 0;
  }
  .request-list-leave-active {
    position: absolute;
    width: 100%;
  }
  .request-list-move {
    transition: transform 0.2s ease;
  }
</style>
