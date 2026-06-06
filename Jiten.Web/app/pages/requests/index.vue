<script setup lang="ts">
import { MediaType, RequestStatus } from '~/types';
import type { MediaRequestDto } from '~/types/types';
import { getMediaTypeText } from '~/utils/mediaTypeMapper';
import { getRequestStatusText, getRequestStatusSeverity } from '~/utils/requestStatusMapper';
import { getLinkTypeText } from '~/utils/linkTypeMapper';

definePageMeta({
  middleware: ['auth'],
});

useHead({ title: 'Media Requests - Jiten' });

const { requests, totalCount, isLoading, fetchRequests, toggleUpvote, subscribe, unsubscribe, fetchMyQuota } = useMediaRequests();

const authStore = useAuthStore();
const quota = ref<{ activeCount: number; limit: number } | null>(null);
const toast = useToast();
const router = useRouter();
const route = useRoute();

const limit = 20;

function parseTabFromQuery() {
  if (route.query.tab === 'mine') return 1;
  if (route.query.tab === 'contributions') return 2;
  return 0;
}
function parseTypeFromQuery() { return route.query.type !== undefined ? Number(route.query.type) as MediaType : undefined; }
function parseStatusFromQuery() {
  if (route.query.status === 'all') return undefined;
  if (route.query.status !== undefined) return Number(route.query.status) as RequestStatus;
  return parseTabFromQuery() === 0 ? RequestStatus.Open : undefined;
}
function parseSortFromQuery() { return typeof route.query.sort === 'string' ? route.query.sort : 'votes'; }
function parseOffsetFromQuery() { return route.query.page ? (Number(route.query.page) - 1) * limit : 0; }

const activeTab = ref(parseTabFromQuery());
const selectedMediaType = ref<MediaType | undefined>(parseTypeFromQuery());
const selectedStatus = ref<RequestStatus | undefined>(parseStatusFromQuery());
const sortBy = ref(parseSortFromQuery());
const offset = ref(parseOffsetFromQuery());

const mediaTypeOptions = [
  { label: 'All', value: undefined },
  ...Object.values(MediaType)
    .filter(v => typeof v === 'number')
    .map(v => ({ label: getMediaTypeText(v as MediaType), value: v as MediaType })),
];

const statusOptions = [
  { label: 'All', value: undefined },
  { label: 'Open', value: RequestStatus.Open },
  { label: 'In Progress', value: RequestStatus.InProgress },
  { label: 'Completed', value: RequestStatus.Completed },
  { label: 'Rejected', value: RequestStatus.Rejected },
];

const sortOptions = [
  { label: 'Most Voted', value: 'votes' },
  { label: 'Newest', value: 'recent' },
  { label: 'Last Completed', value: 'completed' },
];

const attachmentOptions = [
  { label: 'All', value: undefined },
  { label: 'Has Attachments', value: 'yes' },
  { label: 'No Attachments', value: 'no' },
];

function parseAttachmentsFromQuery() {
  const v = route.query.attachments;
  return v === 'yes' || v === 'no' ? v : undefined;
}

const selectedAttachments = ref<string | undefined>(parseAttachmentsFromQuery());

const searchQuery = ref(typeof route.query.search === 'string' ? route.query.search : '');
const debouncedSearch = ref(searchQuery.value);
let searchTimeout: ReturnType<typeof setTimeout>;
watch(searchQuery, (val) => {
  clearTimeout(searchTimeout);
  searchTimeout = setTimeout(() => { debouncedSearch.value = val; }, 300);
});

const isMine = computed(() => activeTab.value === 1);
const isContributed = computed(() => activeTab.value === 2);
const isPersonalTab = computed(() => activeTab.value === 1 || activeTab.value === 2);

const displaySections = computed(() => {
  return [{ title: '', items: requests.value, muted: false }];
});

