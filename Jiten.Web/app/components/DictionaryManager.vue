<script setup lang="ts">
  import { Button, ProgressBar, Select, InputText } from 'primevue';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';
  import { JMDICT_DICTIONARY_ID } from '~/composables/useYomitanDictionary';
  import type { ImportProgress, DictionaryMeta } from '~/composables/useYomitanDictionary';

  const toast = useToast();
  const confirm = useConfirm();
  const { dictionaries, importDictionary, removeDictionary, updateDictionary, reorderDictionaries, loadDictionaries } = useYomitanDictionary();

  const importing = ref(false);
  const importProgress = ref<ImportProgress | null>(null);
  const fileInputRef = ref<HTMLInputElement | null>(null);
  const dragOver = ref(false);

  onMounted(() => loadDictionaries());

  const progressPercent = computed(() => {
    if (!importProgress.value || importProgress.value.total === 0) return 0;
    return Math.round((importProgress.value.current / importProgress.value.total) * 100);
  });

  const progressLabel = computed(() => {
    if (!importProgress.value) return '';
    const { phase, current, total } = importProgress.value;
    if (phase === 'reading') return 'Reading ZIP file...';
    if (phase === 'parsing') return `Parsing term banks (${current + 1}/${total})...`;
    if (phase === 'storing') return `Storing entries (${current + 1}/${total})...`;
    return '';
  });

  function isJmDict(dict: DictionaryMeta): boolean {
    return dict.id === JMDICT_DICTIONARY_ID;
  }

  function openFilePicker() {
    fileInputRef.value?.click();
  }

  function onFileInputChange(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (file) processFile(file);
  }

  function onDrop(event: DragEvent) {
    dragOver.value = false;
    const file = event.dataTransfer?.files?.[0];
    if (file) processFile(file);
  }

  async function processFile(file: File) {
    if (!file.name.endsWith('.zip')) {
      toast.add({ severity: 'error', summary: 'Invalid File', detail: 'Please select a Yomitan dictionary ZIP file.', life: 5000 });
      return;
    }

    importing.value = true;
    importProgress.value = null;

    try {
      const meta = await importDictionary(file, (p) => {
        importProgress.value = p;
      });

      toast.add({
        severity: 'success',
        summary: 'Dictionary Imported',
        detail: `${meta.name} — ${meta.entryCount.toLocaleString()} entries imported.`,
        life: 5000,
      });
    } catch (err: any) {
      console.error('Dictionary import failed:', err);
      toast.add({ severity: 'error', summary: 'Import Failed', detail: err.message || 'Unknown error', life: 5000 });
    } finally {
      importing.value = false;
      importProgress.value = null;
    }
  }

  function confirmRemove(dict: DictionaryMeta) {
    confirm.require({
      message: `Remove "${dict.name}" and all its ${dict.entryCount.toLocaleString()} entries? This cannot be undone.`,
      header: 'Remove Dictionary',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: {
        label: 'Cancel',
        severity: 'secondary',
        outlined: true,
      },
      acceptProps: {
        label: 'Remove',
        severity: 'danger',
      },
      accept: () => doRemove(dict),
    });
  }

  async function doRemove(dict: DictionaryMeta) {
    try {
      await removeDictionary(dict.id);
      await loadDictionaries();
      toast.add({ severity: 'info', summary: 'Dictionary Removed', detail: `${dict.name} has been removed.`, life: 3000 });
    } catch (err: any) {
      toast.add({ severity: 'error', summary: 'Error', detail: err.message, life: 5000 });
    }
  }

  async function moveUp(index: number) {
    if (index <= 0) return;
    const ids = dictionaries.value.map((d) => d.id);
    [ids[index - 1], ids[index]] = [ids[index], ids[index - 1]];
    await reorderDictionaries(ids);
  }

  async function moveDown(index: number) {
    if (index >= dictionaries.value.length - 1) return;
    const ids = dictionaries.value.map((d) => d.id);
    [ids[index], ids[index + 1]] = [ids[index + 1], ids[index]];
    await reorderDictionaries(ids);
  }

  const dictListRef = ref<HTMLElement | null>(null);
  const { dragIndex, dropIndex, handlePointerDown: handleDictPointerDown } = useTouchReorder({
    containerRef: dictListRef,
    async onReorder(from, to) {
      const ids = dictionaries.value.map((d) => d.id);
      const [moved] = ids.splice(from, 1);
      ids.splice(to, 0, moved);
      await reorderDictionaries(ids);
    },
  });

  const modeOptions = [
    { label: 'Always show', value: 'always' },
    { label: 'Fallback', value: 'fallback' },
    { label: 'Disabled', value: 'never' },
  ];

  async function onModeChange(dict: DictionaryMeta, mode: 'always' | 'fallback' | 'never') {
    await updateDictionary(dict.id, { mode });
  }

  const renamingId = ref<string | null>(null);
  const renameValue = ref('');

  function startRename(dict: DictionaryMeta) {
    renamingId.value = dict.id;
    renameValue.value = dict.name;
  }

  function cancelRename() {
    renamingId.value = null;
    renameValue.value = '';
  }

  async function confirmRename() {
    if (!renamingId.value) return;
    const trimmed = renameValue.value.trim();
    if (trimmed) {
      await updateDictionary(renamingId.value, { name: trimmed });
    }
    renamingId.value = null;
    renameValue.value = '';
  }

  function formatDate(timestamp: number): string {
    return new Date(timestamp).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
  }
</script>

