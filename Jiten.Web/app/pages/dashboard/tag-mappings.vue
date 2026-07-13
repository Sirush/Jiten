<script setup lang="ts">
import { ref, watch, onMounted, computed } from 'vue';
import { useDebounceFn } from '@vueuse/core';
import DataTable from 'primevue/datatable';
import Column from 'primevue/column';
import Button from 'primevue/button';
import Dialog from 'primevue/dialog';
import InputText from 'primevue/inputtext';
import Select from 'primevue/select';
import Tag from 'primevue/tag';
import Toast from 'primevue/toast';
import { useToast } from 'primevue/usetoast';
import { useConfirm } from 'primevue/useconfirm';
import { LinkType } from '~/types/enums';
import { getLinkTypeText } from '~/utils/linkTypeMapper';
import type { TagMapping } from '~/types/types';

useHead({ title: 'Tag Mappings Management - Jiten' });
definePageMeta({ middleware: ['auth-admin'] });

const { $api } = useNuxtApp();
const toast = useToast();
const confirm = useConfirm();

const mappings = ref<TagMapping[]>([]);
const tags = ref<{ value: number; label: string }[]>([]);
const loading = ref(false);
const showDialog = ref(false);
const dialogMode = ref<'create' | 'edit'>('create');
const currentMapping = ref<TagMapping | null>(null);

// Filters
const selectedProvider = ref<LinkType | null>(null);
const selectedTag = ref<number | null>(null);
const searchQuery = ref('');

// Form data
const formData = ref({
  provider: LinkType.Web,
  externalTagName: '',
  tagId: 0,
});

// Dropdown options
const providers = computed(() =>
  Object.values(LinkType)
    .filter((v) => typeof v === 'number')
    .map((v) => ({ value: v as LinkType, label: getLinkTypeText(v as LinkType) }))
);

async function loadTags() {
  try {
    const allTags = await $api<{ tagId: number; name: string }[]>('/admin/tags');
    tags.value = allTags.map((t) => ({ value: t.tagId, label: t.name }));
  } catch (error: any) {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: error?.data?.message || 'Failed to load tags',
      life: 5000,
    });
  }
}

async function loadMappings() {
  loading.value = true;
  try {
    const params = new URLSearchParams();
    if (selectedProvider.value !== null) params.append('provider', selectedProvider.value.toString());
    if (selectedTag.value !== null) params.append('tagId', selectedTag.value.toString());
    if (searchQuery.value) params.append('search', searchQuery.value);

    mappings.value = await $api<TagMapping[]>(`/admin/tag-mappings?${params.toString()}`);
  } catch (error: any) {
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail: error?.data?.message || 'Failed to load tag mappings',
      life: 5000,
    });
  } finally {
    loading.value = false;
  }
}

const debouncedLoad = useDebounceFn(loadMappings, 300);

watch([selectedProvider, selectedTag, searchQuery], () => {
  debouncedLoad();
});

function clearFilters() {
  selectedProvider.value = null;
  selectedTag.value = null;
  searchQuery.value = '';
}

function openCreateDialog() {
  dialogMode.value = 'create';
  formData.value = {
    provider: LinkType.Web,
    externalTagName: '',
    tagId: tags.value[0]?.value || 0,
  };
  currentMapping.value = null;
  showDialog.value = true;
}

function openEditDialog(mapping: TagMapping) {
  dialogMode.value = 'edit';
  formData.value = {
    provider: mapping.provider,
    externalTagName: mapping.externalTagName,
    tagId: mapping.tagId,
  };
  currentMapping.value = mapping;
  showDialog.value = true;
}

async function saveMapping() {
  if (!formData.value.externalTagName.trim()) {
    toast.add({
      severity: 'warn',
      summary: 'Validation Error',
      detail: 'External tag name is required',
      life: 3000,
    });
    return;
  }

  if (!formData.value.tagId) {
    toast.add({
      severity: 'warn',
      summary: 'Validation Error',
      detail: 'Jiten tag is required',
      life: 3000,
    });
    return;
  }

  try {
    if (dialogMode.value === 'create') {
      await $api('/admin/tag-mappings', {
        method: 'POST',
        body: {
          provider: formData.value.provider,
          externalTagName: formData.value.externalTagName.trim(),
          tagId: formData.value.tagId,
        },
      });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Tag mapping created successfully',
        life: 3000,
      });
    } else {
      await $api(`/admin/tag-mappings/${currentMapping.value?.externalTagMappingId}`, {
        method: 'PUT',
        body: {
          provider: formData.value.provider,
          externalTagName: formData.value.externalTagName.trim(),
          tagId: formData.value.tagId,
        },
      });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Tag mapping updated successfully',
        life: 3000,
      });
    }
    showDialog.value = false;
    await loadMappings();
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

