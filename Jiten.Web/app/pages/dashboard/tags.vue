<script setup lang="ts">
import { ref, computed, onMounted } from 'vue';
import { LinkType } from '~/types/enums';
import { getLinkTypeText } from '~/utils/linkTypeMapper';
import type { Tag as TagType, TagUsage, TagMapping } from '~/types/types';

useHead({ title: 'Tags Management - Jiten' });
definePageMeta({ middleware: ['auth-admin'] });

const { $api } = useNuxtApp();
const toast = useToast();
const confirm = useConfirm();

const tags = ref<TagType[]>([]);
const loading = ref(false);
const showDialog = ref(false);
const showUsageDialog = ref(false);
const dialogMode = ref<'create' | 'edit'>('create');
const currentTag = ref<TagType | null>(null);
const tagName = ref('');
const tagUsage = ref<TagUsage | null>(null);
const loadingUsage = ref(false);
const searchQuery = ref('');

// Tag mappings dialog state
const showMappingsDialog = ref(false);
const currentMappingsTag = ref<TagType | null>(null);
const tagMappings = ref<TagMapping[]>([]);
const loadingMappings = ref(false);

interface NewMappingRow {
  id: string;
  provider: LinkType;
  externalTagName: string;
  isSaving: boolean;
}
const newMappingRows = ref<NewMappingRow[]>([]);

const filteredTags = computed(() => {
  if (!searchQuery.value) return tags.value;

  const query = searchQuery.value.toLowerCase();
  return tags.value.filter(tag =>
    tag.name.toLowerCase().includes(query)
  );
});

const providers = computed(() =>
  Object.values(LinkType)
    .filter((v) => typeof v === 'number')
    .map((v) => ({ value: v as LinkType, label: getLinkTypeText(v as LinkType) }))
);

async function loadTags() {
  loading.value = true;
  try {
    tags.value = await $api<TagType[]>('/admin/tags');
  } catch (error: any) {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: error?.data?.message || 'Failed to load tags',
      life: 5000,
    });
  } finally {
    loading.value = false;
  }
}

function openCreateDialog() {
  dialogMode.value = 'create';
  tagName.value = '';
  currentTag.value = null;
  showDialog.value = true;
}

function openEditDialog(tag: TagType) {
  dialogMode.value = 'edit';
  tagName.value = tag.name;
  currentTag.value = tag;
  showDialog.value = true;
}

async function saveTag() {
  if (!tagName.value.trim()) {
    toast.add({
      severity: 'warn',
      summary: 'Validation Error',
      detail: 'Tag name is required',
      life: 3000,
    });
    return;
  }

  try {
    if (dialogMode.value === 'create') {
      await $api('/admin/tags', {
        method: 'POST',
        body: { name: tagName.value.trim() },
      });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Tag created successfully',
        life: 3000,
      });
    } else {
      await $api(`/admin/tags/${currentTag.value?.tagId}`, {
        method: 'PUT',
        body: { name: tagName.value.trim() },
      });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Tag updated successfully',
        life: 3000,
      });
    }
    showDialog.value = false;
    await loadTags();
  } catch (error: any) {
    const detail = error?.data?.message || error?.message || 'Operation failed';
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail,
      life: 5000,
    });
  }
}

function confirmDelete(tag: TagType) {
  confirm.require({
    message: `Are you sure you want to delete "${tag.name}"? This action cannot be undone and will cascade delete associated deck tags and mappings.`,
    header: 'Confirm Deletion',
    icon: 'pi pi-exclamation-triangle',
    acceptClass: 'p-button-danger',
    accept: () => deleteTag(tag.tagId),
  });
}

async function deleteTag(id: number) {
  try {
    await $api(`/admin/tags/${id}`, { method: 'DELETE' });
    toast.add({
      severity: 'success',
      summary: 'Success',
      detail: 'Tag deleted successfully',
      life: 3000,
    });
    await loadTags();
  } catch (error: any) {
    const detail = error?.data?.message || error?.message || 'Failed to delete tag';
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail,
      life: 5000,
    });
  }
}

async function viewUsage(tagId: number) {
  loadingUsage.value = true;
  try {
    tagUsage.value = await $api<TagUsage>(`/admin/tags/${tagId}/usage`);
    showUsageDialog.value = true;
  } catch (error: any) {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: error?.data?.message || 'Failed to load usage statistics',
      life: 5000,
    });
  } finally {
    loadingUsage.value = false;
  }
}

function createNewMappingRow(): NewMappingRow {
  return {
    id: crypto.randomUUID(),
    provider: LinkType.Anilist,
    externalTagName: '',
    isSaving: false,
  };
}

