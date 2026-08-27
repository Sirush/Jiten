<script setup lang="ts">
  import { ref, onMounted, watch } from 'vue';

  useHead({ title: 'Word Sets - Jiten Admin' });
  definePageMeta({ middleware: ['auth-admin'] });

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  interface WordSetItem {
    setId: number;
    slug: string;
    name: string;
    description: string | null;
    wordCount: number;
    formCount: number;
  }

  const wordSets = ref<WordSetItem[]>([]);
  const loading = ref(false);

  const showDialog = ref(false);
  const dialogMode = ref<'create' | 'edit'>('create');
  const editingSet = ref<WordSetItem | null>(null);
  const formName = ref('');
  const formSlug = ref('');
  const formDescription = ref('');
  const saving = ref(false);

  async function loadWordSets() {
    loading.value = true;
    try {
      wordSets.value = await $api<WordSetItem[]>('/admin/word-sets');
    } catch (error: any) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: error?.data?.message || 'Failed to load word sets',
        life: 5000,
      });
    } finally {
      loading.value = false;
    }
  }

  function slugify(text: string): string {
    return text
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 50);
  }

  function openCreateDialog() {
    dialogMode.value = 'create';
    editingSet.value = null;
    formName.value = '';
    formSlug.value = '';
    formDescription.value = '';
    showDialog.value = true;
  }

  function openEditDialog(set: WordSetItem) {
    dialogMode.value = 'edit';
    editingSet.value = set;
    formName.value = set.name;
    formSlug.value = set.slug;
    formDescription.value = set.description || '';
    showDialog.value = true;
  }

  // Auto-generate slug from name when creating
  watch(formName, (val) => {
    if (dialogMode.value === 'create') {
      formSlug.value = slugify(val);
    }
  });

  async function saveWordSet() {
    if (!formName.value.trim() || !formSlug.value.trim()) {
      toast.add({
        severity: 'warn',
        summary: 'Validation Error',
        detail: 'Name and slug are required',
        life: 3000,
      });
      return;
    }

    saving.value = true;
    try {
      const body = {
        name: formName.value.trim(),
        slug: formSlug.value.trim(),
        description: formDescription.value.trim() || null,
      };

      if (dialogMode.value === 'create') {
        await $api('/admin/word-sets', { method: 'POST', body });
        toast.add({
          severity: 'success',
          summary: 'Success',
          detail: 'Word set created',
          life: 3000,
        });
      } else {
        await $api(`/admin/word-sets/${editingSet.value!.setId}`, { method: 'PUT', body });
        toast.add({
          severity: 'success',
          summary: 'Success',
          detail: 'Word set updated',
          life: 3000,
        });
      }
      showDialog.value = false;
      await loadWordSets();
    } catch (error: any) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: error?.data?.message || 'Operation failed',
        life: 5000,
      });
    } finally {
      saving.value = false;
    }
  }

  function confirmDelete(set: WordSetItem) {
    confirm.require({
      message: `Are you sure you want to delete "${set.name}"? This will also remove all user subscriptions for this set.`,
      header: 'Confirm Deletion',
      icon: 'pi pi-exclamation-triangle',
      acceptProps: { severity: 'danger' },
      accept: () => deleteWordSet(set),
    });
  }

  async function deleteWordSet(set: WordSetItem) {
    try {
      await $api(`/admin/word-sets/${set.setId}`, { method: 'DELETE' });
      toast.add({
        severity: 'success',
        summary: 'Success',
        detail: `Word set "${set.name}" deleted`,
        life: 3000,
      });
      await loadWordSets();
    } catch (error: any) {
      toast.add({
        severity: 'error',
        summary: 'Error',
        detail: error?.data?.message || 'Failed to delete word set',
        life: 5000,
      });
    }
  }

  onMounted(() => loadWordSets());
</script>

<template>
  <div class="container mx-auto p-4">
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard')" />
        <h1 class="text-3xl font-bold">Word Sets</h1>
      </div>
      <Button label="Create Word Set" icon="pi pi-plus" @click="openCreateDialog" />
    </div>

    <DataTable :value="wordSets" :loading="loading" :paginator="wordSets.length > 25" :rows="25" striped-rows class="shadow-md">
      <Column field="name" header="Name" :sortable="true">
        <template #body="{ data }">
          <NuxtLink :to="`/dashboard/word-sets/${data.setId}`" class="text-blue-500 hover:underline font-semibold">
            {{ data.name }}
          </NuxtLink>
        </template>
      </Column>
      <Column field="slug" header="Slug" :sortable="true" />
      <Column field="wordCount" header="Words" :sortable="true" style="width: 100px" />
      <Column field="formCount" header="Forms" :sortable="true" style="width: 100px" />
      <Column header="Actions" style="width: 200px">
        <template #body="{ data }">
          <div class="flex gap-2">
            <Button v-tooltip.top="'Edit'" icon="pi pi-pencil" size="small" severity="secondary" @click="openEditDialog(data)" />
            <Button v-tooltip.top="'View members'" icon="pi pi-eye" size="small" severity="info" @click="navigateTo(`/dashboard/word-sets/${data.setId}`)" />
            <Button v-tooltip.top="'Delete'" icon="pi pi-trash" size="small" severity="danger" @click="confirmDelete(data)" />
          </div>
        </template>
      </Column>
    </DataTable>

    <!-- Create/Edit Dialog -->
    <Dialog v-model:visible="showDialog" :header="dialogMode === 'create' ? 'Create Word Set' : 'Edit Word Set'" :modal="true" class="w-full md:w-1/2">
      <div class="flex flex-col gap-4">
        <div>
          <label class="block text-sm font-medium mb-1">Name</label>
          <InputText v-model="formName" placeholder="e.g. JLPT N3" class="w-full" @keyup.enter="saveWordSet" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Slug</label>
          <InputText v-model="formSlug" placeholder="e.g. jlpt-n3" class="w-full" @keyup.enter="saveWordSet" />
          <div class="text-xs text-gray-400 mt-1">Lowercase letters, numbers, and hyphens only</div>
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Description</label>
          <Textarea v-model="formDescription" placeholder="Optional description" class="w-full" rows="3" />
        </div>
      </div>
      <template #footer>
        <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showDialog = false" />
        <Button :label="dialogMode === 'create' ? 'Create' : 'Save'" icon="pi pi-check" :loading="saving" @click="saveWordSet" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped></style>
