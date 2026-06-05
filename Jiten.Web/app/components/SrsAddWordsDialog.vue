<script setup lang="ts">
  import { useSrsStore } from '~/stores/srsStore';
  import { useToast } from 'primevue/usetoast';
  import { debounce } from 'perfect-debounce';
  import type { DictionarySearchResult, DictionaryEntry } from '~/types';

  const props = defineProps<{
    visible: boolean;
    deckId: number;
  }>();

  const emit = defineEmits(['update:visible', 'words-added']);

  const localVisible = ref(props.visible);
  watch(() => props.visible, (v) => { localVisible.value = v; });
  watch(localVisible, (v) => { emit('update:visible', v); });

  const srsStore = useSrsStore();
  const toast = useToast();
  const convertToRuby = useConvertToRuby();
  const { $api } = useNuxtApp();

  const activeTab = ref('search');

  // Search tab
  const query = ref('');
  const results = ref<DictionaryEntry[]>([]);
  const searching = ref(false);
  const addedKeys = ref(new Set<string>());
  const addingKey = ref<string | null>(null);
  let addedAny = false;

  async function loadExistingKeys() {
    try {
      const keys = await $api<{ wordId: number; readingIndex: number }[]>(`srs/study-decks/${props.deckId}/word-keys`);
      const set = new Set<string>();
      for (const k of keys) set.add(`${k.wordId}-${k.readingIndex}`);
      addedKeys.value = set;
    } catch { /* non-critical */ }
  }

  function wordKey(entry: DictionaryEntry) {
    return `${entry.wordId}-${entry.readingIndex}`;
  }

  function isAdded(entry: DictionaryEntry) {
    return addedKeys.value.has(wordKey(entry));
  }

  const doSearch = debounce(async (q: string) => {
    if (!q.trim()) { results.value = []; return; }
    searching.value = true;
    try {
      const data = await $api<DictionarySearchResult>('vocabulary/search', {
        params: { query: q.trim(), limit: 20 },
      });
      results.value = data.results.length > 0 ? data.results : data.dictionaryResults;
    } catch { results.value = []; }
    finally { searching.value = false; }
  }, 400);

  watch(query, (v) => doSearch(v));

  async function addWord(entry: DictionaryEntry) {
    const key = wordKey(entry);
    addingKey.value = key;
    try {
      await srsStore.addDeckWord(props.deckId, entry.wordId, entry.readingIndex, 1);
      addedKeys.value.add(key);
      addedAny = true;
      toast.add({ severity: 'success', summary: `Added "${entry.text}"`, life: 1500 });
    } catch {
      toast.add({ severity: 'error', summary: 'Failed to add word', life: 3000 });
    } finally { addingKey.value = null; }
  }

  // Paste tab
  type PasteStep = 'input' | 'preview';
  const pasteStep = ref<PasteStep>('input');
  const pasteParseFullText = ref(false);
  const pasteText = ref('');

  const looksLikeFullText = computed(() => {
    const lines = pasteText.value.split('\n').filter(l => l.trim().length > 0);
    return lines.filter(l => l.trim().length > 20).length >= 3;
  });

  watch(looksLikeFullText, (isFullText) => {
    if (isFullText) pasteParseFullText.value = true;
  });
  const pasteUploading = ref(false);
  const pasteCommitting = ref(false);
  const pastePreviewToken = ref('');
  const pasteMatched = ref<{ wordId: number; readingIndex: number; text: string; reading: string }[]>([]);
  const pasteUnmatched = ref<string[]>([]);
  const pasteTotalLines = ref(0);
  const pasteExcludedWordIds = ref(new Set<number>());
  const pasteIncludedCount = computed(() => pasteMatched.value.length - pasteExcludedWordIds.value.size);

  function togglePasteExclude(wordId: number) {
    const s = new Set(pasteExcludedWordIds.value);
    if (s.has(wordId)) s.delete(wordId); else s.add(wordId);
    pasteExcludedWordIds.value = s;
  }

  async function previewPasteList() {
    const lines = pasteText.value.split('\n').map(l => l.trim()).filter(l => l.length > 0);
    if (lines.length === 0) { toast.add({ severity: 'warn', summary: 'No words entered', life: 2000 }); return; }
    pasteUploading.value = true;
    try {
      const result = await srsStore.importPreviewText(lines, pasteParseFullText.value);
      pastePreviewToken.value = result.previewToken;
      pasteMatched.value = result.matched;
      pasteUnmatched.value = result.unmatched;
      pasteTotalLines.value = result.totalLines;
      pasteStep.value = 'preview';
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Preview failed', detail: String(error?.data || error?.message || 'Unknown error'), life: 5000 });
    } finally { pasteUploading.value = false; }
  }

  async function commitPasteImport() {
    pasteCommitting.value = true;
    try {
      const excluded = pasteExcludedWordIds.value.size > 0 ? [...pasteExcludedWordIds.value] : undefined;
      await srsStore.importToExistingDeck(props.deckId, pastePreviewToken.value, excluded);
      addedAny = true;
      toast.add({ severity: 'success', summary: `Added ${pasteIncludedCount.value} words`, life: 3000 });
      resetPaste();
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Import failed', detail: String(error?.data || error?.message || 'Unknown error'), life: 5000 });
    } finally { pasteCommitting.value = false; }
  }

  function resetPaste() {
    pasteStep.value = 'input';
    pasteParseFullText.value = false;
    pasteText.value = '';
    pastePreviewToken.value = '';
    pasteMatched.value = [];
    pasteUnmatched.value = [];
    pasteTotalLines.value = 0;
    pasteExcludedWordIds.value = new Set();
  }

  // File tab
  type FileStep = 'upload' | 'preview';
  const fileStep = ref<FileStep>('upload');
  const fileParseFullText = ref(false);
  const importFile = ref<File | null>(null);
  const fileUploading = ref(false);
  const fileCommitting = ref(false);
  const filePreviewToken = ref('');
  const fileMatched = ref<{ wordId: number; readingIndex: number; text: string; reading: string }[]>([]);
  const fileUnmatched = ref<string[]>([]);
  const fileTotalLines = ref(0);
  const fileExcludedWordIds = ref(new Set<number>());
  const fileIncludedCount = computed(() => fileMatched.value.length - fileExcludedWordIds.value.size);

  function toggleFileExclude(wordId: number) {
    const s = new Set(fileExcludedWordIds.value);
    if (s.has(wordId)) s.delete(wordId); else s.add(wordId);
    fileExcludedWordIds.value = s;
  }

  const fileIsFullTextOnly = computed(() => importFile.value ? isFullTextOnlyFile(importFile.value.name) : false);

  const dragging = ref(false);

  function onFileSelect(event: any) {
    const files = event.target?.files || event.files;
    if (files && files.length > 0) {
      importFile.value = files[0];
      if (fileIsFullTextOnly.value) fileParseFullText.value = true;
    }
  }

  function onFileDrop(event: DragEvent) {
    dragging.value = false;
    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      importFile.value = files[0];
      if (fileIsFullTextOnly.value) fileParseFullText.value = true;
    }
  }

  async function uploadFileAndPreview() {
    if (!importFile.value) return;
    fileUploading.value = true;
    try {
      const useFullText = fileParseFullText.value || fileIsFullTextOnly.value;
      const result = await srsStore.importPreview(importFile.value, useFullText);
      filePreviewToken.value = result.previewToken;
      fileMatched.value = result.matched;
      fileUnmatched.value = result.unmatched;
      fileTotalLines.value = result.totalLines;
      fileStep.value = 'preview';
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Upload failed', detail: String(error?.data || error?.message || 'Unknown error'), life: 5000 });
    } finally { fileUploading.value = false; }
  }

  async function commitFileImport() {
    fileCommitting.value = true;
    try {
      const excluded = fileExcludedWordIds.value.size > 0 ? [...fileExcludedWordIds.value] : undefined;
      await srsStore.importToExistingDeck(props.deckId, filePreviewToken.value, excluded);
      addedAny = true;
      toast.add({ severity: 'success', summary: `Added ${fileIncludedCount.value} words`, life: 3000 });
      resetFile();
    } catch (error: any) {
      toast.add({ severity: 'error', summary: 'Import failed', detail: String(error?.data || error?.message || 'Unknown error'), life: 5000 });
    } finally { fileCommitting.value = false; }
  }

  function resetFile() {
    fileStep.value = 'upload';
    fileParseFullText.value = false;
    importFile.value = null;
    filePreviewToken.value = '';
    fileMatched.value = [];
    fileUnmatched.value = [];
    fileTotalLines.value = 0;
    fileExcludedWordIds.value = new Set();
  }

  watch(localVisible, (v) => {
    if (v) {
      loadExistingKeys();
    } else {
      if (addedAny) { emit('words-added'); addedAny = false; }
      query.value = '';
      results.value = [];
      addedKeys.value = new Set();
      activeTab.value = 'search';
      resetPaste();
      resetFile();
    }
  });

  const parseModeOptions = [
    { label: 'Word list', value: 'wordlist' },
    { label: 'Full text', value: 'fulltext' },
  ];
