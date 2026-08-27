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
  import { LinkType, Genre } from '~/types/enums';
  import { getLinkTypeText } from '~/utils/linkTypeMapper';
  import { getAllGenres, getGenreText } from '~/utils/genreMapper';
  import type { GenreMapping } from '~/types/types';

  useHead({ title: 'Genre Mappings Management - Jiten' });
  definePageMeta({ middleware: ['auth-admin'] });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const mappings = ref<GenreMapping[]>([]);
  const loading = ref(false);
  const showDialog = ref(false);
  const dialogMode = ref<'create' | 'edit'>('create');
  const currentMapping = ref<GenreMapping | null>(null);

  // Filters
  const selectedProvider = ref<LinkType | null>(null);
  const selectedGenre = ref<Genre | null>(null);
  const searchQuery = ref('');

  // Form data
  const formData = ref({
    provider: LinkType.Web,
    externalGenreName: '',
    jitenGenre: Genre.Action,
  });

  // Dropdown options
  const providers = computed(() =>
    Object.values(LinkType)
      .filter((v) => typeof v === 'number')
      .map((v) => ({ value: v as LinkType, label: getLinkTypeText(v as LinkType) }))
  );

  const genres = computed(() => getAllGenres());

  async function loadMappings() {
    loading.value = true;
    try {
      const params = new URLSearchParams();
      if (selectedProvider.value !== null) params.append('provider', selectedProvider.value.toString());
      if (selectedGenre.value !== null) params.append('jitenGenre', selectedGenre.value.toString());
      if (searchQuery.value) params.append('search', searchQuery.value);

      mappings.value = await $api<GenreMapping[]>(`/admin/genre-mappings?${params.toString()}`);
    } catch (error: any) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: error?.data?.message || 'Failed to load genre mappings',
        life: 5000,
      });
    } finally {
      loading.value = false;
    }
  }

  const debouncedLoad = useDebounceFn(loadMappings, 300);

  watch([selectedProvider, selectedGenre, searchQuery], () => {
    debouncedLoad();
  });

  function clearFilters() {
    selectedProvider.value = null;
    selectedGenre.value = null;
    searchQuery.value = '';
  }

  function openCreateDialog() {
    dialogMode.value = 'create';
    formData.value = {
      provider: LinkType.Web,
      externalGenreName: '',
      jitenGenre: Genre.Action,
    };
    currentMapping.value = null;
    showDialog.value = true;
  }

  function openEditDialog(mapping: GenreMapping) {
    dialogMode.value = 'edit';
    formData.value = {
      provider: mapping.provider,
      externalGenreName: mapping.externalGenreName,
      jitenGenre: mapping.jitenGenre,
    };
    currentMapping.value = mapping;
    showDialog.value = true;
  }

  async function saveMapping() {
    if (!formData.value.externalGenreName.trim()) {
      toast.add({
        severity: 'warn',
        summary: 'Validation Error',
        detail: 'External genre name is required',
        life: 3000,
      });
      return;
    }

    try {
      if (dialogMode.value === 'create') {
        await $api('/admin/genre-mappings', {
          method: 'POST',
          body: {
            provider: formData.value.provider,
            externalGenreName: formData.value.externalGenreName.trim(),
            jitenGenre: formData.value.jitenGenre,
          },
        });
        toast.add({
          severity: 'success',
          summary: 'Success',
          detail: 'Genre mapping created successfully',
          life: 3000,
        });
      } else {
        await $api(`/admin/genre-mappings/${currentMapping.value?.externalGenreMappingId}`, {
          method: 'PUT',
          body: {
            provider: formData.value.provider,
            externalGenreName: formData.value.externalGenreName.trim(),
            jitenGenre: formData.value.jitenGenre,
          },
        });
        toast.add({
          severity: 'success',
          summary: 'Success',
          detail: 'Genre mapping updated successfully',
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

  function confirmDelete(mapping: GenreMapping) {
    confirm.require({
      message: `Are you sure you want to delete the mapping from "${mapping.externalGenreName}" to "${mapping.jitenGenreName}"?`,
      header: 'Confirm Deletion',
      icon: 'pi pi-exclamation-triangle',
      acceptClass: 'p-button-danger',
      accept: () => deleteMapping(mapping.externalGenreMappingId),
    });
  }

  async function deleteMapping(id: number) {
    try {
      await $api(`/admin/genre-mappings/${id}`, { method: 'DELETE' });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: 'Genre mapping deleted successfully',
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
    loadMappings();
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Genre Mappings Management</h1>
      </div>
      <Button label="Add Mapping" icon="pi pi-plus" @click="openCreateDialog" />
    </div>

    <!-- Filters -->
    <div class="grid grid-cols-1 md:grid-cols-4 gap-4 mb-4">
      <Select
        v-model="selectedProvider"
        :options="providers"
        option-label="label"
        option-value="value"
        placeholder="Filter by Provider"
        show-clear
        class="w-full"
      />
      <Select
        v-model="selectedGenre"
        :options="genres"
        option-label="label"
        option-value="value"
        placeholder="Filter by Genre"
        show-clear
        filter
        filter-placeholder="Search genre..."
        auto-filter-focus
        reset-filter-on-hide
        class="w-full"
      />
      <InputText v-model="searchQuery" placeholder="Search external genre name..." class="w-full" />
      <Button label="Clear Filters" icon="pi pi-filter-slash" severity="secondary" @click="clearFilters" />
    </div>

    <DataTable
      :value="mappings"
      :loading="loading"
      :paginator="true"
      :rows="25"
      :rows-per-page-options="[10, 25, 50]"
      sort-field="providerName"
      :sort-order="1"
      striped-rows
      class="shadow-md"
    >
      <Column field="externalGenreMappingId" header="ID" :sortable="true" style="width: 80px" />
      <Column field="providerName" header="Provider" :sortable="true" style="width: 150px">
        <template #body="slotProps">
          <Tag :value="slotProps.data.providerName" severity="info" />
        </template>
      </Column>
      <Column field="externalGenreName" header="External Genre Name" :sortable="true" />
      <Column field="jitenGenreName" header="Jiten Genre" :sortable="true" style="width: 180px">
        <template #body="slotProps">
          <Tag :value="slotProps.data.jitenGenreName" severity="success" />
        </template>
      </Column>
      <Column header="Actions" style="width: 150px">
        <template #body="slotProps">
          <div class="flex gap-2">
            <Button icon="pi pi-pencil" size="small" severity="secondary" @click="openEditDialog(slotProps.data)" />
            <Button icon="pi pi-trash" size="small" severity="danger" @click="confirmDelete(slotProps.data)" />
          </div>
        </template>
      </Column>
    </DataTable>

    <!-- Create/Edit Dialog -->
    <Dialog
      v-model:visible="showDialog"
      :header="dialogMode === 'create' ? 'Create Genre Mapping' : 'Edit Genre Mapping'"
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
            option-label="label"
            option-value="value"
            placeholder="Select Provider"
            class="w-full"
          />
        </div>
        <div class="mb-4">
          <label for="externalGenreName" class="block text-sm font-medium mb-2">External Genre Name</label>
          <InputText id="externalGenreName" v-model="formData.externalGenreName" placeholder="Enter external genre name" class="w-full" />
        </div>
        <div class="mb-4">
          <label for="jitenGenre" class="block text-sm font-medium mb-2">Jiten Genre</label>
          <Select
            id="jitenGenre"
            v-model="formData.jitenGenre"
            :options="genres"
            option-label="label"
            option-value="value"
            placeholder="Select Jiten Genre"
            filter
            filter-placeholder="Search genre..."
            auto-filter-focus
            reset-filter-on-hide
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
