<script setup lang="ts">
  import { useConfirm } from 'primevue/useconfirm';
  import { useToast } from 'primevue/usetoast';
  import { CommunityListStatus } from '~/types';
  import type { PaginatedResponse } from '~/types/types';
  import { debounce } from 'perfect-debounce';
  import { COMMUNITY_LIST_STATUS_TEXT as statusText, communityListPath, unlistConfirmation } from '~/composables/useCommunityLists';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { formatRelativeTime } from '~/utils/relativeTime';

  useHead({ title: 'Lists' });
  definePageMeta({ middleware: ['auth-admin'] });

  interface AdminReport {
    reportId: number;
    listId: number;
    listName: string;
    listSlug: string;
    listStatus: CommunityListStatus;
    reporterUserName: string | null;
    reason: string;
    createdAt: string;
    resolvedAt: string | null;
  }

  interface AdminList {
    listId: number;
    name: string;
    slug: string;
    ownerId: string;
    ownerUserName: string | null;
    ownerDisplayName: string | null;
    status: CommunityListStatus;
    itemCount: number;
    favouriteCount: number;
    views7Days: number;
    views30Days: number;
    viewsTotal: number;
    openReports: number;
    createdAt: string;
    updatedAt: string;
  }

  const PAGE_SIZE = 50;

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const tab = ref<'reports' | 'lists'>('reports');
  const showResolved = ref(false);
  const reports = ref<PaginatedResponse<AdminReport[]> | null>(null);
  const reportOffset = ref(0);
  const allLists = ref<PaginatedResponse<AdminList[]> | null>(null);
  const listOffset = ref(0);
  const listSearch = ref('');
  const listStatus = ref<CommunityListStatus | null>(null);
  const busy = ref(false);

  async function loadReports() {
    reports.value = await $api<PaginatedResponse<AdminReport[]>>('admin/list-reports', {
      query: { resolved: showResolved.value, offset: reportOffset.value, limit: PAGE_SIZE },
    });
  }

  async function loadLists() {
    allLists.value = await $api<PaginatedResponse<AdminList[]>>('admin/community-lists', {
      query: { q: listSearch.value.trim() || undefined, status: listStatus.value ?? undefined, offset: listOffset.value, limit: PAGE_SIZE },
    });
  }

  async function run(action: () => Promise<unknown>, failure: string) {
    busy.value = true;
    try {
      await action();
      await Promise.all([loadReports(), loadLists()]);
    } catch (e) {
      toast.add({ severity: 'error', summary: apiErrorMessage(e, failure), life: 5000 });
    } finally {
      busy.value = false;
    }
  }

  const dismiss = (report: AdminReport) => run(() => $api(`admin/list-reports/${report.reportId}/dismiss`, { method: 'POST' }), 'Could not dismiss the report');
  function unlist(listId: number) {
    confirm.require(unlistConfirmation(() => run(() => $api(`admin/community-lists/${listId}/unlist`, { method: 'POST' }), 'Could not unlist the list')));
  }
  const restore = (listId: number) => run(() => $api(`admin/community-lists/${listId}/restore`, { method: 'POST' }), 'Could not restore the list');
  const recount = () => run(() => $api('admin/community-lists/recount', { method: 'POST' }), 'Could not recount');

  function remove(listId: number, name: string) {
    confirm.require({
      header: 'Delete this list?',
      message: `"${name}" will be deleted for everyone, with its favourites and reports.`,
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Delete', severity: 'danger' },
      accept: () => run(() => $api(`lists/${listId}`, { method: 'DELETE' }), 'Could not delete the list'),
    });
  }

  watch(showResolved, () => {
    reportOffset.value = 0;
    loadReports();
  });
  watch(reportOffset, loadReports);
  watch(
    listSearch,
    debounce(() => {
      listOffset.value = 0;
      loadLists();
    }, 300)
  );
  watch([listStatus, listOffset], loadLists);

  onMounted(() => Promise.all([loadReports(), loadLists()]));
</script>

