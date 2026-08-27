<script setup lang="ts">
  import { ref, onMounted } from 'vue';
  import { useDebounceFn } from '@vueuse/core';
  import type { DictionaryEntry } from '~/types/types';

  useHead({ title: 'Word Set Detail - Jiten Admin' });
  definePageMeta({ middleware: ['auth-admin'] });

  const route = useRoute();
  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();

  const setId = Number(route.params.id);

  // Persist sort in URL query params
  const initialSortBy = (route.query.sortBy as string) || 'position';
  const initialSortOrder = (route.query.sortOrder as string) || 'asc';

  // Word set metadata
  interface WordSetInfo {
    setId: number;
    slug: string;
    name: string;
    description: string | null;
    wordCount: number;
    formCount: number;
  }

  interface WordSetMember {
    key: string;
    wordId: number;
    readingIndex: number;
    position: number;
    text: string;
    rubyText: string;
    meanings: string[];
    partsOfSpeech: string[];
    frequencyRank: number;
  }

  interface WordForm {
    readingIndex: number;
    text: string;
    rubyText: string;
    formType: number;
    frequencyRank: number;
    alreadyInSet: boolean;
  }

  interface WordFormsPreview {
    wordId: number;
    partsOfSpeech: string[];
    meanings: string[];
    forms: WordForm[];
  }

  const wordSet = ref<WordSetInfo | null>(null);
  const loadingSet = ref(false);

  // All member keys for checking existence in search results
  const memberKeySet = ref<Set<string>>(new Set());

  // Members table
  const members = ref<WordSetMember[]>([]);
  const totalMembers = ref(0);
  const loadingMembers = ref(false);
  const currentPage = ref(0);
  const rowsPerPage = ref(50);
  const sortBy = ref(initialSortBy);
  const sortOrder = ref(initialSortOrder);
  const selectedMembers = ref<WordSetMember[]>([]);

  // Search & add
  const searchQuery = ref('');
  const searchResults = ref<DictionaryEntry[]>([]);
  const searchLoading = ref(false);

  // Add by WordId
  const wordIdInput = ref('');
  const addingByWordId = ref(false);

  // Form selection dialog
  const showFormDialog = ref(false);
  const formPreview = ref<WordFormsPreview | null>(null);
  const selectedForms = ref<Set<number>>(new Set());
  const addingForms = ref(false);

  // Edit metadata dialog
  const showEditDialog = ref(false);
  const editName = ref('');
  const editSlug = ref('');
  const editDescription = ref('');
  const savingEdit = ref(false);

  async function loadWordSet() {
    loadingSet.value = true;
    try {
      const sets = await $api<WordSetInfo[]>('/admin/word-sets');
      wordSet.value = sets.find((s) => s.setId === setId) || null;
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to load word set'), life: 5000 });
    } finally {
      loadingSet.value = false;
    }
  }

  async function loadAllMemberKeys() {
    try {
      const keys = await $api<{ wordId: number; readingIndex: number }[]>(`/admin/word-sets/${setId}/member-keys`);
      memberKeySet.value = new Set(keys.map((k) => `${k.wordId}-${k.readingIndex}`));
    } catch {}
  }

  async function loadMembers(silent = false) {
    if (!silent) loadingMembers.value = true;
    try {
      const params = new URLSearchParams();
      params.append('offset', (currentPage.value * rowsPerPage.value).toString());
      params.append('limit', rowsPerPage.value.toString());
      params.append('sortBy', sortBy.value);
      params.append('sortOrder', sortOrder.value);

      const result = await $api<{ data: WordSetMember[]; totalItems: number }>(`/admin/word-sets/${setId}/members?${params.toString()}`);
      members.value = result.data.map((m) => ({ ...m, key: `${m.wordId}-${m.readingIndex}` }));
      totalMembers.value = result.totalItems;
    } catch (error: any) {
      if (!silent) toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to load members'), life: 5000 });
    } finally {
      loadingMembers.value = false;
    }
  }

  function onPage(event: any) {
    currentPage.value = event.page;
    rowsPerPage.value = event.rows;
    loadMembers();
  }

  function onSortChange() {
    currentPage.value = 0;
    const router = useRouter();
    router.replace({ query: { sortBy: sortBy.value, sortOrder: sortOrder.value } });
    loadMembers();
  }

  // Search
  const { search: dictionarySearch, results: dictResults, isLoading: dictLoading } = useDictionarySearch();

  const debouncedSearch = useDebounceFn(async (query: string) => {
    if (!query || query.trim().length === 0) {
      searchResults.value = [];
      searchLoading.value = false;
      return;
    }
    searchLoading.value = true;
    await dictionarySearch(query.trim());
    searchResults.value = dictResults.value;
    searchLoading.value = dictLoading.value;
  }, 300);

  function onSearchInput() {
    searchLoading.value = true;
    debouncedSearch(searchQuery.value);
  }

  // Add word flow
  async function addWord(entry: DictionaryEntry) {
    try {
      const preview = await $api<WordFormsPreview>(`/admin/word-sets/${setId}/word-forms/${entry.wordId}`);

      const eligibleForms = preview.forms.filter((f) => !f.alreadyInSet);

      if (eligibleForms.length === 0) {
        toast.add({ severity: 'info', summary: 'Info', detail: 'All forms already in set', life: 3000 });
        return;
      }

      if (eligibleForms.length === 1) {
        await addMembersToSet([{ wordId: entry.wordId, readingIndex: eligibleForms[0].readingIndex }]);
        return;
      }

      // Multiple forms: open selection dialog
      formPreview.value = preview;
      selectedForms.value = new Set(eligibleForms.map((f) => f.readingIndex));
      showFormDialog.value = true;
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to load word forms'), life: 5000 });
    }
  }

  async function addByWordId() {
    const id = Number(wordIdInput.value.trim());
    if (!id || isNaN(id)) {
      toast.add({ severity: 'warn', summary: 'Invalid', detail: 'Enter a valid WordId', life: 3000 });
      return;
    }
    addingByWordId.value = true;
    try {
      const preview = await $api<WordFormsPreview>(`/admin/word-sets/${setId}/word-forms/${id}`);

      const eligibleForms = preview.forms.filter((f) => !f.alreadyInSet);

      if (eligibleForms.length === 0) {
        toast.add({ severity: 'info', summary: 'Info', detail: 'All forms already in set', life: 3000 });
        return;
      }

      if (eligibleForms.length === 1) {
        await addMembersToSet([{ wordId: id, readingIndex: eligibleForms[0].readingIndex }]);
        wordIdInput.value = '';
        return;
      }

      formPreview.value = preview;
      selectedForms.value = new Set(eligibleForms.map((f) => f.readingIndex));
      showFormDialog.value = true;
      wordIdInput.value = '';
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Word not found'), life: 5000 });
    } finally {
      addingByWordId.value = false;
    }
  }

  async function addSelectedForms() {
    if (!formPreview.value || selectedForms.value.size === 0) return;

    addingForms.value = true;
    const members = Array.from(selectedForms.value).map((ri) => ({
      wordId: formPreview.value!.wordId,
      readingIndex: ri,
    }));

    await addMembersToSet(members);
    addingForms.value = false;
    showFormDialog.value = false;
  }

  async function addMembersToSet(membersToAdd: { wordId: number; readingIndex: number }[]) {
    try {
      const result = await $api<{ added: number; skipped: number }>(`/admin/word-sets/${setId}/members`, { method: 'POST', body: { members: membersToAdd } });
      toast.add({
        severity: 'success',
        summary: 'Added',
        detail: `${result.added} form(s) added${result.skipped > 0 ? `, ${result.skipped} skipped` : ''}`,
        life: 3000,
      });
      if (result.added > 0) {
        const updated = new Set(memberKeySet.value);
        for (const m of membersToAdd) updated.add(`${m.wordId}-${m.readingIndex}`);
        memberKeySet.value = updated;
        if (wordSet.value) {
          wordSet.value = { ...wordSet.value, formCount: wordSet.value.formCount + result.added };
        }
      }
      loadMembers(true);
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to add members'), life: 5000 });
    }
  }

  // Remove
  const removing = ref(false);

  async function doRemoveMembers(membersToRemove: { wordId: number; readingIndex: number }[]) {
    removing.value = true;
    try {
      const result = await $api<{ removed: number }>(`/admin/word-sets/${setId}/members/remove`, { method: 'POST', body: { members: membersToRemove } });
      const removed = result?.removed ?? 0;
      toast.add({
        severity: removed > 0 ? 'success' : 'warn',
        summary: removed > 0 ? 'Removed' : 'Nothing removed',
        detail: `${removed} member(s) removed`,
        life: 3000,
      });
      if (removed > 0) {
        const removeKeys = new Set(membersToRemove.map((m) => `${m.wordId}-${m.readingIndex}`));
        members.value = members.value.filter((m) => !removeKeys.has(m.key));
        totalMembers.value = Math.max(0, totalMembers.value - removed);
        selectedMembers.value = [];
        const updatedKeys = new Set(memberKeySet.value);
        for (const k of removeKeys) updatedKeys.delete(k);
        memberKeySet.value = updatedKeys;
        if (wordSet.value) {
          wordSet.value = { ...wordSet.value, formCount: Math.max(0, wordSet.value.formCount - removed) };
        }
        loadMembers(true);
      }
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to remove members'), life: 5000 });
    } finally {
      removing.value = false;
    }
  }

  function confirmRemoveSelected() {
    if (selectedMembers.value.length === 0) return;
    const membersToRemove = selectedMembers.value.map((m) => ({
      wordId: m.wordId,
      readingIndex: m.readingIndex,
    }));
    confirm.require({
      message: `Remove ${selectedMembers.value.length} member(s) from this word set?`,
      header: 'Confirm Removal',
      icon: 'pi pi-exclamation-triangle',
      acceptProps: { severity: 'danger' },
      accept: () => doRemoveMembers(membersToRemove),
    });
  }

  function removeFromSearch(entry: DictionaryEntry) {
    doRemoveMembers([{ wordId: entry.wordId, readingIndex: entry.readingIndex }]);
  }

  function removeSingleMember(member: WordSetMember) {
    confirm.require({
      message: 'Remove this word from the set?',
      header: 'Confirm',
      icon: 'pi pi-exclamation-triangle',
      acceptProps: { severity: 'danger' },
      accept: () => doRemoveMembers([{ wordId: member.wordId, readingIndex: member.readingIndex }]),
    });
  }

  // Edit metadata
  function openEditMetadata() {
    if (!wordSet.value) return;
    editName.value = wordSet.value.name;
    editSlug.value = wordSet.value.slug;
    editDescription.value = wordSet.value.description || '';
    showEditDialog.value = true;
  }

  async function saveMetadata() {
    if (!editName.value.trim() || !editSlug.value.trim()) {
      toast.add({ severity: 'warn', summary: 'Validation', detail: 'Name and slug required', life: 3000 });
      return;
    }
    savingEdit.value = true;
    try {
      await $api(`/admin/word-sets/${setId}`, {
        method: 'PUT',
        body: {
          name: editName.value.trim(),
          slug: editSlug.value.trim(),
          description: editDescription.value.trim() || null,
        },
      });
      toast.add({ severity: 'success', summary: 'Saved', detail: 'Word set updated', life: 3000 });
      showEditDialog.value = false;
      await loadWordSet();
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Update failed'), life: 5000 });
    } finally {
      savingEdit.value = false;
    }
  }

  function getFormTypeLabel(formType: number) {
    return formType === 0 ? 'Kanji' : 'Kana';
  }

  function getFormTypeSeverity(formType: number): string {
    return formType === 0 ? 'info' : 'secondary';
  }

  onMounted(async () => {
    await Promise.all([loadWordSet(), loadMembers(), loadAllMemberKeys()]);
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <!-- Header -->
    <div class="flex items-center justify-between mb-6">
      <div class="flex items-center">
        <Button icon="pi pi-arrow-left" class="p-button-text mr-2" @click="navigateTo('/dashboard/word-sets')" />
        <div>
          <h1 class="text-3xl font-bold">{{ wordSet?.name || 'Loading...' }}</h1>
          <p v-if="wordSet?.description" class="text-gray-500 dark:text-gray-400 mt-1">{{ wordSet.description }}</p>
        </div>
      </div>
      <div class="flex gap-2 items-center">
        <Tag severity="info" class="text-sm px-3 py-1">{{ wordSet?.wordCount ?? 0 }} words</Tag>
        <Tag severity="secondary" class="text-sm px-3 py-1">{{ wordSet?.formCount ?? 0 }} forms</Tag>
        <Button icon="pi pi-pencil" label="Edit" severity="secondary" size="small" @click="openEditMetadata" />
      </div>
    </div>

    <!-- Search & Add Panel -->
    <div class="border rounded-lg p-4 mb-6 bg-white dark:bg-gray-900">
      <h2 class="text-lg font-semibold mb-3">Search & Add Words</h2>
      <div class="flex gap-3 mb-3">
        <InputText v-model="searchQuery" placeholder="Search by Japanese, reading, or English..." class="flex-1" @input="onSearchInput" />
        <div class="flex gap-1">
          <InputText v-model="wordIdInput" placeholder="WordId" class="w-32" @keyup.enter="addByWordId" />
          <Button icon="pi pi-plus" severity="success" :loading="addingByWordId" @click="addByWordId" />
        </div>
      </div>

      <div v-if="searchLoading" class="flex justify-center py-4">
        <ProgressSpinner style="width: 30px; height: 30px" stroke-width="4" />
      </div>

      <div v-else-if="searchResults.length > 0" class="flex flex-col gap-2 max-h-80 overflow-y-auto">
        <div
          v-for="entry in searchResults"
          :key="`${entry.wordId}-${entry.readingIndex}`"
          class="flex items-center justify-between border rounded p-3 hover:bg-gray-50 dark:hover:bg-gray-800"
        >
          <div class="flex-1 min-w-0">
            <div class="flex items-center gap-2">
              <span class="text-lg font-bold">{{ entry.text }}</span>
              <Tag v-if="entry.frequencyRank > 0" severity="secondary" class="text-xs">#{{ entry.frequencyRank }}</Tag>
            </div>
            <div class="text-sm text-gray-500 dark:text-gray-400 truncate">
              {{ entry.meanings.slice(0, 3).join('; ') }}
            </div>
            <div class="flex gap-1 mt-1">
              <Tag v-for="pos in entry.partsOfSpeech.slice(0, 3)" :key="pos" severity="info" class="text-xs">
                {{ pos }}
              </Tag>
            </div>
          </div>
          <Button
            v-if="memberKeySet.has(`${entry.wordId}-${entry.readingIndex}`)"
            icon="pi pi-trash"
            size="small"
            severity="danger"
            class="ml-3 flex-shrink-0"
            @click="removeFromSearch(entry)"
          />
          <Button v-else icon="pi pi-plus" size="small" severity="success" class="ml-3 flex-shrink-0" @click="addWord(entry)" />
        </div>
      </div>

      <div v-else-if="searchQuery.trim().length > 0 && !searchLoading" class="text-center text-gray-400 py-4">No results found</div>
    </div>

    <!-- Members Table -->
    <div>
      <div class="flex items-center justify-between mb-3">
        <h2 class="text-lg font-semibold">Members</h2>
        <div class="flex gap-2 items-center">
          <Select
            v-model="sortBy"
            :options="[
              { label: 'Position', value: 'position' },
              { label: 'Frequency Rank', value: 'globalFreq' },
            ]"
            option-label="label"
            option-value="value"
            class="w-44"
            @change="onSortChange"
          />
          <Select
            v-model="sortOrder"
            :options="[
              { label: 'Ascending', value: 'asc' },
              { label: 'Descending', value: 'desc' },
            ]"
            option-label="label"
            option-value="value"
            class="w-36"
            @change="onSortChange"
          />
          <Button
            v-if="selectedMembers.length > 0"
            :label="`Remove Selected (${selectedMembers.length})`"
            icon="pi pi-trash"
            severity="danger"
            size="small"
            @click="confirmRemoveSelected"
          />
        </div>
      </div>

      <DataTable
        v-model:selection="selectedMembers"
        :value="members"
        :loading="loadingMembers"
        :lazy="true"
        :paginator="true"
        :rows="rowsPerPage"
        :total-records="totalMembers"
        :rows-per-page-options="[25, 50, 100]"
        :first="currentPage * rowsPerPage"
        data-key="key"
        striped-rows
        class="shadow-md"
        @page="onPage"
      >
        <Column selection-mode="multiple" style="width: 50px" />
        <Column field="position" header="#" style="width: 70px" />
        <Column header="Word" style="min-width: 200px">
          <template #body="{ data }">
            <NuxtLink :to="`/vocabulary/${data.wordId}/${data.readingIndex}`" target="_blank" class="text-blue-500 hover:underline">
              <span class="text-lg font-bold">{{ data.text }}</span>
            </NuxtLink>
            <div v-if="data.rubyText && data.text !== data.rubyText" class="text-xs text-gray-400" v-html="data.rubyText" />
          </template>
        </Column>
        <Column header="Meanings" style="min-width: 250px">
          <template #body="{ data }">
            <span class="text-sm">{{ data.meanings.slice(0, 3).join('; ') }}</span>
          </template>
        </Column>
        <Column header="POS" style="width: 180px">
          <template #body="{ data }">
            <div class="flex flex-wrap gap-1">
              <Tag v-for="pos in data.partsOfSpeech" :key="pos" severity="info" class="text-xs">{{ pos }}</Tag>
            </div>
          </template>
        </Column>
        <Column field="frequencyRank" header="Freq" style="width: 90px">
          <template #body="{ data }">
            <span v-if="data.frequencyRank > 0">{{ data.frequencyRank }}</span>
            <span v-else class="text-gray-300">-</span>
          </template>
        </Column>
        <Column header="" style="width: 60px">
          <template #body="{ data }">
            <Button icon="pi pi-trash" size="small" severity="danger" text @click="removeSingleMember(data)" />
          </template>
        </Column>
      </DataTable>
    </div>

    <!-- Form Selection Dialog -->
    <Dialog v-model:visible="showFormDialog" header="Select Forms to Add" :modal="true" class="w-full md:w-2/3 lg:w-1/2">
      <div v-if="formPreview" class="mb-4">
        <div class="text-sm text-gray-500 dark:text-gray-400 mb-2">POS: {{ formPreview.partsOfSpeech.join(', ') }}</div>
        <div class="text-sm mb-4">
          {{ formPreview.meanings.slice(0, 5).join('; ') }}
        </div>

        <div class="flex flex-col gap-2">
          <div
            v-for="form in formPreview.forms"
            :key="form.readingIndex"
            class="flex items-center gap-3 border rounded p-3"
            :class="{ 'opacity-50': form.alreadyInSet }"
          >
            <Checkbox
              :model-value="selectedForms.has(form.readingIndex)"
              :binary="true"
              :disabled="form.alreadyInSet"
              @update:model-value="
                (val: boolean) => {
                  const s = new Set(selectedForms);
                  if (val) s.add(form.readingIndex);
                  else s.delete(form.readingIndex);
                  selectedForms = s;
                }
              "
            />
            <Tag :severity="getFormTypeSeverity(form.formType)">
              {{ getFormTypeLabel(form.formType) }}
            </Tag>
            <span class="text-lg font-bold">{{ form.text }}</span>
            <span v-if="form.rubyText && form.rubyText !== form.text" class="text-sm text-gray-400">
              {{ form.rubyText }}
            </span>
            <Tag v-if="form.frequencyRank > 0" severity="secondary" class="text-xs">#{{ form.frequencyRank }}</Tag>
            <Tag v-if="form.alreadyInSet" severity="warn" class="text-xs">Already in set</Tag>
          </div>
        </div>
      </div>

      <template #footer>
        <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showFormDialog = false" />
        <Button label="Add Selected" icon="pi pi-check" :loading="addingForms" :disabled="selectedForms.size === 0" @click="addSelectedForms" />
      </template>
    </Dialog>

    <!-- Edit Metadata Dialog -->
    <Dialog v-model:visible="showEditDialog" header="Edit Word Set" :modal="true" class="w-full md:w-1/2">
      <div class="flex flex-col gap-4">
        <div>
          <label class="block text-sm font-medium mb-1">Name</label>
          <InputText v-model="editName" class="w-full" @keyup.enter="saveMetadata" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Slug</label>
          <InputText v-model="editSlug" class="w-full" @keyup.enter="saveMetadata" />
        </div>
        <div>
          <label class="block text-sm font-medium mb-1">Description</label>
          <Textarea v-model="editDescription" class="w-full" rows="3" />
        </div>
      </div>
      <template #footer>
        <Button label="Cancel" icon="pi pi-times" class="p-button-text" @click="showEditDialog = false" />
        <Button label="Save" icon="pi pi-check" :loading="savingEdit" @click="saveMetadata" />
      </template>
    </Dialog>
  </div>
</template>

<style scoped></style>