async function loadRequests() {
  const search = debouncedSearch.value.trim() || undefined;
  if (isContributed.value) {
    await fetchRequests({
      mediaType: selectedMediaType.value,
      status: selectedStatus.value,
      sort: sortBy.value,
      offset: 0,
      limit: 200,
      contributed: true,
      search,
      attachments: selectedAttachments.value,
    });
  } else if (isMine.value) {
    await fetchRequests({
      mediaType: selectedMediaType.value,
      status: selectedStatus.value,
      sort: sortBy.value,
      offset: 0,
      limit: 200,
      mine: true,
      search,
      attachments: selectedAttachments.value,
    });
  } else {
    await fetchRequests({
      mediaType: selectedMediaType.value,
      status: selectedStatus.value,
      sort: sortBy.value,
      offset: offset.value,
      limit,
      mine: false,
      search,
      attachments: selectedAttachments.value,
    });
  }
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

watch([selectedMediaType, selectedStatus, sortBy, activeTab, debouncedSearch, selectedAttachments], () => {
  offset.value = 0;
  loadRequests();
  if (activeTab.value === 1) {
    fetchMyQuota().then(q => { quota.value = q; });
  }
});

watch(offset, () => loadRequests());

watch([activeTab, selectedMediaType, selectedStatus, sortBy, offset, debouncedSearch, selectedAttachments], () => {
  const query: Record<string, string> = {};
  if (activeTab.value === 1) query.tab = 'mine';
  else if (activeTab.value === 2) query.tab = 'contributions';
  if (selectedMediaType.value !== undefined) query.type = String(selectedMediaType.value);
  if (debouncedSearch.value.trim()) query.search = debouncedSearch.value.trim();
  if (selectedAttachments.value) query.attachments = selectedAttachments.value;
  if (selectedStatus.value === undefined) query.status = 'all';
  else if (selectedStatus.value !== RequestStatus.Open) query.status = String(selectedStatus.value);
  if (sortBy.value !== 'votes') query.sort = sortBy.value;
  if (!isPersonalTab.value && offset.value > 0) query.page = String(offset.value / limit + 1);
  router.replace({ query });
});

async function handleUpvote(request: MediaRequestDto) {
  const result = await toggleUpvote(request.id);
  if (result) {
    request.hasUserUpvoted = result.upvoted;
    request.upvoteCount = result.upvoteCount;
    if (result.upvoted) {
      request.isSubscribed = true;
    }
  }
}

async function handleSubscribe(request: MediaRequestDto) {
  if (request.isSubscribed) {
    const success = await unsubscribe(request.id);
    if (success) request.isSubscribed = false;
  } else {
    const success = await subscribe(request.id);
    if (success) request.isSubscribed = true;
  }
}

function formatCompletedAt(dateString: string): string {
  const date = new Date(dateString);
  const now = new Date();
  const diffDays = Math.floor((now.getTime() - date.getTime()) / 86400000);
  if (diffDays < 30) return `${diffDays || 1}d ago`;
  return `on ${date.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: date.getFullYear() !== now.getFullYear() ? 'numeric' : undefined })}`;
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

function onPageChange(event: { first: number }) {
  offset.value = event.first;
}

onMounted(() => loadRequests());
</script>

<template>
  <div class="container mx-auto p-2 md:p-4">
    <div class="flex items-center justify-between mb-6">
      <h1 class="text-2xl font-bold">Media Requests</h1>
      <Button
        label="New Request"
        icon="pi pi-plus"
        @click="router.push('/requests/new')"
      />
    </div>

    <Tabs v-model:value="activeTab">
      <TabList>
        <Tab :value="0">All Requests</Tab>
        <Tab :value="1">My Requests</Tab>
        <Tab :value="2">My Contributions</Tab>
      </TabList>
    </Tabs>

    <div class="flex flex-wrap gap-3 my-4 items-end">
      <div class="flex flex-col gap-1">
        <label class="text-sm text-muted-color">Search</label>
        <IconField>
          <InputIcon class="pi pi-search" />
          <InputText v-model="searchQuery" placeholder="Search titles..." class="w-56" />
          <InputIcon v-if="searchQuery" class="pi pi-times cursor-pointer" @click="searchQuery = ''" />
        </IconField>
      </div>
      <div class="flex flex-col gap-1">
        <label class="text-sm text-muted-color">Media Type</label>
        <Select
          v-model="selectedMediaType"
          :options="mediaTypeOptions"
          optionLabel="label"
          optionValue="value"
          placeholder="Media Type"
          class="w-40"
        />
      </div>
      <div class="flex flex-col gap-1">
        <label class="text-sm text-muted-color">Status</label>
        <Select
          v-model="selectedStatus"
          :options="statusOptions"
          optionLabel="label"
          optionValue="value"
          placeholder="Status"
          class="w-40"
        />
      </div>
      <div class="flex flex-col gap-1">
        <label class="text-sm text-muted-color">Sort</label>
        <Select
          v-model="sortBy"
          :options="sortOptions"
          optionLabel="label"
          optionValue="value"
          class="w-40"
        />
      </div>
      <div class="flex flex-col gap-1">
        <label class="text-sm text-muted-color">Attachments</label>
        <Select
          v-model="selectedAttachments"
          :options="attachmentOptions"
          optionLabel="label"
          optionValue="value"
          placeholder="Attachments"
          class="w-44"
        />
      </div>
    </div>

    <div v-if="isLoading" class="flex justify-center py-12">
      <ProgressSpinner style="width: 50px; height: 50px" />
    </div>

    <div v-else-if="requests.length === 0" class="text-center py-12 text-muted-color">
      <i class="pi pi-inbox text-4xl mb-3" />
      <template v-if="isMine">
        <p>You haven't made any requests yet.</p>
        <Button
          label="Make a request"
          icon="pi pi-arrow-right"
          iconPos="right"
          text
          class="mt-2"
          @click="router.push('/requests/new')"
        />
      </template>
      <p v-else-if="isContributed">You haven't contributed to any requests yet.</p>
      <p v-else>No requests found. Be the first!</p>
    </div>

    <template v-else>
      <div v-if="isMine && quota" class="mb-3">
        <Tag :value="`${quota.activeCount} / ${quota.limit} active slots used`" severity="secondary" />
      </div>

      <template v-for="section in displaySections" :key="section.title">
        <div v-if="section.items.length > 0" class="mb-6" :class="{ 'opacity-50': section.muted }">
          <h3 v-if="section.title" class="text-lg font-semibold mb-3 flex items-center gap-2">
            {{ section.title }}
            <span class="text-sm font-normal text-muted-color">({{ section.items.length }})</span>
          </h3>
          <TransitionGroup name="request-list" tag="div" class="flex flex-col gap-3">
            <NuxtLink
              v-for="request in section.items"
              :key="request.id"
              :to="`/requests/${request.id}`"
              class="no-underline! text-inherit"
            >
              <Card class="shadow-sm cursor-pointer hover:bg-surface-50 dark:hover:bg-surface-800 transition-colors">
                <template #content>
                  <div class="flex items-start gap-2 md:gap-4">
                    <div @click.prevent>
                      <UpvoteButton
                        :has-upvoted="request.hasUserUpvoted"
                        :upvote-count="request.upvoteCount"
                        compact
                        @toggle="handleUpvote(request)"
                      />
                    </div>

                    <div class="flex-1 min-w-0">
                      <div class="flex items-center gap-2 flex-wrap mb-1">
                        <span class="font-semibold text-lg">{{ request.title }}</span>
                        <Tag :value="getMediaTypeText(request.mediaType)" severity="secondary" />
                        <Tag
                          :value="getRequestStatusText(request.status)"
                          :severity="getRequestStatusSeverity(request.status)"
                        />
                        <span v-if="request.isOwnRequest && !isMine" class="text-xs text-muted-color italic">
                          Your request
                        </span>
                      </div>

                      <div class="flex items-center gap-3 text-sm text-muted-color mt-1">
                        <span v-if="request.externalLinkType" class="hidden md:flex items-center gap-1">
                          <i class="pi pi-external-link text-xs" />
                          {{ getLinkTypeText(request.externalLinkType) }}
                        </span>
                        <span v-if="request.commentCount > 0" class="flex items-center gap-1">
                          <i class="pi pi-comments text-xs" />
                          {{ request.commentCount }}
                        </span>
                        <span v-if="request.uploadCount > 0" class="flex items-center gap-1">
                          <i class="pi pi-paperclip text-xs" />
                          {{ request.uploadCount }}
                        </span>
                        <span v-if="request.requesterName && authStore.isAdmin" class="flex items-center gap-1">
                          <i class="pi pi-user text-xs" />
                          {{ request.requesterName }}
                        </span>
                        <span>{{ formatTimeAgo(request.createdAt) }}</span>
                        <span v-if="request.completedAt" class="hidden md:flex items-center gap-1">
                          <i class="pi pi-check-circle text-xs" />
                          completed {{ formatCompletedAt(request.completedAt) }}
                        </span>
                      </div>
                    </div>

                    <div class="shrink-0 hidden md:block" @click.prevent>
                      <RequestSubscribeButton
                        :is-subscribed="request.isSubscribed"
                        compact
                        @toggle="handleSubscribe(request)"
                      />
                    </div>
                  </div>
                </template>
              </Card>
            </NuxtLink>
          </TransitionGroup>
        </div>
      </template>

      <Paginator
        v-if="!isPersonalTab && totalCount > limit"
        :rows="limit"
        :totalRecords="totalCount"
        :first="offset"
        class="mt-4"
        @page="onPageChange"
      />
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
