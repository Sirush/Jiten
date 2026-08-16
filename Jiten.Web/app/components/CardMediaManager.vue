<script setup lang="ts">
  import type {
    CardMediaDeleteTarget,
    CardMediaKind,
    CardMediaKindFilter,
    CardMediaManageItem,
    CardMediaManageResponse,
    CardMediaManageSummary,
    CardMediaSort,
  } from '~/types';
  import { formatBytes } from '~/utils/formatBytes';
  import { formatDateShort } from '~/utils/formatDateShort';
  import { debounce } from 'perfect-debounce';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  const { $api } = useNuxtApp();
  const toast = useToast();
  const confirm = useConfirm();
  const cardMedia = useCardMedia();
  const { refresh: refreshPlus } = useJitenPlus();

  const items = ref<CardMediaManageItem[]>([]);
  const summary = ref<CardMediaManageSummary | null>(null);
  const totalForms = ref(0);
  const pageSize = ref(50);
  const page = ref(1);
  const loading = ref(false);
  const search = ref('');
  const kindFilter = ref<CardMediaKindFilter>('all');
  const sort = ref<CardMediaSort>('size');
  const selected = ref<Set<string>>(new Set());
  const deletingKey = ref<string | null>(null);
  const deletingAll = ref(false);
  const deletingSelected = ref(false);

  const kindOptions = [
    { label: 'All', value: 'all' },
    { label: 'Images', value: 'image' },
    { label: 'Audio', value: 'audio' },
  ];
  const sortOptions = [
    { label: 'Largest first', value: 'size' },
    { label: 'Newest', value: 'date_desc' },
    { label: 'Oldest', value: 'date_asc' },
  ];

  const totalFiles = computed(() => (summary.value ? summary.value.imageCount + summary.value.audioCount : 0));

  const quotaPercent = computed(() => {
    const s = summary.value;
    if (!s || s.maxBytes <= 0) return 0;
    return Math.min(100, Math.round((s.usedBytes / s.maxBytes) * 100));
  });
  // A lapsed account has no allowance at all, so there is no denominator to show it against.
  const quotaLabel = computed(() => {
    const s = summary.value;
    if (!s) return '';
    if (s.maxBytes <= 0) return `${formatBytes(s.usedBytes)} stored`;
    return `${formatBytes(s.usedBytes)} used of ${formatBytes(s.maxBytes)}`;
  });

  function rowKey(item: CardMediaManageItem) {
    return `${item.wordId}-${item.readingIndex}`;
  }

  function fileKey(item: CardMediaManageItem, kind: CardMediaKind) {
    return `${item.wordId}-${item.readingIndex}-${kind}`;
  }

  function filesOf(item: CardMediaManageItem): CardMediaKind[] {
    const kinds: CardMediaKind[] = [];
    if (item.image) kinds.push('image');
    if (item.audio) kinds.push('audio');
    return kinds;
  }

  const selectedFileCount = computed(() => {
    let n = 0;
    for (const item of items.value) if (selected.value.has(rowKey(item))) n += filesOf(item).length;
    return n;
  });

  const allPageSelected = computed(() => items.value.length > 0 && items.value.every((i) => selected.value.has(rowKey(i))));

  function toggleRow(item: CardMediaManageItem, checked: boolean) {
    const next = new Set(selected.value);
    if (checked) next.add(rowKey(item));
    else next.delete(rowKey(item));
    selected.value = next;
  }

  function togglePage(checked: boolean) {
    const next = new Set(selected.value);
    for (const item of items.value) {
      if (checked) next.add(rowKey(item));
      else next.delete(rowKey(item));
    }
    selected.value = next;
  }

  async function load() {
    loading.value = true;
    try {
      const params = new URLSearchParams({ page: String(page.value), sort: sort.value, kind: kindFilter.value });
      const term = search.value.trim();
      if (term) params.set('search', term);

      const res = await $api<CardMediaManageResponse>(`srs/card-media/manage?${params.toString()}`);
      items.value = res.items;
      summary.value = res.summary;
      totalForms.value = res.totalForms;
      pageSize.value = res.pageSize;

      // A delete can empty the current page; fall back to the last populated page rather than showing blank.
      const maxPage = Math.max(1, Math.ceil(totalForms.value / pageSize.value));
      if (page.value > maxPage) {
        page.value = maxPage;
        await load();
      }
    } catch {
      toast.add({ severity: 'error', summary: 'Could not load card media', detail: 'Please try again.', life: 4000 });
    } finally {
      loading.value = false;
    }
  }

  const runSearch = debounce(() => {
    page.value = 1;
    selected.value = new Set();
    load();
  }, 300);
  watch(search, runSearch);

  watch([kindFilter, sort], () => {
    page.value = 1;
    selected.value = new Set();
    load();
  });

  function onPage(e: { page: number }) {
    page.value = e.page + 1;
    selected.value = new Set();
    load();
  }

  // Audio plays through a throwaway element (no persistent <audio> per row).
  const playingUrl = ref<string | null>(null);
  let audioEl: HTMLAudioElement | null = null;

  function stopAudio() {
    if (audioEl) {
      audioEl.onended = null;
      audioEl.onerror = null;
      audioEl.pause();
      audioEl = null;
    }
    playingUrl.value = null;
  }

  function toggleAudio(url: string) {
    if (playingUrl.value === url) {
      stopAudio();
      return;
    }
    stopAudio();
    const a = new Audio(url);
    audioEl = a;
    const done = () => {
      if (audioEl === a) stopAudio();
    };
    a.onended = done;
    a.onerror = done;
    playingUrl.value = url;
    a.play().catch(done);
  }

  function confirmDeleteFile(item: CardMediaManageItem, kind: CardMediaKind) {
    const noun = kind === 'image' ? 'image' : 'audio clip';
    confirm.require({
      message: `Remove the ${noun} for this word? This cannot be undone.`,
      header: kind === 'image' ? 'Remove image?' : 'Remove audio?',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Remove', severity: 'danger' },
      accept: () => doDeleteFile(item, kind),
    });
  }

  async function doDeleteFile(item: CardMediaManageItem, kind: CardMediaKind) {
    if (deletingKey.value) return;
    if (kind === 'audio' && item.audio && playingUrl.value === item.audio.url) stopAudio();
    deletingKey.value = fileKey(item, kind);
    try {
      await $api(`srs/card-media/${item.wordId}/${item.readingIndex}/${kind}`, { method: 'DELETE' });
      cardMedia.invalidateWord(item.wordId);
      await refreshPlus();
      await load();
      toast.add({ severity: 'success', summary: 'Removed', life: 2000 });
    } catch {
      toast.add({ severity: 'error', summary: 'Could not remove media', detail: 'Please try again.', life: 4000 });
    } finally {
      deletingKey.value = null;
    }
  }

  function confirmDeleteSelected() {
    const n = selectedFileCount.value;
    if (n === 0) return;
    confirm.require({
      message: `This will permanently delete ${n} selected file${n === 1 ? '' : 's'}. This cannot be undone.`,
      header: 'Delete selected?',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: `Delete ${n}`, severity: 'danger' },
      accept: doDeleteSelected,
    });
  }

  async function doDeleteSelected() {
    if (deletingSelected.value) return;
    const targets: CardMediaDeleteTarget[] = [];
    const wordIds = new Set<number>();
    for (const item of items.value) {
      if (!selected.value.has(rowKey(item))) continue;
      for (const kind of filesOf(item)) {
        targets.push({ wordId: item.wordId, readingIndex: item.readingIndex, kind });
        wordIds.add(item.wordId);
      }
    }
    if (targets.length === 0) return;

    deletingSelected.value = true;
    stopAudio();
    try {
      await $api('srs/card-media/delete-batch', { method: 'POST', body: { items: targets } });
      for (const wordId of wordIds) cardMedia.invalidateWord(wordId);
      selected.value = new Set();
      await refreshPlus();
      await load();
      toast.add({ severity: 'success', summary: 'Selected media deleted', life: 2500 });
    } catch {
      toast.add({ severity: 'error', summary: 'Could not delete media', detail: 'Please try again.', life: 4000 });
    } finally {
      deletingSelected.value = false;
    }
  }

  function confirmDeleteAll() {
    confirm.require({
      message: `This will permanently delete all ${totalFiles.value} file${totalFiles.value === 1 ? '' : 's'} across every word. This cannot be undone.`,
      header: 'Delete all card media?',
      icon: 'pi pi-exclamation-triangle',
      rejectProps: { label: 'Cancel', severity: 'secondary', outlined: true },
      acceptProps: { label: 'Delete all', severity: 'danger' },
      accept: doDeleteAll,
    });
  }

  async function doDeleteAll() {
    if (deletingAll.value) return;
    deletingAll.value = true;
    stopAudio();
    try {
      await $api('srs/card-media', { method: 'DELETE' });
      cardMedia.clearCache();
      selected.value = new Set();
      page.value = 1;
      await refreshPlus();
      await load();
      toast.add({ severity: 'success', summary: 'All card media deleted', life: 2500 });
    } catch {
      toast.add({ severity: 'error', summary: 'Could not delete media', detail: 'Please try again.', life: 4000 });
    } finally {
      deletingAll.value = false;
    }
  }

  onMounted(load);
  onUnmounted(stopAudio);