<template>
  <div class="container mx-auto flex flex-col gap-4 p-2 md:p-4">
    <div class="flex flex-wrap items-center justify-between gap-3">
      <h1 class="m-0 text-2xl font-bold">Community lists</h1>
      <Button label="Recount favourites and titles" icon="pi pi-refresh" severity="secondary" outlined size="small" :loading="busy" @click="recount" />
    </div>

    <Tabs v-model:value="tab" :show-navigators="false">
      <TabList>
        <Tab value="reports">Reports</Tab>
        <Tab value="lists">All lists</Tab>
      </TabList>
    </Tabs>

    <template v-if="tab === 'reports'">
      <div class="flex items-center gap-2">
        <Checkbox v-model="showResolved" input-id="showResolved" binary />
        <label for="showResolved" class="cursor-pointer text-sm">Show resolved reports</label>
      </div>
      <DataTable :value="reports?.data ?? []" :loading="!reports" data-key="reportId" size="small" striped-rows>
        <template #empty>{{ showResolved ? 'No resolved reports.' : 'No open reports.' }}</template>
        <Column header="List">
          <template #body="{ data }">
            <NuxtLink :to="communityListPath({ listId: data.listId, slug: data.listSlug })" class="font-semibold">{{ data.listName }}</NuxtLink>
            <Tag
              v-if="data.listStatus !== CommunityListStatus.Public"
              :value="statusText[data.listStatus as CommunityListStatus]"
              severity="secondary"
              class="ml-2"
            />
          </template>
        </Column>
        <Column field="reason" header="Reason" class="max-w-md whitespace-pre-line" />
        <Column header="Reporter">
          <template #body="{ data }">{{ data.reporterUserName ?? 'Deleted user' }}</template>
        </Column>
        <Column header="Reported">
          <template #body="{ data }">{{ formatRelativeTime(data.createdAt) }}</template>
        </Column>
        <Column v-if="!showResolved" header="">
          <template #body="{ data }">
            <div class="flex justify-end gap-1">
              <Button label="Dismiss" size="small" severity="secondary" text :disabled="busy" @click="dismiss(data)" />
              <Button
                v-if="data.listStatus !== CommunityListStatus.Unlisted"
                label="Unlist"
                size="small"
                severity="danger"
                text
                :disabled="busy"
                @click="unlist(data.listId)"
              />
              <Button label="Delete" size="small" severity="danger" text :disabled="busy" @click="remove(data.listId, data.listName)" />
            </div>
          </template>
        </Column>
      </DataTable>
      <Paginator
        v-if="(reports?.totalItems ?? 0) > PAGE_SIZE"
        :rows="PAGE_SIZE"
        :total-records="reports!.totalItems"
        :first="reportOffset"
        @page="reportOffset = $event.first"
      />
    </template>

    <template v-else>
      <div class="flex flex-col gap-3 sm:flex-row">
        <SearchInput v-model="listSearch" placeholder="Search by name..." aria-label="Search lists" class="min-w-0 flex-1" />
        <Select
          v-model="listStatus"
          :options="[
            { label: 'All statuses', value: null },
            { label: 'Public', value: CommunityListStatus.Public },
            { label: 'Draft', value: CommunityListStatus.Draft },
            { label: 'Unlisted', value: CommunityListStatus.Unlisted },
          ]"
          option-label="label"
          option-value="value"
          aria-label="Status"
          class="sm:w-48"
        />
      </div>
      <DataTable :value="allLists?.data ?? []" :loading="!allLists" data-key="listId" size="small" striped-rows scrollable>
        <template #empty>No lists match.</template>
        <Column header="List">
          <template #body="{ data }">
            <NuxtLink :to="communityListPath(data)" class="font-semibold">{{ data.name }}</NuxtLink>
            <div class="text-xs text-muted-color">{{ data.ownerDisplayName ?? '(no display name)' }}, {{ data.ownerUserName }}</div>
          </template>
        </Column>
        <Column header="Status">
          <template #body="{ data }">{{ statusText[data.status as CommunityListStatus] }}</template>
        </Column>
        <Column field="itemCount" header="Titles" />
        <Column field="favouriteCount" header="Favourites" />
        <Column header="Views 7d / 30d / all">
          <template #body="{ data }"
            ><span class="tabular-nums">{{ data.views7Days }} / {{ data.views30Days }} / {{ data.viewsTotal }}</span></template
          >
        </Column>
        <Column field="openReports" header="Open reports" />
        <Column header="Updated">
          <template #body="{ data }">{{ formatRelativeTime(data.updatedAt) }}</template>
        </Column>
        <Column header="">
          <template #body="{ data }">
            <div class="flex justify-end gap-1">
              <Button
                v-if="data.status === CommunityListStatus.Unlisted"
                label="Restore"
                size="small"
                severity="secondary"
                text
                :disabled="busy"
                @click="restore(data.listId)"
              />
              <Button
                v-else-if="data.status === CommunityListStatus.Public"
                label="Unlist"
                size="small"
                severity="danger"
                text
                :disabled="busy"
                @click="unlist(data.listId)"
              />
              <Button label="Delete" size="small" severity="danger" text :disabled="busy" @click="remove(data.listId, data.name)" />
            </div>
          </template>
        </Column>
      </DataTable>
      <Paginator
        v-if="(allLists?.totalItems ?? 0) > PAGE_SIZE"
        :rows="PAGE_SIZE"
        :total-records="allLists!.totalItems"
        :first="listOffset"
        @page="listOffset = $event.first"
      />
    </template>
  </div>
</template>
