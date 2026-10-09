<script setup lang="ts">
  import { ref, computed, watch, onMounted } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import Dialog from 'primevue/dialog';
  import AutoComplete from 'primevue/autocomplete';
  import Select from 'primevue/select';
  import Message from 'primevue/message';
  import DataTable from 'primevue/datatable';
  import Column from 'primevue/column';
  import Tag from 'primevue/tag';
  import ProgressSpinner from 'primevue/progressspinner';
  import Tabs from 'primevue/tabs';
  import TabList from 'primevue/tablist';
  import Tab from 'primevue/tab';
  import TabPanels from 'primevue/tabpanels';
  import TabPanel from 'primevue/tabpanel';
  import FranchisesTab from '~/components/dashboard/FranchisesTab.vue';
  import { debounce } from 'perfect-debounce';
  import { SeriesKind, type SeriesDetail, type SeriesRef, type SeriesSummary } from '~/types/series';
  import type { FranchiseNode, PaginatedResponse } from '~/types/types';
  import { MediaGroupKind } from '~/types/mediaGroup';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';
  import { seriesKindLabel } from '~/utils/seriesKind';

  definePageMeta({ middleware: ['auth-admin'] });
  useHead({ title: 'Series and franchises - Admin Dashboard - Jiten' });

  const route = useRoute();
  const router = useRouter();
  const activeTab = computed<string>({
    get: () => (route.query.tab === 'franchises' ? 'franchises' : 'series'),
    set: (value) => {
      router.replace({ query: { ...route.query, tab: value === 'franchises' ? 'franchises' : undefined } });
    },
  });

  const PAGE_SIZE = 25;
  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const localiseTitle = useLocaliseTitle();

  const notify = (severity: 'success' | 'error' | 'warn', detail: string) =>
    toast.add({ severity, summary: severity === 'success' ? 'Done' : 'Not done', detail, life: severity === 'success' ? 3000 : 6000 });

  const query = ref('');
  const kindFilter = ref<SeriesKind | null>(null);
  const kindOptions = [
    { value: null, label: 'Series and settings' },
    { value: SeriesKind.Series, label: 'Series only' },
    { value: SeriesKind.Setting, label: 'Settings only' },
  ];
  const rows = ref<SeriesSummary[]>([]);
  const total = ref(0);
  const offset = ref(0);
  const loading = ref(false);
  const listError = ref('');

  async function loadList() {
    loading.value = true;
    listError.value = '';
    try {
      const res = await $api<PaginatedResponse<SeriesSummary[]>>('admin/series', {
        query: { query: query.value.trim() || undefined, kind: kindFilter.value ?? undefined, offset: offset.value, limit: PAGE_SIZE },
      });
      rows.value = res.data;
      total.value = res.totalItems;
    } catch (e) {
      listError.value = apiErrorMessage(e, 'The series list could not be loaded.');
    } finally {
      loading.value = false;
    }
  }

  const reloadFromStart = debounce(() => {
    offset.value = 0;
    loadList();
  }, 300);
  watch([query, kindFilter], reloadFromStart);

  function onPage(ev: { first: number }) {
    offset.value = ev.first;
    loadList();
  }

  onMounted(loadList);

  const createOpen = ref(false);
  const createName = ref('');
  const createKind = ref<SeriesKind>(SeriesKind.Series);
  const { error: createError, busy: creating, create } = useSeriesCreate();

  function openCreate() {
    createName.value = '';
    createKind.value = SeriesKind.Series;
    createError.value = '';
    createOpen.value = true;
  }

  async function submitCreate() {
    const created = await create(createName.value, createKind.value, 'It could not be created.');
    if (!created) return;
    createOpen.value = false;
    notify('success', `Created ${created.name}`);
    await loadList();
    openDetail(created.seriesId);
  }

  const detailId = ref<number | null>(null);
  const detail = ref<SeriesDetail | null>(null);
  const detailLoading = ref(false);
  const detailError = ref('');
  const renameValue = ref('');
  const detailBusy = ref(false);
  const confirmRemove = ref<number | null>(null);

  const sortedMembers = computed(() => [...(detail.value?.members ?? [])].sort((a, b) => localiseTitle(a).localeCompare(localiseTitle(b))));

  async function openDetail(id: number) {
    detailId.value = id;
    detail.value = null;
    detailError.value = '';
    confirmRemove.value = null;
    detailLoading.value = true;
    try {
      const loaded = await $api<SeriesDetail>(`admin/series/${id}`);
      if (detailId.value !== id) return;
      detail.value = loaded;
      renameValue.value = loaded.name;
    } catch (e) {
      detailError.value = apiErrorMessage(e, 'This series could not be loaded.');
    } finally {
      detailLoading.value = false;
    }
  }

  function closeDetail() {
    detailId.value = null;
    detail.value = null;
  }

  async function patchDetail(body: Record<string, unknown>, success: string) {
    if (!detail.value) return;
    detailBusy.value = true;
    detailError.value = '';
    try {
      await $api<SeriesRef>(`admin/series/${detail.value.seriesId}`, { method: 'PATCH', body });
      notify('success', success);
      await Promise.all([openDetail(detail.value.seriesId), loadList()]);
    } catch (e) {
      detailError.value = apiErrorMessage(e, 'The change was not saved.');
    } finally {
      detailBusy.value = false;
    }
  }

  function rename() {
    const name = renameValue.value.trim();
    if (!name || name === detail.value?.name) return;
    patchDetail({ name }, `Renamed to ${name}`);
  }

  async function removeMember(member: FranchiseNode) {
    if (!detail.value) return;
    detailBusy.value = true;
    detailError.value = '';
    try {
      await $api(`admin/series/${detail.value.seriesId}/members/remove`, { method: 'POST', body: { deckIds: [member.deckId] } });
      notify('success', `Removed ${localiseTitle(member)}`);
      await Promise.all([openDetail(detail.value.seriesId), loadList()]);
    } catch (e) {
      detailError.value = apiErrorMessage(e, 'The deck could not be removed.');
    } finally {
      detailBusy.value = false;
      confirmRemove.value = null;
    }
  }

  const mergeSource = ref<SeriesSummary | null>(null);
  const { suggestions: mergeSuggestions, fetchSuggestions: searchMergeTargets } = useSeriesSuggestions(() => mergeSource.value?.kind ?? SeriesKind.Series);
  const mergeTarget = ref<SeriesSummary | string | null>(null);
  const mergeError = ref('');
  const merging = ref(false);

  function openMerge(row: SeriesSummary) {
    mergeSource.value = row;
    mergeTarget.value = null;
    mergeError.value = '';
  }

  async function submitMerge() {
    const src = mergeSource.value;
    const target = mergeTarget.value;
    if (!src || !target || typeof target !== 'object') return;
    merging.value = true;
    mergeError.value = '';
    try {
      await $api(`admin/series/${src.seriesId}/merge-into/${target.seriesId}`, { method: 'POST' });
      notify('success', `Merged ${src.name} into ${target.name}`);
      mergeSource.value = null;
      if (detailId.value === src.seriesId) openDetail(target.seriesId);
      await loadList();
    } catch (e) {
      mergeError.value = apiErrorMessage(e, 'The merge failed. Nothing was changed.');
    } finally {
      merging.value = false;
    }
  }

  function askDelete(row: SeriesSummary) {
    confirm.require({
      header: `Delete ${row.name}?`,
      message: `${row.deckCount} ${row.deckCount === 1 ? 'deck' : 'decks'} will leave this ${seriesKindLabel(row.kind).toLowerCase()}. The decks themselves stay.`,
      acceptLabel: 'Delete',
      rejectLabel: 'Keep',
      acceptClass: 'p-button-danger',
      accept: () => deleteSeries(row),
    });
  }

  async function deleteSeries(row: SeriesSummary) {
    try {
      await $api(`admin/series/${row.seriesId}`, { method: 'DELETE' });
      notify('success', `Deleted ${row.name}`);
      if (detailId.value === row.seriesId) closeDetail();
      await loadList();
    } catch (e) {
      notify('error', apiErrorMessage(e, 'It could not be deleted.'));
    }
  }
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="mb-6 flex flex-wrap items-center justify-between gap-3">
      <div class="flex items-center">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" aria-label="Back to the dashboard" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Series and franchises</h1>
      </div>
    </div>

    <Tabs v-model:value="activeTab" :show-navigators="false">
      <TabList :pt="{ root: { class: 'bg-transparent!' }, tabList: { class: 'bg-transparent!' } }">
        <Tab value="series">Series and settings</Tab>
        <Tab value="franchises">Franchises</Tab>
      </TabList>
      <TabPanels class="bg-transparent! px-0! pt-5!">
        <TabPanel value="series">
          <div class="mb-4 flex flex-wrap gap-2">
            <SearchInput v-model="query" placeholder="Search by name" aria-label="Search series and settings" class="w-full md:w-96" />
            <Select
              v-model="kindFilter"
              :options="kindOptions"
              option-label="label"
              option-value="value"
              placeholder="Series and settings"
              class="w-full sm:w-56"
              aria-label="Kind"
            />
            <Button label="New" icon="pi pi-plus" class="sm:ml-auto" @click="openCreate" />
          </div>

          <Message v-if="listError" severity="error" :closable="false" class="mb-4">
            <div class="flex flex-wrap items-center gap-3">
              <span>{{ listError }}</span>
              <Button label="Try again" size="small" severity="secondary" @click="loadList" />
            </div>
          </Message>

          <DataTable
            :value="rows"
            :loading="loading"
            lazy
            paginator
            :rows="PAGE_SIZE"
            :first="offset"
            :total-records="total"
            data-key="seriesId"
            striped-rows
            class="overflow-hidden rounded-lg shadow-md"
            @page="onPage"
          >
            <template #empty>
              <p class="m-0 py-6 text-center text-gray-500 dark:text-gray-400">
                {{ query || kindFilter ? 'Nothing matches this search.' : 'No series yet. Create one here, or from the franchise builder.' }}
              </p>
            </template>
            <Column header="Name">
              <template #body="{ data }">
                <button type="button" class="text-left font-medium hover:underline" @click="openDetail(data.seriesId)">{{ data.name }}</button>
              </template>
            </Column>
            <Column header="Kind" style="width: 110px">
              <template #body="{ data }">
                <Tag :value="seriesKindLabel(data.kind)" :severity="data.kind === SeriesKind.Setting ? 'secondary' : 'info'" />
              </template>
            </Column>
            <Column field="deckCount" header="Decks" style="width: 90px" />
            <Column header="Actions" style="width: 200px">
              <template #body="{ data }">
                <div class="flex gap-2">
                  <Tooltip content="Details and members">
                    <Button icon="pi pi-pencil" size="small" severity="secondary" :aria-label="`Edit ${data.name}`" @click="openDetail(data.seriesId)" />
                  </Tooltip>
                  <Tooltip content="Merge into another">
                    <Button icon="pi pi-arrow-right-arrow-left" size="small" severity="secondary" :aria-label="`Merge ${data.name}`" @click="openMerge(data)" />
                  </Tooltip>
                  <Tooltip content="Delete">
                    <Button icon="pi pi-trash" size="small" severity="danger" :aria-label="`Delete ${data.name}`" @click="askDelete(data)" />
                  </Tooltip>
                </div>
              </template>
            </Column>
          </DataTable>
        </TabPanel>
        <TabPanel value="franchises">
          <FranchisesTab v-if="activeTab === 'franchises'" />
        </TabPanel>
      </TabPanels>
    </Tabs>

    <Dialog :visible="createOpen" header="New series or setting" modal class="w-full max-w-lg" @update:visible="(v: boolean) => (createOpen = v)">
      <form class="flex flex-col gap-4" @submit.prevent="submitCreate">
        <div class="flex flex-col gap-1.5">
          <label for="series-create-name" class="text-sm font-medium">Name</label>
          <InputText id="series-create-name" v-model="createName" autofocus class="w-full" :invalid="!!createError && !createName.trim()" />
        </div>
        <div class="flex flex-col gap-1.5">
          <label for="series-create-kind" class="text-sm font-medium">Kind</label>
          <Select
            v-model="createKind"
            input-id="series-create-kind"
            :options="[
              { value: SeriesKind.Series, label: 'Series: an ordered group of titles' },
              { value: SeriesKind.Setting, label: 'Setting: a shared world that never merges franchises' },
            ]"
            option-label="label"
            option-value="value"
            class="w-full"
          />
        </div>
        <Message v-if="createError" severity="error" :closable="false">{{ createError }}</Message>
        <div class="flex justify-end gap-2">
          <Button label="Cancel" severity="secondary" text type="button" @click="createOpen = false" />
          <Button label="Create" type="submit" :loading="creating" />
        </div>
      </form>
    </Dialog>

    <Dialog
      :visible="detailId != null"
      :header="detail ? `${seriesKindLabel(detail.kind)}: ${detail.name}` : 'Series'"
      modal
      class="w-full max-w-3xl"
      @update:visible="(v: boolean) => !v && closeDetail()"
    >
      <div v-if="detailLoading" class="flex justify-center py-8">
        <ProgressSpinner style="width: 40px; height: 40px" />
      </div>
      <div v-else-if="detail" class="flex flex-col gap-5">
        <Message v-if="detailError" severity="error" :closable="false">{{ detailError }}</Message>

        <form class="flex flex-col gap-1.5" @submit.prevent="rename">
          <label for="series-rename" class="text-sm font-medium">Name</label>
          <div class="flex flex-wrap gap-2">
            <InputText id="series-rename" v-model="renameValue" class="min-w-0 flex-1" />
            <Button
              label="Rename"
              type="submit"
              severity="secondary"
              :disabled="!renameValue.trim() || renameValue.trim() === detail.name"
              :loading="detailBusy"
            />
          </div>
        </form>

        <div class="flex flex-col gap-2">
          <div class="flex flex-wrap items-center justify-between gap-2">
            <span class="text-sm font-medium">Decks ({{ detail.members.length }})</span>
            <div class="flex flex-wrap gap-2">
              <NuxtLink
                v-if="detail.franchiseId"
                :to="franchisePath(detail.franchiseId, { kind: MediaGroupKind.Series, id: detail.seriesId })"
                class="text-sm text-primary-700 hover:underline dark:text-primary-300"
              >
                Public page
              </NuxtLink>
              <NuxtLink
                v-if="detail.members.length"
                :to="`/dashboard/franchise/${detail.members[0]!.deckId}`"
                class="text-sm font-medium text-primary-700 hover:underline dark:text-primary-300"
              >
                Open franchise builder
              </NuxtLink>
            </div>
          </div>
          <p v-if="!detail.members.length" class="m-0 text-sm text-gray-500 dark:text-gray-400">
            No decks yet. Add them from the franchise builder of any deck that belongs here.
          </p>
          <ul v-else class="m-0 flex max-h-96 list-none flex-col overflow-y-auto p-0">
            <li
              v-for="m in sortedMembers"
              :key="m.deckId"
              class="flex flex-wrap items-center justify-between gap-2 border-t border-gray-200 py-2 first:border-t-0 dark:border-gray-800"
            >
              <div class="min-w-0">
                <NuxtLink :to="`/decks/media/${m.deckId}/detail`" class="font-medium hover:underline" v-bind="japaneseTextAttrs(localiseTitle(m))">
                  {{ localiseTitle(m) }}
                </NuxtLink>
                <div class="text-xs text-gray-500 dark:text-gray-400">{{ getMediaTypeText(m.mediaType) }}</div>
              </div>
              <div v-if="confirmRemove === m.deckId" class="flex flex-wrap items-center gap-2">
                <span class="text-xs text-amber-800 dark:text-amber-300">Leaves {{ detail.name }}.</span>
                <Button label="Remove" size="small" severity="danger" outlined :loading="detailBusy" @click="removeMember(m)" />
                <Button label="Keep" size="small" severity="secondary" text @click="confirmRemove = null" />
              </div>
              <div v-else class="flex gap-1">
                <Tooltip content="Franchise builder">
                  <Button
                    size="small"
                    severity="secondary"
                    text
                    :aria-label="`Open the franchise builder for ${localiseTitle(m)}`"
                    @click="navigateTo(`/dashboard/franchise/${m.deckId}`)"
                  >
                    <Icon name="material-symbols:account-tree-outline" />
                  </Button>
                </Tooltip>
                <Button label="Remove" size="small" severity="secondary" text @click="confirmRemove = m.deckId" />
              </div>
            </li>
          </ul>
        </div>
      </div>
      <Message v-else-if="detailError" severity="error" :closable="false">{{ detailError }}</Message>
    </Dialog>

    <Dialog
      :visible="!!mergeSource"
      :header="mergeSource ? `Merge ${mergeSource.name}` : 'Merge'"
      modal
      class="w-full max-w-lg"
      @update:visible="(v: boolean) => !v && (mergeSource = null)"
    >
      <form v-if="mergeSource" class="flex flex-col gap-4" @submit.prevent="submitMerge">
        <div class="flex flex-col gap-1.5">
          <label for="series-merge-target" class="text-sm font-medium">Merge into</label>
          <AutoComplete
            v-model="mergeTarget"
            input-id="series-merge-target"
            :suggestions="mergeSuggestions"
            option-label="name"
            :placeholder="`Search ${mergeSource.kind === SeriesKind.Setting ? 'settings' : 'series'}…`"
            class="w-full"
            input-class="w-full"
            @complete="(e: { query: string }) => searchMergeTargets(e.query, mergeSource!.seriesId)"
          />
        </div>
        <p class="m-0 text-sm text-gray-600 dark:text-gray-400">
          <template v-if="mergeTarget && typeof mergeTarget === 'object'">
            {{ mergeSource.deckCount }} {{ mergeSource.deckCount === 1 ? 'deck' : 'decks' }} will move to {{ mergeTarget.name }}, then
            {{ mergeSource.name }} will be deleted.
          </template>
          <template v-else>Pick where its decks go. {{ mergeSource.name }} will be deleted afterwards.</template>
        </p>
        <Message v-if="mergeError" severity="error" :closable="false">{{ mergeError }}</Message>
        <div class="flex justify-end gap-2">
          <Button label="Cancel" severity="secondary" text type="button" @click="mergeSource = null" />
          <Button label="Merge" type="submit" severity="danger" :disabled="!mergeTarget || typeof mergeTarget !== 'object'" :loading="merging" />
        </div>
      </form>
    </Dialog>
  </div>
</template>
