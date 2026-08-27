<script setup lang="ts">
  import { RequestAction } from '~/types';
  import type { RequestActivityLogDto, RequestUserSummaryDto } from '~/types/types';
  import { getRequestActionText, getRequestActionIcon, getRequestActionSeverity } from '~/utils/requestActionMapper';

  useHead({
    title: 'Request Activity - Jiten Admin',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  const activeTab = ref(0);

  // Activity Log tab state
  const { fetchGlobalActivityLog, fetchUserSummary } = useMediaRequests();

  const activityLog = ref<RequestActivityLogDto[]>([]);
  const activityTotalCount = ref(0);
  const activityLoading = ref(false);
  const activityOffset = ref(0);
  const activityLimit = 25;
  const filterUserId = ref('');
  const filterAction = ref<RequestAction | undefined>(undefined);

  const actionOptions = [
    { label: 'All actions', value: undefined },
    ...Object.entries(RequestAction)
      .filter(([key]) => isNaN(Number(key)))
      .map(([key, value]) => ({
        label: getRequestActionText(value as RequestAction),
        value: value as RequestAction,
      })),
  ];

  async function loadActivityLog() {
    activityLoading.value = true;
    const result = await fetchGlobalActivityLog({
      userId: filterUserId.value || undefined,
      action: filterAction.value,
      offset: activityOffset.value,
      limit: activityLimit,
    });
    activityLog.value = result?.data ?? [];
    activityTotalCount.value = result?.totalItems ?? 0;
    activityLoading.value = false;
  }

  function onActivityPage(event: { first: number }) {
    activityOffset.value = event.first;
    loadActivityLog();
  }

  function applyFilters() {
    activityOffset.value = 0;
    loadActivityLog();
  }

  // User Summary tab state
  const summaryUserId = ref('');
  const userSummary = ref<RequestUserSummaryDto | null>(null);
  const summaryLoading = ref(false);

  async function lookUpUser() {
    if (!summaryUserId.value.trim()) return;
    summaryLoading.value = true;
    userSummary.value = await fetchUserSummary(summaryUserId.value.trim());
    summaryLoading.value = false;
  }

  function formatDetail(action: RequestAction, detail: string | null): string {
    if (!detail) return '';
    try {
      const d = JSON.parse(detail);
      switch (action) {
        case RequestAction.RequestCreated:
          return [d.Title, d.mediaType, d.ExternalUrl].filter(Boolean).join(' | ');
        case RequestAction.RequestDeleted:
          return d.Title ?? '';
        case RequestAction.CommentAdded:
        case RequestAction.CommentEdited:
          return d.textPreview ?? '';
        case RequestAction.FileUploaded:
          return d.fileName ? `${d.fileName} (${formatFileSize(d.fileSize)})` : '';
        case RequestAction.FileDeletedByAdmin:
        case RequestAction.ContributionValidated:
        case RequestAction.ContributionRevoked:
          return d.FileName ?? '';
        case RequestAction.StatusChangedToCompleted:
          return [d.deckId ? `Deck #${d.deckId}` : null, d.AdminNote].filter(Boolean).join(' - ');
        case RequestAction.StatusChangedToRejected:
          return d.AdminNote ?? '';
        case RequestAction.RequestEditedByAdmin:
          return d.oldTitle && d.newTitle && d.oldTitle !== d.newTitle ? `"${d.oldTitle}" → "${d.newTitle}"` : '';
        default:
          return detail;
      }
    } catch {
      return detail;
    }
  }

  function formatFileSize(bytes: number | null): string {
    if (!bytes) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1048576).toFixed(1)} MB`;
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
    return date.toLocaleDateString();
  }

  onMounted(() => loadActivityLog());
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center mb-6">
      <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
      <h1 class="text-3xl font-bold">Request Activity</h1>
    </div>

    <Tabs v-model:value="activeTab" :show-navigators="false">
      <TabList>
        <Tab :value="0">Activity Log</Tab>
        <Tab :value="1">User Summary</Tab>
      </TabList>

      <TabPanels>
        <TabPanel :value="0">
          <div class="flex gap-3 items-end flex-wrap mb-4 mt-4">
            <div class="flex flex-col gap-1">
              <label class="text-sm font-semibold">User ID</label>
              <InputText v-model="filterUserId" placeholder="Filter by user ID" class="w-48" @keyup.enter="applyFilters" />
            </div>
            <div class="flex flex-col gap-1">
              <label class="text-sm font-semibold">Action</label>
              <Select v-model="filterAction" :options="actionOptions" option-label="label" option-value="value" placeholder="All actions" class="w-56" />
            </div>
            <Button label="Filter" icon="pi pi-filter" @click="applyFilters" />
          </div>

          <DataTable :value="activityLog" :loading="activityLoading" striped-rows class="shadow-md">
            <Column header="Time" style="width: 120px">
              <template #body="{ data }">
                <span class="text-sm">{{ formatTimeAgo(data.createdAt) }}</span>
              </template>
            </Column>
            <Column header="Request" style="min-width: 200px">
              <template #body="{ data }">
                <NuxtLink v-if="data.mediaRequestId" :to="`/requests/${data.mediaRequestId}`" class="text-blue-500 hover:underline">
                  {{ data.requestTitle || `#${data.mediaRequestId}` }}
                </NuxtLink>
              </template>
            </Column>
            <Column header="User" style="width: 180px">
              <template #body="{ data }">
                <span v-if="data.userName" class="text-sm truncate block max-w-[160px]" :title="data.userId">{{ data.userName }}</span>
                <span v-else class="text-sm font-mono truncate block max-w-[160px]" :title="data.userId">{{ data.userId.slice(0, 8) }}</span>
              </template>
            </Column>
            <Column header="Action" style="width: 200px">
              <template #body="{ data }">
                <div class="flex items-center gap-2">
                  <i :class="getRequestActionIcon(data.action)" class="text-xs" />
                  <Tag :value="getRequestActionText(data.action)" :severity="getRequestActionSeverity(data.action)" class="text-xs" />
                </div>
              </template>
            </Column>
            <Column header="Detail" style="min-width: 200px">
              <template #body="{ data }">
                <span v-if="data.detail" class="text-sm text-muted-color block max-w-md" :title="data.detail">
                  {{ formatDetail(data.action, data.detail) }}
                </span>
              </template>
            </Column>
          </DataTable>

          <Paginator :first="activityOffset" :rows="activityLimit" :total-records="activityTotalCount" class="mt-4" @page="onActivityPage" />
        </TabPanel>

        <TabPanel :value="1">
          <div class="flex gap-3 items-end mt-4 mb-6">
            <div class="flex flex-col gap-1">
              <label class="text-sm font-semibold">User ID</label>
              <InputText v-model="summaryUserId" placeholder="Enter user ID" class="w-64" @keyup.enter="lookUpUser" />
            </div>
            <Button label="Look up" icon="pi pi-search" :loading="summaryLoading" @click="lookUpUser" />
          </div>

          <div v-if="userSummary" class="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-5 gap-4">
            <Card class="shadow-md">
              <template #title>
                <div class="flex items-center gap-2">
                  <i class="pi pi-list text-blue-500" />
                  Requests
                </div>
              </template>
              <template #content>
                <div class="text-4xl font-bold text-center">{{ userSummary.requestCount }}</div>
              </template>
            </Card>
            <Card class="shadow-md">
              <template #title>
                <div class="flex items-center gap-2">
                  <i class="pi pi-thumbs-up text-green-500" />
                  Upvotes
                </div>
              </template>
              <template #content>
                <div class="text-4xl font-bold text-center">{{ userSummary.upvoteCount }}</div>
              </template>
            </Card>
            <Card class="shadow-md">
              <template #title>
                <div class="flex items-center gap-2">
                  <i class="pi pi-bolt text-amber-500" />
                  Boosts
                </div>
              </template>
              <template #content>
                <div class="text-4xl font-bold text-center">{{ userSummary.boostCount }}</div>
              </template>
            </Card>
            <Card class="shadow-md">
              <template #title>
                <div class="flex items-center gap-2">
                  <i class="pi pi-bell text-orange-500" />
                  Subscriptions
                </div>
              </template>
              <template #content>
                <div class="text-4xl font-bold text-center">{{ userSummary.subscriptionCount }}</div>
              </template>
            </Card>
            <Card class="shadow-md">
              <template #title>
                <div class="flex items-center gap-2">
                  <i class="pi pi-upload text-purple-500" />
                  Uploads
                </div>
              </template>
              <template #content>
                <div class="text-4xl font-bold text-center">{{ userSummary.uploadCount }}</div>
              </template>
            </Card>
            <Card class="shadow-md">
              <template #title>
                <div class="flex items-center gap-2">
                  <i class="pi pi-check-circle text-teal-500" />
                  Fulfilled
                </div>
              </template>
              <template #content>
                <div class="text-4xl font-bold text-center">{{ userSummary.fulfilledCount }}</div>
              </template>
            </Card>
          </div>
        </TabPanel>
      </TabPanels>
    </Tabs>
  </div>
</template>