</script>

<template>
  <div class="flex flex-col gap-4">
    <!-- Header: quota + summary + delete all -->
    <div class="flex flex-col sm:flex-row sm:items-start sm:justify-between gap-3">
      <div class="flex-1 min-w-0">
        <ProgressBar :value="quotaPercent" :show-value="false" class="!h-3" />
        <p class="text-sm text-gray-600 dark:text-gray-400 mt-2">{{ quotaLabel }}</p>
        <div v-if="summary" class="mt-2 flex flex-wrap items-center gap-2 text-sm">
          <span class="inline-flex items-center gap-1.5 rounded-full bg-surface-100 dark:bg-surface-800 px-3 py-1 text-surface-700 dark:text-surface-200">
            <i class="pi pi-image text-surface-500 dark:text-surface-400" />
            {{ summary.imageCount }} image{{ summary.imageCount === 1 ? '' : 's' }} · {{ formatBytes(summary.imageBytes) }}
          </span>
          <span class="inline-flex items-center gap-1.5 rounded-full bg-surface-100 dark:bg-surface-800 px-3 py-1 text-surface-700 dark:text-surface-200">
            <i class="pi pi-volume-up text-surface-500 dark:text-surface-400" />
            {{ summary.audioCount }} audio clip{{ summary.audioCount === 1 ? '' : 's' }} · {{ formatBytes(summary.audioBytes) }}
          </span>
        </div>
      </div>
      <Button
        label="Delete all"
        icon="pi pi-trash"
        severity="danger"
        outlined
        :loading="deletingAll"
        :disabled="totalFiles === 0"
        class="w-full sm:w-auto shrink-0"
        @click="confirmDeleteAll"
      />
    </div>

    <!-- Toolbar -->
    <div class="flex flex-col sm:flex-row sm:items-center gap-2">
      <IconField class="flex-1 min-w-0">
        <InputIcon class="pi pi-search" />
        <InputText v-model="search" placeholder="Search a word" class="w-full" />
      </IconField>
      <SelectButton
        v-model="kindFilter"
        :options="kindOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :pt="{ button: { class: 'text-sm px-3 py-2' } }"
      />
      <Select v-model="sort" :options="sortOptions" option-label="label" option-value="value" class="w-full sm:w-48" />
    </div>

    <!-- Selection bar -->
    <div class="flex flex-wrap items-center justify-between gap-2 min-h-9">
      <label class="flex items-center gap-2 text-sm text-surface-600 dark:text-surface-300 cursor-pointer">
        <Checkbox :model-value="allPageSelected" :binary="true" :disabled="items.length === 0" @update:model-value="togglePage" />
        Select all on this page
      </label>
      <Button
        v-if="selectedFileCount > 0"
        :label="`Delete selected (${selectedFileCount})`"
        icon="pi pi-trash"
        severity="danger"
        size="small"
        :loading="deletingSelected"
        @click="confirmDeleteSelected"
      />
    </div>

    <div v-if="loading && items.length === 0" class="flex items-center gap-2 text-sm text-surface-500 dark:text-surface-400 py-4">
      <i class="pi pi-spin pi-spinner" />
      Loading card media…
    </div>

    <p v-else-if="items.length === 0" class="text-sm text-surface-500 dark:text-surface-400 py-4">
      {{ search.trim() ? 'No card media matches that word.' : 'No card media stored.' }}
    </p>

    <div v-else class="flex flex-col gap-2" :class="{ 'opacity-60 pointer-events-none': loading }">
      <div
        v-for="item in items"
        :key="rowKey(item)"
        class="flex items-center gap-3 rounded-lg border border-surface-200 dark:border-surface-700 p-2.5"
        :class="selected.has(rowKey(item)) ? 'bg-primary-50/60 dark:bg-primary-950/30 border-primary-300 dark:border-primary-800' : ''"
      >
        <Checkbox
          :model-value="selected.has(rowKey(item))"
          :binary="true"
          class="shrink-0"
          @update:model-value="(v: boolean) => toggleRow(item, v)"
        />

        <!-- Thumbnail -->
        <div class="shrink-0">
          <Image v-if="item.image" preview>
            <template #image>
              <img
                :src="item.image.url"
                loading="lazy"
                alt="Card image"
                class="h-12 w-12 rounded-md object-cover border border-surface-200 dark:border-surface-700"
              />
            </template>
            <template #preview="slotProps">
              <img :src="item.image.url" alt="Card image" :style="slotProps.style" />
            </template>
          </Image>
          <div
            v-else
            class="h-12 w-12 rounded-md border border-surface-200 dark:border-surface-700 bg-surface-50 dark:bg-surface-800 flex items-center justify-center text-surface-400 dark:text-surface-400"
          >
            <i class="pi pi-volume-up" />
          </div>
        </div>

        <!-- Word + meta -->
        <div class="flex-1 min-w-0">
          <NuxtLink
            :to="`/vocabulary/${item.wordId}/${item.readingIndex}`"
            class="font-medium text-primary-600 dark:text-primary-400 hover:underline break-words"
            lang="ja"
          >
            {{ item.wordText || `Word #${item.wordId}` }}
          </NuxtLink>
          <div class="mt-0.5 flex flex-wrap gap-x-3 gap-y-0.5 text-xs text-surface-500 dark:text-surface-400">
            <span v-if="item.image" class="inline-flex items-center gap-1">
              <i class="pi pi-image" /> {{ formatBytes(item.image.fileSizeBytes) }} · {{ formatDateShort(item.image.createdAt) }}
            </span>
            <span v-if="item.audio" class="inline-flex items-center gap-1">
              <i class="pi pi-volume-up" /> {{ formatBytes(item.audio.fileSizeBytes) }} · {{ formatDateShort(item.audio.createdAt) }}
            </span>
          </div>
        </div>

        <!-- Actions -->
        <div class="flex items-center gap-0.5 shrink-0">
          <Button
            v-if="item.audio"
            v-tooltip.top="playingUrl === item.audio.url ? 'Stop' : 'Play audio'"
            :icon="playingUrl === item.audio.url ? 'pi pi-stop' : 'pi pi-play'"
            text
            rounded
            size="small"
            :aria-label="playingUrl === item.audio.url ? 'Stop audio' : 'Play audio'"
            @click="toggleAudio(item.audio.url)"
          />
          <Button
            v-if="item.image"
            v-tooltip.top="'Delete image'"
            icon="pi pi-trash"
            severity="danger"
            text
            rounded
            size="small"
            aria-label="Delete image"
            :loading="deletingKey === fileKey(item, 'image')"
            @click="confirmDeleteFile(item, 'image')"
          />
          <Button
            v-if="item.audio"
            v-tooltip.top="'Delete audio'"
            icon="pi pi-trash"
            severity="danger"
            text
            rounded
            size="small"
            aria-label="Delete audio"
            :loading="deletingKey === fileKey(item, 'audio')"
            @click="confirmDeleteFile(item, 'audio')"
          />
        </div>
      </div>
    </div>

    <Paginator v-if="totalForms > pageSize" :rows="pageSize" :total-records="totalForms" :first="(page - 1) * pageSize" @page="onPage" />
  </div>
</template>