function addNewMappingRow() {
  const lastProvider =
    newMappingRows.value.length > 0
      ? newMappingRows.value[newMappingRows.value.length - 1].provider
      : LinkType.Anilist;

  newMappingRows.value.push({
    id: crypto.randomUUID(),
    provider: lastProvider,
    externalTagName: '',
    isSaving: false,
  });
}

function removeNewMappingRow(rowId: string) {
  newMappingRows.value = newMappingRows.value.filter((r) => r.id !== rowId);
  if (newMappingRows.value.length === 0) {
    addNewMappingRow();
  }
}

async function loadTagMappings() {
  if (!currentMappingsTag.value) return;

  loadingMappings.value = true;
  try {
    const params = new URLSearchParams();
    params.append('tagId', currentMappingsTag.value.tagId.toString());
    tagMappings.value = await $api<TagMapping[]>(`/admin/tag-mappings?${params.toString()}`);
  } catch (error: any) {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: error?.data?.message || 'Failed to load tag mappings',
      life: 5000,
    });
  } finally {
    loadingMappings.value = false;
  }
}

async function saveMapping(row: NewMappingRow) {
  if (!row.externalTagName.trim()) {
    toast.add({
      severity: 'warn',
      summary: 'Validation Error',
      detail: 'External tag name is required',
      life: 3000,
    });
    return;
  }

  row.isSaving = true;
  try {
    await $api('/admin/tag-mappings', {
      method: 'POST',
      body: {
        provider: row.provider,
        externalTagName: row.externalTagName.trim(),
        tagId: currentMappingsTag.value!.tagId,
      },
    });

    toast.add({
      severity: 'success',
      summary: 'Success',
      detail: 'Tag mapping created successfully',
      life: 3000,
    });

    removeNewMappingRow(row.id);
    await loadTagMappings();
  } catch (error: any) {
    const detail = error?.data?.message || error?.message || 'Failed to create mapping';
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail,
      life: 5000,
    });
    row.isSaving = false;
  }
}

function openMappingsDialog(tag: TagType) {
  currentMappingsTag.value = tag;
  newMappingRows.value = [createNewMappingRow()];
  showMappingsDialog.value = true;
  loadTagMappings();
}

function closeMappingsDialog() {
  showMappingsDialog.value = false;
  currentMappingsTag.value = null;
  tagMappings.value = [];
  newMappingRows.value = [];
}

