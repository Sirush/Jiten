<script setup lang="ts">
  import { ref } from 'vue';
  import InputNumber from 'primevue/inputnumber';
  import Button from 'primevue/button';
  import Checkbox from 'primevue/checkbox';
  import { useToast } from 'primevue/usetoast';
  import InputText from 'primevue/inputtext';
  import InputGroup from 'primevue/inputgroup';

  useHead({
    title: 'Add Media from Jimaku - Jiten',
  });

  definePageMeta({
    middleware: ['auth'],
  });

  interface JimakuFileDto {
    url: string;
    name: string;
    size: number;
    last_modified: string;
  }

  interface JimakuResult {
    entry: {
      id: number;
      name: string;
      japanese_name?: string | null;
      english_name?: string | null;
    };
    files: JimakuFileDto[] | null;
  }

  interface FileRow extends JimakuFileDto {
    selected: boolean;
    title: string | null;
  }

  const toast = useToast();
  const startId = ref(9000);
  const endId = ref(15000);
  const currentId = ref<number | null>(null);
  const jumpId = ref<number | null>(null);
  const jimakuData = ref<JimakuResult | null>(null);
  const files = ref<FileRow[]>([]);
  const prefix = ref('');
  const suffix = ref('');
  const regexPattern = ref('');
  const fetching = ref(false);
  const submitting = ref(false);
  const { $api } = useNuxtApp();

  const selectedCount = computed(() => files.value.filter((f) => f.selected).length);

  const naturalCompare = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });

  const fetchId = async (id: number) => {
    currentId.value = id;
    fetching.value = true;
    try {
      const response = (await $api(`admin/get-jimaku/${id}`)) as JimakuResult;
      jimakuData.value = response;
      files.value = (response.files || []).map((f) => ({ ...f, selected: false, title: null })).sort((a, b) => naturalCompare(a.name, b.name));
    } catch (error) {
      console.error(error);
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, `Failed to fetch Jimaku data for ID ${id}.`), life: 3000 });
      fetching.value = false;
      if (id + 1 <= endId.value) await fetchId(id + 1); // Try next id
      return;
    }
    fetching.value = false;
  };

  const fetchNext = async () => {
    const next = currentId.value === null ? startId.value : currentId.value + 1;
    if (next > endId.value) {
      toast.add({ severity: 'info', summary: 'Info', detail: 'Finished processing all IDs.', life: 3000 });
      return;
    }
    await fetchId(next);
  };

  const goToId = async () => {
    if (jumpId.value === null) return;
    await fetchId(jumpId.value);
  };

  const submit = async () => {
    const selectedFiles = files.value.filter((f) => f.selected);
    if (!jimakuData.value || !selectedFiles.length) {
      toast.add({ severity: 'warn', summary: 'Warning', detail: 'No files selected.', life: 3000 });
      return;
    }

    const payload = {
      jimakuId: jimakuData.value.entry.id,
      files: selectedFiles.map((f) => ({
        url: f.url,
        name: f.name,
        // Position-based "Episode {n}" is the backend default, so only send manual or detected titles
        title: f.title ?? detectedTitles.value.get(f.url) ?? null,
      })),
    };

    submitting.value = true;
    try {
      const response = await $api('admin/add-jimaku-deck', { method: 'POST', body: payload });
      toast.add({ severity: 'success', summary: 'Success', detail: `Deck '${response.title}' has been queued for processing.`, life: 3000 });
      await fetchNext();
    } catch (error) {
      console.error(error);
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Failed to add media.'), life: 3000 });
    } finally {
      submitting.value = false;
    }
  };

  const setAll = (selected: boolean) => files.value.forEach((f) => (f.selected = selected));
  const invertSelection = () => files.value.forEach((f) => (f.selected = !f.selected));

  const selectByPrefix = (selected: boolean) => {
    if (!prefix.value) return;
    const p = prefix.value.toLowerCase();
    files.value.forEach((f) => {
      if (f.name.toLowerCase().startsWith(p)) f.selected = selected;
    });
  };

  const selectBySuffix = (selected: boolean) => {
    if (!suffix.value) return;
    const s = suffix.value.toLowerCase();
    files.value.forEach((f) => {
      if (f.name.toLowerCase().endsWith(s)) f.selected = selected;
    });
  };

  const selectByRegex = (selected: boolean) => {
    if (!regexPattern.value) return;
    let regex: RegExp;
    try {
      regex = new RegExp(regexPattern.value, 'i');
    } catch {
      toast.add({ severity: 'error', summary: 'Invalid regex', detail: `'${regexPattern.value}' is not a valid regular expression.`, life: 3000 });
      return;
    }
    files.value.forEach((f) => {
      if (regex.test(f.name)) f.selected = selected;
    });
  };

  const sortByName = () => {
    files.value = [...files.value].sort((a, b) => naturalCompare(a.name, b.name));
  };

  const moveFile = (index: number, delta: number) => {
    const target = index + delta;
    if (target < 0 || target >= files.value.length) return;
    const list = [...files.value];
    [list[index], list[target]] = [list[target]!, list[index]!];
    files.value = list;
  };

  const listRef = ref<HTMLElement | null>(null);
  const { dragIndex, dropIndex, handlePointerDown } = useTouchReorder({
    containerRef: listRef,
    onReorder(from, to) {
      const list = [...files.value];
      const [moved] = list.splice(from, 1);
      list.splice(to, 0, moved!);
      files.value = list;
    },
  });

  // Episode number each selected file will get, following the current list order
  const episodeNumbers = computed(() => {
    const numbers = new Map<string, number>();
    let n = 0;
    for (const f of files.value) {
      if (f.selected) numbers.set(f.url, ++n);
    }
    return numbers;
  });

  // Best-effort episode titles detected from the selected file names; manual titles always win
  const detectedTitles = computed(() => {
    const selected = files.value.filter((f) => f.selected);
    const detections = detectNumbering(selected.map((f) => f.name));
    const map = new Map<string, string>();
    selected.forEach((f, i) => {
      const d = detections[i];
      if (d) map.set(f.url, `Episode ${d.display}`);
    });
    return map;
  });

  const effectiveTitle = (file: FileRow) => file.title ?? detectedTitles.value.get(file.url) ?? `Episode ${episodeNumbers.value.get(file.url)}`;

  const sortByDetectedNumber = () => {
    const selected = files.value.filter((f) => f.selected);
    const detections = detectNumbering(selected.map((f) => f.name));
    const valueByUrl = new Map(selected.map((f, i) => [f.url, detections[i]?.value]));
    files.value = files.value
      .map((f, i) => ({ f, i }))
      .sort((a, b) => (valueByUrl.get(a.f.url) ?? Infinity) - (valueByUrl.get(b.f.url) ?? Infinity) || a.i - b.i)
      .map((x) => x.f);
  };

  const renamingUrl = ref<string | null>(null);
  const renameValue = ref('');

  const startRename = (file: FileRow) => {
    renamingUrl.value = file.url;
    renameValue.value = file.name;
  };

  const cancelRename = () => {
    renamingUrl.value = null;
    renameValue.value = '';
  };

  const confirmRename = () => {
    const file = files.value.find((f) => f.url === renamingUrl.value);
    const trimmed = renameValue.value.trim();
    if (file && trimmed) {
      // Keep the original extension if the new name drops it, the backend relies on it
      const dotIndex = file.name.lastIndexOf('.');
      const extension = dotIndex >= 0 ? file.name.slice(dotIndex) : '';
      file.name = !trimmed.includes('.') && extension ? `${trimmed}${extension}` : trimmed;
    }
    cancelRename();
  };

  const editingTitleUrl = ref<string | null>(null);
  const titleValue = ref('');

  const startTitleEdit = (file: FileRow) => {
    editingTitleUrl.value = file.url;
    titleValue.value = file.title ?? detectedTitles.value.get(file.url) ?? '';
  };

  const cancelTitleEdit = () => {
    editingTitleUrl.value = null;
    titleValue.value = '';
  };

  const confirmTitleEdit = () => {
    const file = files.value.find((f) => f.url === editingTitleUrl.value);
    if (file) {
      const trimmed = titleValue.value.trim();
      file.title = trimmed || null; // Empty reverts to the default "Episode {n}"
    }
    cancelTitleEdit();
  };

  const formatSize = (bytes: number) => {
    if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
    if (bytes >= 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${bytes} B`;
  };

  const isArchive = (name: string) => /\.(zip|rar|7z)$/i.test(name);
</script>

<template>
  <div class="container mx-auto p-4">
    <h1 class="text-3xl font-bold mb-6">Add Media from Jimaku</h1>

    <div class="grid grid-cols-1 md:grid-cols-2 gap-6 mb-6 max-w-xl">
      <div>
        <label for="startId" class="block mb-2">Start ID</label>
        <InputNumber v-model="startId" input-id="startId" :use-grouping="false" />
      </div>
      <div>
        <label for="endId" class="block mb-2">End ID</label>
        <InputNumber v-model="endId" input-id="endId" :use-grouping="false" />
      </div>
    </div>

    <div class="flex flex-wrap items-center gap-4 mb-6">
      <Button :label="currentId === null ? 'Start' : 'Next'" :loading="fetching" class="p-button-primary" @click="fetchNext" />
      <InputGroup class="!w-56">
        <InputNumber v-model="jumpId" placeholder="Jump to ID" :use-grouping="false" @keydown.enter="goToId" />
        <Button icon="pi pi-arrow-right" aria-label="Go to ID" :loading="fetching" @click="goToId" />
      </InputGroup>
    </div>

    <div v-if="jimakuData">
      <h2 class="text-2xl font-bold mb-1">
        <a :href="`https://jimaku.cc/entry/${jimakuData.entry.id}`" target="_blank" rel="noopener" class="hover:underline">
          {{ jimakuData.entry.name }}
          <i class="pi pi-external-link text-sm align-middle text-surface-400" />
        </a>
      </h2>
      <div class="text-sm text-surface-500 dark:text-surface-400 mb-4">
        Jimaku ID {{ currentId }}
        <span v-if="jimakuData.entry.japanese_name"> · {{ jimakuData.entry.japanese_name }}</span>
        <span v-if="jimakuData.entry.english_name"> · {{ jimakuData.entry.english_name }}</span>
      </div>

      <div class="grid grid-cols-1 md:grid-cols-3 gap-4 mb-2">
        <InputGroup>
          <InputText v-model="prefix" placeholder="Prefix" @keydown.enter="selectByPrefix(true)" />
          <Button icon="pi pi-check" aria-label="Select by prefix" @click="selectByPrefix(true)" />
          <Button icon="pi pi-times" aria-label="Deselect by prefix" @click="selectByPrefix(false)" />
        </InputGroup>
        <InputGroup>
          <InputText v-model="suffix" placeholder="Suffix" @keydown.enter="selectBySuffix(true)" />
          <Button icon="pi pi-check" aria-label="Select by suffix" @click="selectBySuffix(true)" />
          <Button icon="pi pi-times" aria-label="Deselect by suffix" @click="selectBySuffix(false)" />
        </InputGroup>
        <InputGroup>
          <InputText v-model="regexPattern" placeholder="Regex" @keydown.enter="selectByRegex(true)" />
          <Button icon="pi pi-check" aria-label="Select by regex" @click="selectByRegex(true)" />
          <Button icon="pi pi-times" aria-label="Deselect by regex" @click="selectByRegex(false)" />
        </InputGroup>
      </div>

      <div class="flex flex-wrap items-center gap-2 mb-4">
        <Button label="Select All" severity="secondary" size="small" @click="setAll(true)" />
        <Button label="Deselect All" severity="secondary" size="small" @click="setAll(false)" />
        <Button label="Invert" severity="secondary" size="small" @click="invertSelection" />
        <Button label="Sort by name" icon="pi pi-sort-alpha-down" severity="secondary" size="small" @click="sortByName" />
        <Button
          v-tooltip.top="'Order selected files by the episode/volume number detected in their names'"
          label="Sort by number"
          icon="pi pi-sort-numeric-down"
          severity="secondary"
          size="small"
          :disabled="selectedCount < 2"
          @click="sortByDetectedNumber"
        />
        <span class="ml-auto text-sm text-surface-600 dark:text-surface-300 font-medium"> {{ selectedCount }} / {{ files.length }} selected </span>
      </div>

      <div
        class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 overflow-y-auto"
        style="height: min(60vh, 42rem); min-height: 16rem; resize: vertical"
      >
        <div v-if="!files.length" class="p-4 text-surface-500 dark:text-surface-400">No files in this entry.</div>
        <div ref="listRef">
          <div
            v-for="(file, index) in files"
            :key="file.url"
            class="flex items-center gap-2 px-2 py-1.5 border-b border-surface-100 dark:border-surface-800 last:border-b-0 transition-colors cursor-pointer"
            :class="{
              'bg-primary-50 dark:bg-primary-900/20': file.selected && dropIndex !== index,
              'bg-primary-100/60 dark:bg-primary-900/40': dropIndex === index && dragIndex !== index,
              'opacity-50': dragIndex === index,
            }"
            @click="file.selected = !file.selected"
          >
            <div class="flex items-center shrink-0" @click.stop>
              <Button
                icon="pi pi-chevron-up"
                text
                rounded
                size="small"
                class="!w-6 !h-6"
                :disabled="index === 0"
                aria-label="Move up"
                @click="moveFile(index, -1)"
              />
              <Button
                icon="pi pi-chevron-down"
                text
                rounded
                size="small"
                class="!w-6 !h-6"
                :disabled="index === files.length - 1"
                aria-label="Move down"
                @click="moveFile(index, 1)"
              />
              <span class="cursor-grab active:cursor-grabbing px-1" style="touch-action: none" @pointerdown="handlePointerDown($event, index)">
                <i class="pi pi-bars text-surface-400 dark:text-surface-400 text-xs" />
              </span>
              <span class="w-7 text-right text-xs text-surface-400 dark:text-surface-400 tabular-nums mr-1">{{ index + 1 }}</span>
              <Checkbox v-model="file.selected" binary />
            </div>

            <template v-if="renamingUrl === file.url">
              <InputText
                v-model="renameValue"
                class="flex-1 !py-1 text-sm"
                size="small"
                autofocus
                @click.stop
                @keydown.enter="confirmRename"
                @keydown.escape="cancelRename"
              />
              <div class="flex items-center shrink-0" @click.stop>
                <Button icon="pi pi-check" text rounded size="small" class="!w-6 !h-6" aria-label="Confirm rename" @click="confirmRename" />
                <Button icon="pi pi-times" text rounded size="small" class="!w-6 !h-6" aria-label="Cancel rename" @click="cancelRename" />
              </div>
            </template>
            <template v-else>
              <span class="flex-1 min-w-0 truncate text-sm" :title="file.name">
                <i v-if="isArchive(file.name)" class="pi pi-box text-amber-500 text-xs mr-1" />
                {{ file.name }}
              </span>

              <template v-if="file.selected && selectedCount >= 2">
                <template v-if="editingTitleUrl === file.url">
                  <InputText
                    v-model="titleValue"
                    class="!py-0.5 w-44 text-xs shrink-0"
                    size="small"
                    placeholder="Subdeck title"
                    autofocus
                    @click.stop
                    @keydown.enter="confirmTitleEdit"
                    @keydown.escape="cancelTitleEdit"
                  />
                  <div class="flex items-center shrink-0" @click.stop>
                    <Button icon="pi pi-check" text rounded size="small" class="!w-6 !h-6" aria-label="Confirm title" @click="confirmTitleEdit" />
                    <Button icon="pi pi-times" text rounded size="small" class="!w-6 !h-6" aria-label="Cancel title" @click="cancelTitleEdit" />
                  </div>
                </template>
                <button
                  v-else
                  type="button"
                  class="shrink-0 text-xs px-1.5 py-0.5 rounded cursor-pointer transition-colors max-w-48 truncate"
                  :class="
                    file.title || detectedTitles.has(file.url)
                      ? 'bg-primary-100 text-primary-700 dark:bg-primary-900/40 dark:text-primary-300 hover:bg-primary-200 dark:hover:bg-primary-900/60'
                      : 'bg-surface-100 text-surface-500 dark:bg-surface-800 dark:text-surface-400 hover:bg-surface-200 dark:hover:bg-surface-700'
                  "
                  :title="
                    file.title
                      ? 'Custom subdeck title — click to edit'
                      : detectedTitles.has(file.url)
                        ? 'Detected from the file name — click to edit'
                        : 'Position-based title — click to edit'
                  "
                  @click.stop="startTitleEdit(file)"
                >
                  <i :class="!file.title && detectedTitles.has(file.url) ? 'pi pi-sparkles' : 'pi pi-tag'" class="text-[10px] mr-1" />{{ effectiveTitle(file) }}
                </button>
              </template>

              <span class="shrink-0 text-xs text-surface-400 dark:text-surface-400 tabular-nums hidden sm:inline">{{ formatSize(file.size) }}</span>
              <Button
                icon="pi pi-pencil"
                text
                rounded
                size="small"
                class="!w-6 !h-6 shrink-0 opacity-40 hover:opacity-100"
                aria-label="Rename file"
                @click.stop="startRename(file)"
              />
            </template>
          </div>
        </div>
      </div>

      <div class="flex gap-2 mt-6">
        <Button label="Submit" class="p-button-success" :loading="submitting" :disabled="!selectedCount" @click="submit" />
        <Button label="Skip" class="p-button-warning" :disabled="submitting || fetching" @click="fetchNext" />
      </div>
    </div>
  </div>
</template>

<style scoped></style>
