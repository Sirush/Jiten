<script setup lang="ts">
  import Button from 'primevue/button';
  import Message from 'primevue/message';
  import DataTable, { type DataTableSortEvent } from 'primevue/datatable';
  import Column from 'primevue/column';
  import Select from 'primevue/select';
  import Tag from 'primevue/tag';
  import { debounce } from 'perfect-debounce';
  import type { FranchiseListSort, FranchiseSummary, FranchiseSyncSummary, PaginatedResponse } from '~/types';
  import { useFranchiseRename } from '~/composables/useFranchiseRename';
  import { useJitenStore } from '~/stores/jitenStore';
  import { apiErrorMessage } from '~/utils/apiErrorMessage';

  const PAGE_SIZE = 25;
  const { $api } = useNuxtApp();
  const toast = useToast();
  const store = useJitenStore();
  const localiseTitle = useLocaliseTitle();

  type NameSource = 'all' | 'manual' | 'automatic';
  const nameSourceOptions: { value: NameSource; label: string }[] = [
    { value: 'all', label: 'All names' },
    { value: 'manual', label: 'Set by admin' },
    { value: 'automatic', label: 'Automatic' },
  ];

  const query = ref('');
  const nameSource = ref<NameSource>('all');
  const sortField = ref<FranchiseListSort>('name');
  const sortOrder = ref<1 | -1>(1);
  const rows = ref<FranchiseSummary[]>([]);
  const total = ref(0);
  const offset = ref(0);
  const loading = ref(false);
  const listError = ref('');

  let latestRequest = 0;

  async function loadList() {
    const request = ++latestRequest;
    loading.value = true;
    listError.value = '';
    try {
      const res = await $api<PaginatedResponse<FranchiseSummary[]>>('admin/franchise', {
        query: {
          query: query.value.trim() || undefined,
          manual: nameSource.value === 'all' ? undefined : nameSource.value === 'manual',
          sort: sortField.value,
          descending: sortOrder.value === -1,
          titleLanguage: store.titleLanguage,
          offset: offset.value,
          limit: PAGE_SIZE,
        },
      });
      if (request !== latestRequest) return;
      rows.value = res.data;
      total.value = res.totalItems;
    } catch (e) {
      if (request !== latestRequest) return;
      listError.value = apiErrorMessage(e, 'The franchise list could not be loaded.');
    } finally {
      if (request === latestRequest) loading.value = false;
    }
  }

  function reloadFromStart() {
    offset.value = 0;
    loadList();
  }

  watch(query, debounce(reloadFromStart, 300));
  watch(nameSource, reloadFromStart);
  watch(
    () => store.titleLanguage,
    () => {
      if (sortField.value === 'name') reloadFromStart();
    }
  );

  function onPage(ev: { first: number }) {
    offset.value = ev.first;
    loadList();
  }

  function onSort(ev: DataTableSortEvent) {
    sortField.value = (ev.sortField as FranchiseListSort) ?? 'name';
    sortOrder.value = ev.sortOrder === -1 ? -1 : 1;
    reloadFromStart();
  }

  onMounted(loadList);

  const filtering = computed(() => !!query.value.trim() || nameSource.value !== 'all');

  const syncing = ref(false);
  async function sync() {
    syncing.value = true;
    try {
      const s = await $api<FranchiseSyncSummary>('admin/franchise/sync', { method: 'POST' });
      toast.add({
        severity: 'success',
        summary: 'Franchises synced',
        detail: `${s.created} created, ${s.renamed} renamed, ${s.merged} merged, ${s.deleted} deleted, ${s.unchanged} unchanged.`,
        life: 8000,
      });
      await loadList();
    } catch (e) {
      toast.add({ severity: 'error', summary: 'Not synced', detail: apiErrorMessage(e, 'The sync failed. Nothing was changed.'), life: 6000 });
    } finally {
      syncing.value = false;
    }
  }

  const editingId = ref<number | null>(null);
  const rename = useFranchiseRename({
    saved: async (titles) => {
      toast.add({ severity: 'success', summary: 'Done', detail: titles ? `Renamed to ${localiseTitle(titles)}` : 'Back to the automatic name', life: 3000 });
      await cancelEdit();
      await loadList();
    },
    close: () => cancelEdit(),
  });
  const { value: editValue, busy: saving, error: editError } = rename;

  function startEdit(row: FranchiseSummary) {
    editingId.value = row.franchiseId;
    rename.reset(row);
  }

  async function cancelEdit() {
    const id = editingId.value;
    editingId.value = null;
    await nextTick();
    if (id != null) document.getElementById(`franchise-edit-${id}`)?.focus();
  }
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-wrap items-center gap-2">
      <SearchInput v-model="query" placeholder="Search by name or deck title" aria-label="Search franchises" class="w-full md:w-96" />
      <Select v-model="nameSource" :options="nameSourceOptions" option-label="label" option-value="value" class="w-full sm:w-44" aria-label="Name source" />
      <Button label="Sync franchises" icon="pi pi-sync" severity="secondary" outlined :loading="syncing" class="sm:ml-auto" @click="sync" />
    </div>

    <Message v-if="listError" severity="error" :closable="false">
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
      :sort-field="sortField"
      :sort-order="sortOrder"
      data-key="franchiseId"
      striped-rows
      class="overflow-hidden rounded-lg shadow-md"
      @page="onPage"
      @sort="onSort"
    >
      <template #empty>
        <p v-if="!listError" class="m-0 py-6 text-center text-gray-500 dark:text-gray-400">
          {{ filtering ? 'Nothing matches this search.' : 'No franchises yet. Sync franchises to build them from the stored links and series.' }}
        </p>
      </template>
      <Column field="name" header="Name" sortable>
        <template #body="{ data }">
          <form
            v-if="editingId === data.franchiseId"
            class="flex flex-col gap-2"
            @submit.prevent="rename.submit({ franchiseId: data.franchiseId, titles: data, nameIsManual: data.nameIsManual })"
            @keydown.esc.prevent="cancelEdit"
          >
            <GroupTitlesFields v-model="editValue" :invalid="!!editError && !editValue.originalTitle.trim()" autofocus />
            <p v-if="editError" class="m-0 text-sm text-red-700 dark:text-red-400" role="alert">{{ editError }}</p>
            <div class="flex flex-wrap gap-2">
              <Button label="Save" type="submit" size="small" :loading="saving" />
              <Button
                v-if="data.nameIsManual"
                label="Use automatic name"
                type="button"
                size="small"
                severity="secondary"
                :disabled="saving"
                @click="rename.save(data.franchiseId, null)"
              />
              <Button label="Cancel" type="button" size="small" severity="secondary" text :disabled="saving" @click="cancelEdit" />
            </div>
          </form>
          <div v-else class="flex min-w-0 flex-col">
            <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
              <NuxtLink :to="`/franchise/${data.franchiseId}`" class="font-medium hover:underline" v-bind="japaneseTextAttrs(localiseTitle(data))">
                {{ localiseTitle(data) }}
              </NuxtLink>
              <Tag v-if="data.nameIsManual" value="Set by admin" severity="info" class="!px-1.5 !py-0 !text-xs" />
            </div>
            <span
              v-if="localiseTitle(data) !== data.originalTitle"
              class="text-sm text-gray-500 dark:text-gray-400"
              v-bind="japaneseTextAttrs(data.originalTitle)"
            >
              {{ data.originalTitle }}
            </span>
          </div>
        </template>
      </Column>
      <Column field="deckCount" sort-field="decks" header="Decks" sortable style="width: 90px" />
      <Column field="seriesCount" sort-field="series" header="Series" sortable style="width: 90px" />
      <Column style="width: 110px">
        <template #header><span class="sr-only">Actions</span></template>
        <template #body="{ data }">
          <div class="flex justify-end gap-2">
            <Tooltip content="Rename">
              <Button
                :id="`franchise-edit-${data.franchiseId}`"
                icon="pi pi-pencil"
                size="small"
                severity="secondary"
                :aria-label="`Rename ${localiseTitle(data)}`"
                :disabled="editingId === data.franchiseId"
                @click="startEdit(data)"
              />
            </Tooltip>
            <Tooltip v-if="data.firstDeckId" content="Open in the franchise builder">
              <Button
                as="router-link"
                :to="`/dashboard/franchise/${data.firstDeckId}`"
                icon="pi pi-sitemap"
                size="small"
                severity="secondary"
                :aria-label="`Open ${localiseTitle(data)} in the franchise builder`"
              />
            </Tooltip>
          </div>
        </template>
      </Column>
    </DataTable>
  </div>
</template>