onMounted(() => {
  loadTags();
});
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Tags Management</h1>
      </div>
      <Button label="Add Tag" icon="pi pi-plus" @click="openCreateDialog" />
    </div>

    <!-- Search Bar -->
    <div class="mb-4">
      <IconField class="w-full md:w-96">
        <InputIcon>
          <Icon name="material-symbols:search-rounded" />
        </InputIcon>
        <InputText
          v-model="searchQuery"
          type="text"
          placeholder="Search tags by name..."
          class="w-full"
        />
        <InputIcon
          v-if="searchQuery"
          class="cursor-pointer"
          @click="searchQuery = ''"
        >
          <Icon name="material-symbols:close" />
        </InputIcon>
      </IconField>
    </div>

    <DataTable
      :value="filteredTags"
      :loading="loading"
      :paginator="true"
      :rows="25"
      :rowsPerPageOptions="[10, 25, 50]"
      sortField="name"
      :sortOrder="1"
      stripedRows
      class="shadow-md"
    >
      <Column field="tagId" header="ID" :sortable="true" style="width: 100px" />
      <Column field="name" header="Name" :sortable="true" />
      <Column header="Actions" style="width: 250px">
        <template #body="slotProps">
          <div class="flex gap-2">
            <Button
              icon="pi pi-pencil"
              size="small"
              severity="secondary"
              @click="openEditDialog(slotProps.data)"
            />
            <Button
              icon="pi pi-chart-bar"
              size="small"
              severity="info"
              @click="viewUsage(slotProps.data.tagId)"
            />
            <Button
              icon="pi pi-link"
              size="small"
              severity="info"
              @click="openMappingsDialog(slotProps.data)"
              v-tooltip.top="'Manage Mappings'"
            />
            <Button
              icon="pi pi-trash"
              size="small"
              severity="danger"
              @click="confirmDelete(slotProps.data)"
            />
          </div>
        </template>
      </Column>
    </DataTable>

    <!-- Create/Edit Dialog -->
    <Dialog
      v-model:visible="showDialog"
      :header="dialogMode === 'create' ? 'Create Tag' : 'Edit Tag'"
      :modal="true"
      class="w-full md:w-1/2"
    >
      <div class="p-fluid">
        <div class="mb-4">
          <label for="tagName" class="block text-sm font-medium mb-2">Tag Name</label>
          <InputText
            id="tagName"
            v-model="tagName"
            placeholder="Enter tag name"
            class="w-full"
            @keyup.enter="saveTag"
          />
        </div>
      </div>
      <template #footer>
        <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showDialog = false" />
        <Button label="Save" icon="pi pi-check" @click="saveTag" />
      </template>
    </Dialog>

    <!-- Usage Dialog -->
    <Dialog
      v-model:visible="showUsageDialog"
      header="Tag Usage Statistics"
      :modal="true"
      class="w-full md:w-1/2"
    >
      <div v-if="loadingUsage" class="flex justify-center items-center py-8">
        <ProgressSpinner style="width: 50px; height: 50px" />
      </div>
      <div v-else-if="tagUsage" class="p-4">
        <div class="grid grid-cols-2 gap-4">
          <div class="text-center p-4 border rounded">
            <p class="text-sm text-gray-500 dark:text-gray-400">Decks Using This Tag</p>
            <p class="text-3xl font-bold mt-2">{{ tagUsage.deckCount }}</p>
          </div>
          <div class="text-center p-4 border rounded">
            <p class="text-sm text-gray-500 dark:text-gray-400">External Tag Mappings</p>
            <p class="text-3xl font-bold mt-2">{{ tagUsage.mappingCount }}</p>
          </div>
        </div>
      </div>
      <template #footer>
        <Button label="Close" @click="showUsageDialog = false" />
      </template>
    </Dialog>

    <!-- Manage Mappings Dialog -->
    <Dialog
      v-model:visible="showMappingsDialog"
      :header="`Manage Tag Mappings: ${currentMappingsTag?.name || ''}`"
      :modal="true"
      class="w-full md:w-3/4 lg:w-2/3"
      @hide="closeMappingsDialog"
    >
      <div class="space-y-6">
        <!-- Existing Mappings Section -->
        <div>
          <h3 class="text-lg font-semibold mb-3">Existing Mappings</h3>
          <div v-if="loadingMappings" class="flex justify-center py-8">
            <ProgressSpinner style="width: 50px; height: 50px" />
          </div>
          <DataTable
            v-else-if="tagMappings.length > 0"
            :value="tagMappings"
            class="shadow-sm"
            stripedRows
          >
            <Column field="providerName" header="Provider" style="width: 180px">
              <template #body="slotProps">
                <Tag :value="slotProps.data.providerName" severity="info" />
              </template>
            </Column>
            <Column field="externalTagName" header="External Tag Name" />
          </DataTable>
          <div v-else class="text-center py-8 text-gray-500 dark:text-gray-400">
            No existing mappings for this tag
          </div>
        </div>

        <!-- Add New Mappings Section -->
        <div>
          <h3 class="text-lg font-semibold mb-3">Add New Mappings</h3>
          <div class="space-y-3">
            <div
              v-for="row in newMappingRows"
              :key="row.id"
              class="grid grid-cols-12 gap-2 items-end"
            >
              <div class="col-span-12 md:col-span-4">
                <label class="block text-sm font-medium mb-1">Provider</label>
                <Select
                  v-model="row.provider"
                  :options="providers"
                  optionLabel="label"
                  optionValue="value"
                  class="w-full"
                  :disabled="row.isSaving"
                />
              </div>
              <div class="col-span-12 md:col-span-5">
                <label class="block text-sm font-medium mb-1">External Tag Name</label>
                <InputText
                  v-model="row.externalTagName"
                  placeholder="Enter external tag name"
                  class="w-full"
                  :disabled="row.isSaving"
                  @keyup.enter="saveMapping(row)"
                />
              </div>
              <div class="col-span-12 md:col-span-3 flex gap-2">
                <Button
                  icon="pi pi-check"
                  severity="success"
                  :loading="row.isSaving"
                  :disabled="!row.externalTagName.trim() || row.isSaving"
                  @click="saveMapping(row)"
                />
                <Button
                  icon="pi pi-trash"
                  severity="danger"
                  :disabled="row.isSaving"
                  @click="removeNewMappingRow(row.id)"
                />
              </div>
            </div>
          </div>
          <div class="flex justify-end mt-4">
            <Button
              label="Add Row"
              icon="pi pi-plus"
              severity="secondary"
              @click="addNewMappingRow"
            />
          </div>
        </div>
      </div>

      <template #footer>
        <Button label="Close" @click="closeMappingsDialog" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped></style>