<template>
  <div class="flex flex-col gap-4">
    <input ref="fileInputRef" type="file" accept=".zip" class="hidden" @change="onFileInputChange" />

    <!-- Mobile: select file button only -->
    <div class="sm:hidden flex items-center gap-3">
      <template v-if="importing">
        <i class="pi pi-spin pi-spinner text-primary" />
        <div class="flex-1 flex flex-col gap-1">
          <span class="text-sm text-gray-600 dark:text-gray-300">{{ progressLabel }}</span>
          <ProgressBar :value="progressPercent" style="height: 0.5rem" />
        </div>
      </template>
      <Button v-else label="Select File" icon="pi pi-folder-open" severity="secondary" outlined size="small" @click="openFilePicker" />
    </div>

    <!-- Desktop: drag & drop zone -->
    <div
      class="hidden sm:block border-2 border-dashed rounded-xl p-6 text-center transition-colors"
      :class="dragOver
        ? 'border-primary bg-primary-50 dark:bg-primary-900/20'
        : 'border-surface-300 dark:border-surface-600'"
      @dragover.prevent="dragOver = true"
      @dragleave.prevent="dragOver = false"
      @drop.prevent="onDrop"
    >
      <template v-if="importing">
        <div class="flex flex-col items-center gap-3">
          <i class="pi pi-spin pi-spinner text-2xl text-primary" />
          <span class="text-sm text-gray-600 dark:text-gray-300">{{ progressLabel }}</span>
          <ProgressBar :value="progressPercent" class="w-full max-w-xs" style="height: 0.5rem" />
        </div>
      </template>
      <template v-else>
        <i class="pi pi-upload text-3xl text-gray-400 dark:text-gray-400 mb-2" />
        <p class="text-sm text-gray-600 dark:text-gray-300 mb-3">
          Drag and drop a Yomitan dictionary (.zip) here, or
        </p>
        <Button label="Select File" icon="pi pi-folder-open" severity="secondary" outlined size="small" @click="openFilePicker" />
      </template>
    </div>

    <!-- Dictionary List -->
    <div class="flex flex-col gap-3">
      <div class="text-xs font-bold text-gray-500 dark:text-gray-400 uppercase tracking-widest">
        Dictionaries ({{ dictionaries.length }})
      </div>

      <div ref="dictListRef" class="flex flex-col gap-3">
      <div
        v-for="(dict, index) in dictionaries"
        :key="dict.id"
        class="border rounded-lg p-4 bg-surface-0 dark:bg-surface-900 flex flex-col sm:flex-row sm:items-center gap-3 transition-colors"
        :class="{
          'border-primary bg-primary-50/50 dark:bg-primary-900/20': dropIndex === index && dragIndex !== index,
          'opacity-50': dragIndex === index,
          'border-surface-200 dark:border-surface-700': dropIndex !== index && dragIndex !== index,
        }"
      >
        <!-- Arrows + drag handle + priority badge -->
        <div class="flex items-center gap-1 shrink-0">
          <div class="flex flex-col gap-0.5">
            <Button icon="pi pi-chevron-up" text rounded size="small" :disabled="index === 0" @click="moveUp(index)" class="!w-6 !h-6" />
            <Button icon="pi pi-chevron-down" text rounded size="small" :disabled="index === dictionaries.length - 1" @click="moveDown(index)" class="!w-6 !h-6" />
          </div>
          <span class="cursor-grab active:cursor-grabbing" style="touch-action: none" @pointerdown="handleDictPointerDown($event, index)">
            <i class="pi pi-bars text-gray-400 dark:text-gray-400 text-xs" />
          </span>
          <span class="w-7 h-7 rounded-full bg-primary-100 dark:bg-primary-900 text-primary-700 dark:text-primary-300 text-xs font-bold flex items-center justify-center">
            {{ index + 1 }}
          </span>
        </div>

        <!-- Info -->
        <div class="flex-1 min-w-0">
          <div class="font-semibold text-sm text-gray-800 dark:text-gray-200 truncate flex items-center gap-2">
            <template v-if="renamingId === dict.id">
              <InputText
                v-model="renameValue"
                class="text-sm !py-0.5 !px-1.5 w-48"
                size="small"
                autofocus
                @keydown.enter="confirmRename"
                @keydown.escape="cancelRename"
              />
              <Button icon="pi pi-check" text rounded size="small" class="!w-6 !h-6" @click="confirmRename" />
              <Button icon="pi pi-times" text rounded size="small" class="!w-6 !h-6" @click="cancelRename" />
            </template>
            <template v-else>
              {{ dict.name }}
              <span v-if="isJmDict(dict)" class="text-[10px] font-bold uppercase tracking-wider px-1.5 py-0.5 rounded bg-blue-100 text-blue-700 dark:bg-blue-900/40 dark:text-blue-300">
                Built-in
              </span>
              <Button v-if="!isJmDict(dict)" icon="pi pi-pencil" text rounded size="small" class="!w-5 !h-5 opacity-40 hover:opacity-100" @click="startRename(dict)" />
            </template>
          </div>
          <div class="text-xs text-gray-500 dark:text-gray-400">
            <template v-if="!isJmDict(dict)">
              {{ dict.entryCount.toLocaleString() }} entries
              <span v-if="dict.revision"> · {{ dict.revision }}</span>
              · Added {{ formatDate(dict.addedAt) }}
            </template>
            <template v-else>
              Default Japanese-English dictionary based on JMDict.
            </template>
          </div>
        </div>

        <!-- Controls -->
        <div class="flex items-center gap-2 shrink-0">
          <Select
            :modelValue="dict.mode"
            @update:modelValue="(v: any) => onModeChange(dict, v)"
            :options="modeOptions"
            optionLabel="label"
            optionValue="value"
            class="text-xs w-48"
            size="small"
          />
          <Button v-if="!isJmDict(dict)" icon="pi pi-trash" severity="danger" text rounded size="small" @click="confirmRemove(dict)" />
          <div v-else class="w-8" />
        </div>
      </div>
      </div>
    </div>
  </div>
</template>