</script>

<template>
  <Dialog
    v-model:visible="localVisible"
    header="Add Words"
    modal
    class="w-full"
    :style="{ width: '700px', maxWidth: '95vw', height: '85vh', maxHeight: '600px' }"
    :dismissable-mask="true"
    :pt="{
      root: { class: 'max-sm:!m-2 max-sm:!max-h-[95vh]' },
      content: { style: 'flex: 1; display: flex; flex-direction: column; overflow: hidden; padding: 0 1.25rem 1.25rem' },
    }"
  >
    <div class="flex flex-col flex-1 overflow-hidden">
      <Tabs v-model:value="activeTab" class="flex flex-col flex-1 overflow-hidden">
        <TabList>
          <Tab value="search">Search</Tab>
          <Tab value="paste">Text</Tab>
          <Tab value="file">File</Tab>
        </TabList>

        <TabPanels class="flex-1 overflow-hidden !p-0">
          <!-- Search -->
          <TabPanel value="search" class="flex flex-col gap-3 h-full overflow-hidden pt-4">
            <div class="relative shrink-0">
              <InputText
                v-model="query"
                placeholder="Search Japanese, romaji, or English..."
                class="w-full"
              />
              <ProgressSpinner
                v-if="searching"
                style="width: 20px; height: 20px; position: absolute; right: 12px; top: 50%; transform: translateY(-50%)"
              />
            </div>

            <div v-if="results.length === 0 && query.trim() && !searching" class="flex-1 flex items-center justify-center text-gray-500">
              No results for "{{ query.trim() }}"
            </div>

            <div v-if="results.length > 0" class="flex flex-col gap-2 flex-1 overflow-y-auto">
              <div
                v-for="entry in results"
                :key="wordKey(entry)"
                class="flex items-center gap-2 sm:gap-3 px-3 sm:px-4 py-2.5 sm:py-3 rounded-lg border border-gray-200 dark:border-gray-700 hover:bg-gray-50 dark:hover:bg-gray-800/40 transition-colors"
              >
                <div class="flex-1 min-w-0">
                  <div class="flex items-center gap-3 flex-wrap">
                    <span v-if="entry.primaryKanjiText" class="text-lg font-noto-sans font-medium" lang="ja" v-html="convertToRuby(entry.primaryKanjiText)" />
                    <span
                      v-if="entry.primaryKanjiText"
                      class="text-sm text-gray-500 dark:text-gray-400 font-noto-sans"
                      lang="ja"
                    >{{ entry.text }}</span>
                    <span v-else class="text-lg font-noto-sans font-medium" lang="ja" v-html="convertToRuby(entry.rubyText)" />
                    <span
                      v-for="pos in entry.partsOfSpeech.slice(0, 2)"
                      :key="pos"
                      class="inline-block rounded-full px-2 py-0.5 text-xs bg-purple-100 text-purple-700 dark:bg-purple-900/30 dark:text-purple-300"
                    >{{ pos }}</span>
                  </div>
                  <div class="text-sm text-gray-600 dark:text-gray-400 line-clamp-1 mt-0.5">
                    {{ entry.meanings.join('; ') }}
                  </div>
                </div>
                <div class="flex items-center gap-2 shrink-0">
                  <span v-if="entry.frequencyRank < 2147483647" class="text-xs text-gray-400">#{{ entry.frequencyRank.toLocaleString() }}</span>
                  <Button
                    :label="isAdded(entry) ? 'Add again' : 'Add'"
                    :icon="isAdded(entry) ? undefined : 'pi pi-plus'"
                    :severity="isAdded(entry) ? 'secondary' : undefined"
                    size="small"
                    :loading="addingKey === wordKey(entry)"
                    @click="addWord(entry)"
                  />
                </div>
              </div>
            </div>
          </TabPanel>

          <!-- Paste List -->
          <TabPanel value="paste" class="flex flex-col gap-3 h-full overflow-hidden pt-4">
            <template v-if="pasteStep === 'input'">
              <Textarea
                v-model="pasteText"
                placeholder="Paste Japanese words here, one per line..."
                class="w-full font-noto-sans flex-1 !resize-none" lang="ja"
                :style="{ minHeight: '120px' }"
              />
              <div class="shrink-0 flex flex-col gap-3">
                <div>
                  <SelectButton
                    :model-value="pasteParseFullText ? 'fulltext' : 'wordlist'"
                    :options="parseModeOptions"
                    option-label="label"
                    option-value="value"
                    @update:model-value="(v: string) => pasteParseFullText = v === 'fulltext'"
                  />
                  <p class="text-xs text-gray-400 mt-1">{{ pasteParseFullText ? 'Extracts all vocabulary from sentences' : 'One word per line' }}</p>
                </div>
                <Button
                  label="Preview"
                  icon="pi pi-eye"
                  class="w-full"
                  :loading="pasteUploading"
                  :disabled="!pasteText.trim()"
                  @click="previewPasteList"
                />
              </div>
            </template>

            <template v-if="pasteStep === 'preview'">
              <div class="shrink-0 flex items-center justify-between">
                <Button icon="pi pi-arrow-left" severity="secondary" text size="small" @click="pasteStep = 'input'" />
                <span class="text-sm text-gray-500">{{ pasteTotalLines }} lines parsed</span>
              </div>
              <div class="flex-1 overflow-y-auto">
                <SrsImportPreview
                  :matched="pasteMatched"
                  :unmatched="pasteUnmatched"
                  :excluded-word-ids="pasteExcludedWordIds"
                  @toggle-exclude="togglePasteExclude"
                />
              </div>
              <Button
                :label="`Add ${pasteIncludedCount} words`"
                class="w-full shrink-0"
                :loading="pasteCommitting"
                :disabled="pasteIncludedCount === 0"
                @click="commitPasteImport"
              />
            </template>
          </TabPanel>

          <!-- File -->
          <TabPanel value="file" class="flex flex-col gap-3 h-full overflow-hidden pt-4">
            <template v-if="fileStep === 'upload'">
              <div class="flex-1 flex flex-col justify-center gap-4">
                <div
                  :class="['flex flex-col items-center justify-center gap-2 sm:gap-3 p-5 sm:p-8 rounded-xl border-2 border-dashed transition-colors cursor-pointer',
                    dragging ? 'border-purple-500 bg-purple-50 dark:bg-purple-900/20' : 'border-gray-300 dark:border-gray-600 hover:border-purple-400 dark:hover:border-purple-500']"
                  @click="($refs.fileInput as HTMLInputElement)?.click()"
                  @dragover.prevent="dragging = true"
                  @dragleave="dragging = false"
                  @drop.prevent="onFileDrop"
                >
                  <Icon name="material-symbols:upload-file" size="36" class="text-gray-400" />
                  <div class="text-sm text-gray-500 text-center">
                    <span class="font-medium text-purple-600 dark:text-purple-400">Choose a file</span> or drag and drop
                  </div>
                  <div class="text-xs text-gray-400">{{ IMPORT_ACCEPT_EXTENSIONS.join(', ') }}</div>
                  <input
                    ref="fileInput"
                    type="file"
                    :accept="IMPORT_ACCEPT_ATTR"
                    class="hidden"
                    @change="onFileSelect"
                  />
                </div>

                <div v-if="importFile" class="flex items-center gap-2 sm:gap-3 px-3 sm:px-4 py-2 sm:py-3 rounded-lg border border-gray-200 dark:border-gray-700">
                  <Icon name="material-symbols:description" size="20" class="text-gray-400 shrink-0" />
                  <div class="flex-1 min-w-0">
                    <div class="text-sm font-medium truncate">{{ importFile.name }}</div>
                    <div class="text-xs text-gray-400">{{ (importFile.size / 1024).toFixed(1) }} KB</div>
                  </div>
                  <Button icon="pi pi-times" severity="secondary" text size="small" @click="importFile = null; fileParseFullText = false" />
                </div>

                <div v-if="importFile && !fileIsFullTextOnly">
                  <SelectButton
                    :model-value="fileParseFullText ? 'fulltext' : 'wordlist'"
                    :options="parseModeOptions"
                    option-label="label"
                    option-value="value"
                    @update:model-value="(v: string) => fileParseFullText = v === 'fulltext'"
                  />
                  <p class="text-xs text-gray-400 mt-1">{{ fileParseFullText ? 'Extracts all vocabulary from sentences' : 'One word per line' }}</p>
                </div>
                <p v-if="importFile && fileIsFullTextOnly" class="text-xs text-gray-400">
                  All vocabulary will be extracted from this file.
                </p>
              </div>

              <Button
                label="Upload & Preview"
                icon="pi pi-upload"
                class="w-full shrink-0"
                :loading="fileUploading"
                :disabled="!importFile"
                @click="uploadFileAndPreview"
              />
            </template>

            <template v-if="fileStep === 'preview'">
              <div class="shrink-0 flex items-center justify-between">
                <Button icon="pi pi-arrow-left" severity="secondary" text size="small" @click="fileStep = 'upload'" />
                <span class="text-sm text-gray-500">{{ fileTotalLines }} lines parsed</span>
              </div>
              <div class="flex-1 overflow-y-auto">
                <SrsImportPreview
                  :matched="fileMatched"
                  :unmatched="fileUnmatched"
                  :excluded-word-ids="fileExcludedWordIds"
                  @toggle-exclude="toggleFileExclude"
                />
              </div>
              <Button
                :label="`Add ${fileIncludedCount} words`"
                class="w-full shrink-0"
                :loading="fileCommitting"
                :disabled="fileIncludedCount === 0"
                @click="commitFileImport"
              />
            </template>
          </TabPanel>
        </TabPanels>
      </Tabs>
    </div>
  </Dialog>
</template>