function confirmDelete(mapping: TagMapping) {
  confirm.require({
    message: `Are you sure you want to delete the mapping from "${mapping.externalTagName}" to "${mapping.tagName}"?`,
    header: 'Confirm Deletion',
    icon: 'pi pi-exclamation-triangle',
    acceptClass: 'p-button-danger',
    accept: () => deleteMapping(mapping.externalTagMappingId),
  });
}

async function deleteMapping(id: number) {
  try {
    await $api(`/admin/tag-mappings/${id}`, { method: 'DELETE' });
    toast.add({
      severity: 'success',
      summary: 'Success',
      detail: 'Tag mapping deleted successfully',
      life: 3000,
    });
    await loadMappings();
  } catch (error: any) {
    const detail = error?.data?.message || error?.message || 'Failed to delete mapping';
    toast.add({
      severity: 'error',
      summary: 'Error',
      detail,
      life: 5000,
    });
  }
}

onMounted(() => {
  loadTags();
  loadMappings();
});
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Tag Mappings Management</h1>
      </div>
      <Button label="Add Mapping" icon="pi pi-plus" @click="openCreateDialog" />
    </div>

    <!-- Filters -->
    <div class="grid grid-cols-1 md:grid-cols-4 gap-4 mb-4">
      <Select
        v-model="selectedProvider"
        :options="providers"
        optionLabel="label"
        optionValue="value"
        placeholder="Filter by Provider"
        showClear
        class="w-full"
      />
      <Select
        v-model="selectedTag"
        :options="tags"
        optionLabel="label"
        optionValue="value"
        placeholder="Filter by Tag"
        showClear
        filter
        filterPlaceholder="Search tag..."
        autoFilterFocus
        resetFilterOnHide
        class="w-full"
      />
      <InputText
        v-model="searchQuery"
        placeholder="Search external tag name..."
        class="w-full"
      />
      <Button
        label="Clear Filters"
        icon="pi pi-filter-slash"
        severity="secondary"
        @click="clearFilters"
      />
    </div>

    <DataTable
      :value="mappings"
      :loading="loading"
      :paginator="true"
      :rows="25"
      :rowsPerPageOptions="[10, 25, 50]"
      sortField="providerName"
      :sortOrder="1"
      stripedRows
      class="shadow-md"
    >
      <Column field="externalTagMappingId" header="ID" :sortable="true" style="width: 80px" />
      <Column field="providerName" header="Provider" :sortable="true" style="width: 150px">
        <template #body="slotProps">
          <Tag :value="slotProps.data.providerName" severity="info" />
        </template>
      </Column>
      <Column field="externalTagName" header="External Tag Name" :sortable="true" />
      <Column field="tagName" header="Jiten Tag" :sortable="true" style="width: 180px">
        <template #body="slotProps">
          <Tag :value="slotProps.data.tagName" severity="success" />
        </template>
      </Column>
      <Column header="Actions" style="width: 150px">
        <template #body="slotProps">
          <div class="flex gap-2">
            <Button
              icon="pi pi-pencil"
              size="small"
              severity="secondary"
              @click="openEditDialog(slotProps.data)"
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
      :header="dialogMode === 'create' ? 'Create Tag Mapping' : 'Edit Tag Mapping'"
      :modal="true"
      class="w-full md:w-1/2"
    >
      <div class="p-fluid">
        <div class="mb-4">
          <label for="provider" class="block text-sm font-medium mb-2">Provider</label>
          <Select
            id="provider"
            v-model="formData.provider"
            :options="providers"
            optionLabel="label"
            optionValue="value"
            placeholder="Select Provider"
            class="w-full"
          />
        </div>
        <div class="mb-4">
          <label for="externalTagName" class="block text-sm font-medium mb-2">External Tag Name</label>
          <InputText
            id="externalTagName"
            v-model="formData.externalTagName"
            placeholder="Enter external tag name"
            class="w-full"
          />
        </div>
        <div class="mb-4">
          <label for="jitenTag" class="block text-sm font-medium mb-2">Jiten Tag</label>
          <Select
            id="jitenTag"
            v-model="formData.tagId"
            :options="tags"
            optionLabel="label"
            optionValue="value"
            placeholder="Select Jiten Tag"
            filter
            filterPlaceholder="Search tag..."
            autoFilterFocus
            resetFilterOnHide
            class="w-full"
          />
        </div>
      </div>
      <template #footer>
        <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showDialog = false" />
        <Button label="Save" icon="pi pi-check" @click="saveMapping" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped></style>
