<script setup lang="ts">
  import { ref, computed, watch, onMounted, onBeforeUnmount } from 'vue';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import InputGroup from 'primevue/inputgroup';
  import Message from 'primevue/message';
  import SelectButton from 'primevue/selectbutton';
  import Tag from 'primevue/tag';
  import { useToast } from 'primevue/usetoast';
  import { formatDateTime, formatDayMonthTime } from '~/utils/dateFormat';

  useHead({
    title: 'Anime & Drama Tracking',
  });

  definePageMeta({
    middleware: ['auth-admin'],
  });

  interface Tracker {
    deckId: number;
    originalTitle: string;
    romajiTitle: string | null;
    englishTitle: string | null;
    coverName: string | null;
    mediaType: string;
    jimakuEntryId: number;
    filePattern: string;
    anilistId: number | null;
    tmdbId: string | null;
    tmdbSeason: number | null;
    expectedEpisodes: number | null;
    expectedEpisodesOverride: number | null;
    airedEpisodes: number | null;
    finishedAiring: boolean;
    imported: number;
    held: number;
    queued: number;
    syncEnabled: boolean;
    completedAt: string | null;
    lastCheckedAt: string | null;
    lastSyncedAt: string | null;
    nextCheckAt: string;
    consecutiveFailures: number;
    lastError: string | null;
  }

  interface TrackedFile {
    fileName: string;
    size: number;
    episodeNumber: number | null;
    status: 'Queued' | 'Imported' | 'Held' | 'Ignored';
    holdReason: string | null;
    childDeckId: number | null;
    firstSeenAt: string;
  }

  interface SearchResult {
    id: number;
    name: string;
    japaneseName: string | null;
    englishName: string | null;
    lastModified?: string;
  }

  interface PreviewFile {
    name: string;
    size: number;
    matched: boolean;
    inDeck: boolean;
    episode: number | null;
    position: number | null;
    status: 'Queued' | 'Held' | null;
    holdReason: string | null;
  }

  interface Preview {
    entry: {
      id: number;
      name: string;
      japaneseName: string | null;
      englishName: string | null;
      mediaType: string;
      isMovie: boolean;
      anilistId: number | null;
      tmdbId: string | null;
    };
    airing: { total: number | null; aired: number | null; finished: boolean } | null;
    trackedDeckId: number | null;
    existingDeck: { deckId: number; originalTitle: string } | null;
    targetDeck: {
      deckId: number;
      originalTitle: string;
      mediaType: string;
      isSubdeck: boolean;
      episodeCount: number;
      holdsTextDirectly: boolean;
      namedEpisodeCount: number;
    } | null;
    patternValid: boolean;
    files: PreviewFile[];
  }

  type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary';

  const toast = useToast();
  const { $api } = useNuxtApp();
  const localiseTitle = useLocaliseTitle();

  const trackers = ref<Tracker[]>([]);
  const loadingTrackers = ref(true);
  const loadError = ref<string | null>(null);

  const expected = (t: Tracker) => t.expectedEpisodesOverride ?? t.expectedEpisodes;

  const flags = (t: Tracker): { label: string; severity: Severity; hint: string }[] => {
    const out: { label: string; severity: Severity; hint: string }[] = [];
    const total = expected(t);
    if (t.lastError) out.push({ label: 'Error', severity: 'danger', hint: t.lastError });
    if (t.held > 0) out.push({ label: `${t.held} held`, severity: 'warn', hint: 'Files waiting for a decision' });
    if (t.queued > 0) out.push({ label: `${t.queued} approved`, severity: 'info', hint: 'Ingested at the next check' });
    if (total != null && t.imported > total) out.push({ label: `Over by ${t.imported - total}`, severity: 'warn', hint: 'More episodes ingested than expected' });
    else if (total != null && t.finishedAiring && t.imported < total)
      out.push({ label: `Short by ${total - t.imported}`, severity: 'warn', hint: 'The show finished airing with episodes still missing' });
    else if (!t.finishedAiring && t.airedEpisodes != null && t.imported < t.airedEpisodes)
      out.push({ label: `Behind by ${t.airedEpisodes - t.imported}`, severity: 'secondary', hint: 'Aired episodes with no matching subtitle yet' });
    if (t.completedAt) out.push({ label: 'Complete', severity: 'success', hint: `Stopped ${formatDateTime(t.completedAt)}` });
    else if (!t.syncEnabled) out.push({ label: 'Paused', severity: 'secondary', hint: 'Not checked until resumed' });
    return out;
  };

  const needsAttention = (t: Tracker) => !!t.lastError || t.held > 0 || flags(t).some((f) => f.label.startsWith('Over') || f.label.startsWith('Short'));

  const pendingRemoval = ref(new Map<number, ReturnType<typeof setTimeout>>());
  const visibleTrackers = computed(() =>
    [...trackers.value].sort((a, b) => Number(needsAttention(b)) - Number(needsAttention(a)) || Number(b.syncEnabled) - Number(a.syncEnabled)),
  );

  const loadTrackers = async () => {
    try {
      trackers.value = await $api<Tracker[]>('admin/jimaku-trackers');
      loadError.value = null;
    } catch (error) {
      loadError.value = extractApiError(error, 'The tracked shows could not be loaded.');
    } finally {
      loadingTrackers.value = false;
    }
  };

  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  const refreshSoon = (deckId?: number) => {
    if (refreshTimer) clearTimeout(refreshTimer);
    refreshTimer = setTimeout(async () => {
      await loadTrackers();
      if (deckId != null && expandedId.value === deckId) await loadFiles(deckId);
    }, 6000);
  };

  const expandedId = ref<number | null>(null);
  const files = ref<TrackedFile[]>([]);
  const loadingFiles = ref(false);
  const editPattern = ref('');
  const editExpected = ref<number | null>(null);
  const editSeason = ref<number | null>(null);
  const savingSettings = ref(false);

  const loadFiles = async (deckId: number) => {
    loadingFiles.value = true;
    try {
      files.value = await $api<TrackedFile[]>(`admin/jimaku-trackers/${deckId}/files`);
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'The files could not be loaded.'), life: 5000 });
    } finally {
      loadingFiles.value = false;
    }
  };

  const toggleExpanded = async (t: Tracker) => {
    if (expandedId.value === t.deckId) {
      expandedId.value = null;
      return;
    }
    expandedId.value = t.deckId;
    files.value = [];
    editPattern.value = t.filePattern;
    editExpected.value = t.expectedEpisodesOverride;
    editSeason.value = t.tmdbSeason;
    await loadFiles(t.deckId);
  };

  const fileGroups = computed(() =>
    (['Held', 'Queued', 'Imported', 'Ignored'] as const)
      .map((status) => ({ status, items: files.value.filter((f) => f.status === status) }))
      .filter((g) => g.items.length > 0),
  );

  const groupTitles = { Held: 'On Hold', Queued: 'Approved', Imported: 'In the Deck', Ignored: 'Ignored' };
  const reasonLabels: Record<string, string> = {
    duplicate: 'Duplicate',
    gap: 'Gap',
    over: 'Over count',
    unnumbered: 'No number',
    archive: 'Archive',
    error: 'Error',
  };

  const splitReason = (reason: string | null) => {
    if (!reason) return null;
    const colon = reason.indexOf(':');
    if (colon < 0) return { label: 'Held', detail: reason };
    const detail = reason.slice(colon + 1).trim();
    return { label: reasonLabels[reason.slice(0, colon)] ?? 'Held', detail: detail.charAt(0).toUpperCase() + detail.slice(1) };
  };

  const formatEpisode = (episode: number | null) => (episode == null ? '' : String(episode));

  const setFileStatus = async (deckId: number, file: TrackedFile, status: TrackedFile['status']) => {
    try {
      await $api(`admin/jimaku-trackers/${deckId}/files/status`, { method: 'POST', body: { fileName: file.fileName, status } });
      file.status = status;
      await loadTrackers();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'The file could not be updated.'), life: 5000 });
    }
  };

  const checkNow = async (t: Tracker) => {
    try {
      await $api(`admin/jimaku-trackers/${t.deckId}/sync`, { method: 'POST' });
      toast.add({ severity: 'success', summary: 'Check queued', detail: 'The list refreshes in a few seconds.', life: 4000 });
      refreshSoon(t.deckId);
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'The check could not be queued.'), life: 5000 });
    }
  };

  const setSyncEnabled = async (t: Tracker, enabled: boolean) => {
    try {
      await $api(`admin/jimaku-trackers/${t.deckId}/sync-enabled`, { method: 'POST', body: { enabled } });
      await loadTrackers();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Tracking could not be changed.'), life: 5000 });
    }
  };

  const saveSettings = async (t: Tracker) => {
    savingSettings.value = true;
    try {
      await $api(`admin/jimaku-trackers/${t.deckId}/settings`, {
        method: 'POST',
        body: { pattern: editPattern.value.trim(), expectedEpisodes: editExpected.value, tmdbSeason: editSeason.value },
      });
      toast.add({ severity: 'success', summary: 'Saved', life: 2500 });
      await Promise.all([loadTrackers(), loadFiles(t.deckId)]);
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'The settings could not be saved.'), life: 5000 });
    } finally {
      savingSettings.value = false;
    }
  };

  const removalDelayMs = 7000;
  const removeTracker = (t: Tracker) => {
    if (expandedId.value === t.deckId) expandedId.value = null;
    const timer = setTimeout(async () => {
      pendingRemoval.value.delete(t.deckId);
      try {
        await $api(`admin/jimaku-trackers/${t.deckId}`, { method: 'DELETE' });
      } catch (error) {
        toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Tracking could not be removed.'), life: 5000 });
      }
      await loadTrackers();
    }, removalDelayMs);
    pendingRemoval.value.set(t.deckId, timer);
  };

  const undoRemoval = (deckId: number) => {
    const timer = pendingRemoval.value.get(deckId);
    if (timer) clearTimeout(timer);
    pendingRemoval.value.delete(deckId);
  };

  const kindOptions = [
    { label: 'Anime', value: 'anime' },
    { label: 'Drama', value: 'drama' },
  ];
  const searchKind = ref<'anime' | 'drama'>('anime');
  const searchQuery = ref('');
  const searching = ref(false);
  const searchResults = ref<SearchResult[] | null>(null);

  const latest = ref<SearchResult[]>([]);
  const latestLoading = ref(false);
  const latestError = ref<string | null>(null);
  let latestRequest = 0;

  const loadLatest = async () => {
    const request = ++latestRequest;
    latestLoading.value = true;
    try {
      const data = await $api<SearchResult[]>('admin/jimaku-trackers/recent', { query: { anime: searchKind.value === 'anime' } });
      if (request !== latestRequest) return;
      latest.value = data;
      latestError.value = null;
    } catch (error) {
      if (request !== latestRequest) return;
      latest.value = [];
      latestError.value = extractApiError(error, 'The latest entries could not be loaded.');
    } finally {
      if (request === latestRequest) latestLoading.value = false;
    }
  };

  watch(searchKind, () => {
    searchResults.value = null;
    loadLatest();
  });

  const jimakuId = ref<number | null>(null);
  const pattern = ref('');
  const targetDeckId = ref<number | null>(null);
  const newExpected = ref<number | null>(null);
  const newSeason = ref<number | null>(null);
  const preview = ref<Preview | null>(null);
  const previewLoading = ref(false);
  const previewError = ref<string | null>(null);
  const submitting = ref(false);

  const search = async () => {
    const query = searchQuery.value.trim();
    if (!query) return;
    const asId = /^\d+$/.test(query) ? Number(query) : Number(query.match(/jimaku\.cc\/entry\/(\d+)/)?.[1] ?? NaN);
    if (Number.isFinite(asId)) {
      await selectEntry(asId);
      return;
    }
    searching.value = true;
    try {
      searchResults.value = await $api<SearchResult[]>('admin/jimaku-trackers/search', { query: { query, anime: searchKind.value === 'anime' } });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'The search failed.'), life: 5000 });
    } finally {
      searching.value = false;
    }
  };

  let previewRequest = 0;
  const loadPreview = async () => {
    if (jimakuId.value == null) return;
    const request = ++previewRequest;
    previewLoading.value = true;
    try {
      const data = await $api<Preview>('admin/jimaku-trackers/preview', {
        query: {
          jimakuId: jimakuId.value,
          pattern: pattern.value.trim() || undefined,
          deckId: targetDeckId.value ?? undefined,
          tmdbSeason: newSeason.value ?? undefined,
          expectedEpisodes: newExpected.value ?? undefined,
        },
      });
      if (request !== previewRequest) return;
      preview.value = data;
      previewError.value = null;
    } catch (error) {
      if (request !== previewRequest) return;
      previewError.value = extractApiError(error, 'This entry could not be read.');
    } finally {
      if (request === previewRequest) previewLoading.value = false;
    }
  };

  const selectEntry = async (id: number) => {
    jimakuId.value = id;
    searchResults.value = null;
    preview.value = null;
    pattern.value = '';
    targetDeckId.value = null;
    newExpected.value = null;
    newSeason.value = null;
    await loadPreview();
  };

  let previewTimer: ReturnType<typeof setTimeout> | null = null;
  watch([pattern, targetDeckId, newExpected, newSeason], () => {
    if (jimakuId.value == null) return;
    if (previewTimer) clearTimeout(previewTimer);
    previewTimer = setTimeout(loadPreview, 350);
  });

  const naturalCompare = (a: string, b: string) => a.localeCompare(b, undefined, { numeric: true, sensitivity: 'base' });
  const previewFiles = computed(() => [...(preview.value?.files ?? [])].sort((a, b) => naturalCompare(a.name, b.name)));
  const matchedFiles = computed(() => previewFiles.value.filter((f) => f.matched));
  const toIngest = computed(() => matchedFiles.value.filter((f) => f.status === 'Queued').length);
  const toHold = computed(() => matchedFiles.value.filter((f) => f.status === 'Held').length);
  const alreadyInDeck = computed(() => matchedFiles.value.filter((f) => f.inDeck).length);

  const episodeMarkers = [
    /(?<![a-z0-9])s\d{1,2}[ ._-]?e(\d{1,4})/i,
    /\s-\s(\d{1,4})(?![0-9a-z])/i,
    /(?<![a-z])(?:episode|ep|e)[ ._#]*(\d{1,4})(?![0-9a-z])/i,
    /[#＃]\s*(\d{1,4})/,
    /第\s*(\d{1,4})/,
  ];

  const extensionOf = (name: string) => name.match(/(\.[a-z]{2,3})?\.[a-z0-9]{2,4}$/i)?.[0] ?? '';

  const suggestPattern = (name: string) => {
    for (const marker of episodeMarkers) {
      const match = marker.exec(name);
      if (!match) continue;
      const numberStart = match.index + match[0].lastIndexOf(match[1]!);
      const prefix = name.slice(0, numberStart);
      const extension = extensionOf(name);
      const siblings = previewFiles.value.map((f) => f.name).filter((n) => n !== name && n.startsWith(prefix) && extensionOf(n) === extension);

      // Other providers share the prefix too; the sibling with the longest shared ending is the same provider
      const tail = name.slice(numberStart + match[1]!.length);
      let suffix = extension;
      for (const sibling of siblings) {
        let i = 0;
        while (i < tail.length && i < sibling.length - prefix.length && tail[tail.length - 1 - i] === sibling[sibling.length - 1 - i]) i++;
        if (i > suffix.length) suffix = tail.slice(tail.length - i);
      }
      return `${prefix}*${suffix.replace(/^\d+/, '')}`;
    }
    return name;
  };

  const blocker = computed(() => {
    const p = preview.value;
    if (!p) return null;
    if (p.entry.isMovie) return 'A film has no further episodes to track. Add it from the Jimaku page instead.';
    if (p.trackedDeckId != null) return 'This entry is already tracked.';
    if (p.targetDeck?.isSubdeck || (p.targetDeck && !['Anime', 'Drama'].includes(p.targetDeck.mediaType)))
      return 'Only a top-level anime or drama deck can be tracked.';
    if (p.targetDeck?.holdsTextDirectly) return 'This deck holds its text directly. Move the text into a subdeck in the media editor first.';
    return null;
  });

  const canSubmit = computed(() => {
    const p = preview.value;
    if (!p || blocker.value || !p.patternValid || previewLoading.value) return false;
    if (targetDeckId.value == null) return !p.existingDeck && toIngest.value > 0;
    return p.targetDeck != null && matchedFiles.value.length > 0;
  });

  const resetForm = () => {
    jimakuId.value = null;
    preview.value = null;
    pattern.value = '';
    targetDeckId.value = null;
    newExpected.value = null;
    newSeason.value = null;
    searchQuery.value = '';
  };

  const submit = async () => {
    if (!canSubmit.value || jimakuId.value == null) return;
    submitting.value = true;
    try {
      await $api('admin/jimaku-trackers', {
        method: 'POST',
        body: {
          jimakuId: jimakuId.value,
          pattern: pattern.value.trim(),
          deckId: targetDeckId.value,
          tmdbSeason: newSeason.value,
          expectedEpisodes: newExpected.value,
        },
      });
      toast.add({
        severity: 'success',
        summary: targetDeckId.value == null ? 'Deck creation queued' : 'Tracking started',
        detail: targetDeckId.value == null ? 'The show appears in the list once its episodes are parsed.' : 'New episodes are being checked now.',
        life: 6000,
      });
      resetForm();
      await Promise.all([loadTrackers(), loadLatest()]);
      refreshSoon();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Tracking could not be started.'), life: 6000 });
    } finally {
      submitting.value = false;
    }
  };

  const formatSize = (bytes: number) => (bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`);

  onMounted(() => {
    loadTrackers();
    loadLatest();
  });
  onBeforeUnmount(() => {
    if (refreshTimer) clearTimeout(refreshTimer);
    if (previewTimer) clearTimeout(previewTimer);
  });
</script>

<template>
  <div class="container mx-auto p-4">
    <h1 class="text-3xl font-bold mb-2">Anime & Drama Tracking</h1>
    <p class="text-surface-600 dark:text-surface-300 mb-6 max-w-3xl">
      Tracked shows are checked on Jimaku once a day. Each new subtitle file that matches the show's pattern becomes the next episode. Anything unexpected
      waits here for a decision.
    </p>

    <section aria-labelledby="tracked-heading" class="mb-10">
      <div class="flex items-center gap-3 mb-3">
        <h2 id="tracked-heading" class="text-xl font-semibold">Tracked Shows</h2>
        <Button label="Refresh" icon="pi pi-refresh" size="small" severity="secondary" text @click="loadTrackers" />
      </div>

      <Message v-if="loadError" severity="error" :closable="false">{{ loadError }}</Message>
      <div v-else-if="loadingTrackers" class="text-sm text-surface-600 dark:text-surface-300 py-6">Loading tracked shows…</div>
      <div v-else-if="trackers.length === 0" class="rounded-lg border border-dashed border-surface-300 dark:border-surface-600 p-6 text-sm">
        No show is tracked yet. Find an entry below and give it a file pattern to start.
      </div>

      <ul v-else class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 divide-y divide-surface-200 dark:divide-surface-700">
        <li v-for="t in visibleTrackers" :key="t.deckId">
          <div v-if="pendingRemoval.has(t.deckId)" class="flex items-center gap-3 px-3 py-3 text-sm">
            <span class="min-w-0 truncate">Tracking of {{ localiseTitle(t) }} will be removed.</span>
            <Button label="Undo" size="small" severity="secondary" class="ml-auto shrink-0" @click="undoRemoval(t.deckId)" />
          </div>

          <template v-else>
            <div class="flex flex-col lg:flex-row lg:items-center gap-x-4 gap-y-2 px-3 py-3">
              <div class="min-w-0 flex-1 flex items-center gap-3">
                <img :src="coverUrl(t.coverName, 'xs')" alt="" width="40" height="60" loading="lazy" class="h-15 w-10 shrink-0 rounded-sm object-cover" />
                <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-center gap-2">
                  <NuxtLink :to="`/dashboard/media/${t.deckId}`" class="font-medium hover:underline truncate" v-bind="japaneseTextAttrs(localiseTitle(t))">
                    {{ localiseTitle(t) }}
                  </NuxtLink>
                  <Tag :value="t.mediaType" severity="secondary" />
                  <Tooltip v-for="flag in flags(t)" :key="flag.label" :content="flag.hint">
                    <Tag :value="flag.label" :severity="flag.severity" />
                  </Tooltip>
                </div>
                <div class="text-xs text-surface-600 dark:text-surface-300 mt-1 break-all">
                  <a :href="`https://jimaku.cc/entry/${t.jimakuEntryId}`" target="_blank" rel="noopener" class="underline">Jimaku {{ t.jimakuEntryId }}</a>
                  <span class="mx-1" aria-hidden="true">|</span>
                  <code>{{ t.filePattern }}</code>
                </div>
                <p v-if="t.lastError" class="text-xs text-red-700 dark:text-red-300 mt-1">{{ t.lastError }}</p>
                </div>
              </div>

              <dl class="grid grid-cols-3 gap-x-4 text-sm shrink-0 lg:w-80">
                <div>
                  <dt class="text-xs text-surface-600 dark:text-surface-300">Ingested</dt>
                  <dd class="tabular-nums font-medium">{{ t.imported }} of {{ expected(t) ?? '?' }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-surface-600 dark:text-surface-300">Aired</dt>
                  <dd class="tabular-nums">{{ t.finishedAiring ? 'Finished' : (t.airedEpisodes ?? 'Unknown') }}</dd>
                </div>
                <div>
                  <dt class="text-xs text-surface-600 dark:text-surface-300">{{ t.syncEnabled ? 'Next check' : 'Last check' }}</dt>
                  <dd class="text-xs leading-5">
                    {{ t.syncEnabled ? formatDateTime(t.nextCheckAt) : t.lastCheckedAt ? formatDateTime(t.lastCheckedAt) : 'Never' }}
                  </dd>
                </div>
              </dl>

              <div class="flex flex-wrap items-center gap-2 shrink-0">
                <Button label="Check Now" size="small" severity="secondary" @click="checkNow(t)" />
                <Button
                  :label="expandedId === t.deckId ? 'Hide Files' : 'Show Files'"
                  size="small"
                  severity="secondary"
                  :aria-expanded="expandedId === t.deckId"
                  @click="toggleExpanded(t)"
                />
              </div>
            </div>

            <div v-if="expandedId === t.deckId" class="px-3 pb-4 pt-1 bg-surface-50 dark:bg-surface-950">
              <div class="grid grid-cols-1 md:grid-cols-[minmax(0,1fr)_10rem_10rem] gap-3 items-end mb-3">
                <div>
                  <label :for="`pattern-${t.deckId}`" class="block text-sm font-medium mb-1">File pattern</label>
                  <InputText :id="`pattern-${t.deckId}`" v-model="editPattern" class="w-full font-mono text-sm" />
                </div>
                <div>
                  <label :for="`expected-${t.deckId}`" class="block text-sm font-medium mb-1">Episode count</label>
                  <InputNumber
                    v-model="editExpected"
                    :input-id="`expected-${t.deckId}`"
                    :min="1"
                    :placeholder="t.expectedEpisodes != null ? String(t.expectedEpisodes) : 'Unknown'"
                    fluid
                  />
                </div>
                <div v-if="!t.anilistId">
                  <label :for="`season-${t.deckId}`" class="block text-sm font-medium mb-1">TMDB season</label>
                  <InputNumber v-model="editSeason" :input-id="`season-${t.deckId}`" :min="1" placeholder="All" fluid />
                </div>
              </div>
              <div class="flex flex-wrap items-center gap-2 mb-4">
                <Button label="Save" size="small" :loading="savingSettings" @click="saveSettings(t)" />
                <Button v-if="t.syncEnabled" label="Pause" size="small" severity="secondary" @click="setSyncEnabled(t, false)" />
                <Button v-else label="Resume" size="small" severity="secondary" @click="setSyncEnabled(t, true)" />
                <Button label="Remove Tracking" size="small" severity="danger" text @click="removeTracker(t)" />
                <span class="text-xs text-surface-600 dark:text-surface-300">Removing keeps the deck and its episodes.</span>
              </div>

              <div v-if="loadingFiles && files.length === 0" class="text-sm text-surface-600 dark:text-surface-300">Loading files…</div>
              <div v-else-if="files.length === 0" class="text-sm text-surface-600 dark:text-surface-300">
                No matching file has been seen yet. The next check lists what the pattern finds.
              </div>

              <div v-for="group in fileGroups" :key="group.status" class="mb-3 last:mb-0">
                <h3 class="text-sm font-semibold mb-1">{{ groupTitles[group.status] }} ({{ group.items.length }})</h3>
                <ul class="rounded border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 divide-y divide-surface-200 dark:divide-surface-700">
                  <li v-for="file in group.items" :key="file.fileName" class="flex flex-col sm:flex-row sm:items-center gap-x-3 gap-y-1 px-3 py-2 text-sm">
                    <span class="w-10 shrink-0 tabular-nums text-surface-600 dark:text-surface-300">{{ formatEpisode(file.episodeNumber) }}</span>
                    <div class="min-w-0 flex-1">
                      <div class="break-all">{{ file.fileName }}</div>
                      <div v-if="splitReason(file.holdReason)" class="text-xs mt-0.5">
                        <span class="font-semibold">{{ splitReason(file.holdReason)!.label }}:</span>
                        {{ splitReason(file.holdReason)!.detail }}
                      </div>
                    </div>
                    <div class="flex items-center gap-2 shrink-0">
                      <template v-if="file.status === 'Held'">
                        <Button
                          v-if="!file.holdReason?.startsWith('archive')"
                          label="Approve"
                          size="small"
                          :aria-label="`Approve ${file.fileName}`"
                          @click="setFileStatus(t.deckId, file, 'Queued')"
                        />
                        <Button label="Ignore" size="small" severity="secondary" :aria-label="`Ignore ${file.fileName}`" @click="setFileStatus(t.deckId, file, 'Ignored')" />
                      </template>
                      <Button
                        v-else-if="file.status === 'Queued'"
                        label="Put on Hold"
                        size="small"
                        severity="secondary"
                        :aria-label="`Put ${file.fileName} on hold`"
                        @click="setFileStatus(t.deckId, file, 'Held')"
                      />
                      <Button
                        v-else-if="file.status === 'Ignored'"
                        label="Restore"
                        size="small"
                        severity="secondary"
                        :aria-label="`Restore ${file.fileName}`"
                        @click="setFileStatus(t.deckId, file, 'Held')"
                      />
                      <NuxtLink v-else-if="file.childDeckId" :to="`/dashboard/media/${file.childDeckId}`" class="text-xs underline">Subdeck {{ file.childDeckId }}</NuxtLink>
                    </div>
                  </li>
                </ul>
                <div v-if="group.status === 'Queued'" class="mt-2">
                  <Button label="Ingest Now" size="small" @click="checkNow(t)" />
                </div>
              </div>
            </div>
          </template>
        </li>
      </ul>
    </section>

    <section aria-labelledby="add-heading">
      <h2 id="add-heading" class="text-xl font-semibold mb-3">Track a Show</h2>

      <div class="max-w-2xl mb-4">
        <label for="jimakuSearch" class="block text-sm font-medium mb-1">Jimaku entry</label>
        <div class="flex flex-col sm:flex-row gap-2">
          <SelectButton v-model="searchKind" :options="kindOptions" option-label="label" option-value="value" :allow-empty="false" aria-label="Entry type" />
          <InputGroup class="flex-1">
            <InputText id="jimakuSearch" v-model="searchQuery" placeholder="Title, entry number, or entry link" @keydown.enter="search" />
            <Button label="Find" icon="pi pi-search" :loading="searching" :disabled="!searchQuery.trim()" @click="search" />
          </InputGroup>
        </div>

        <div v-if="searchResults && searchResults.length === 0" class="text-sm mt-3">No entry matches this title. Try the romaji title or paste the entry link.</div>
        <ul
          v-else-if="searchResults"
          class="mt-3 rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 divide-y divide-surface-200 dark:divide-surface-700 max-h-72 overflow-y-auto"
        >
          <li v-for="result in searchResults" :key="result.id">
            <button
              type="button"
              class="w-full text-left px-3 py-2 min-h-11 hover:bg-surface-100 dark:hover:bg-surface-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary-500 focus-visible:-outline-offset-2"
              @click="selectEntry(result.id)"
            >
              <span class="font-medium">{{ result.name }}</span>
              <span v-if="result.japaneseName" class="block text-xs text-surface-600 dark:text-surface-300" v-bind="japaneseTextAttrs(result.japaneseName)">{{ result.japaneseName }}</span>
            </button>
          </li>
        </ul>

        <template v-else-if="jimakuId == null">
          <h3 class="font-semibold mt-5 mb-2">Latest Uploads Not Tracked Yet</h3>
          <Message v-if="latestError" severity="error" :closable="false">{{ latestError }}</Message>
          <div v-else-if="latestLoading && latest.length === 0" class="text-sm text-surface-600 dark:text-surface-300">Loading the latest entries…</div>
          <div v-else-if="latest.length === 0" class="text-sm">No untracked entry received a file in the last 10 days. Search by title to find an older one.</div>
          <ul
            v-else
            class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 divide-y divide-surface-200 dark:divide-surface-700 overflow-y-auto"
            style="max-height: min(60vh, 36rem)"
          >
            <li v-for="result in latest" :key="result.id">
              <button
                type="button"
                class="w-full text-left flex items-center gap-3 px-3 py-2 min-h-11 hover:bg-surface-100 dark:hover:bg-surface-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary-500 focus-visible:-outline-offset-2"
                @click="selectEntry(result.id)"
              >
                <span class="min-w-0 flex-1">
                  <span class="block font-medium">{{ result.name }}</span>
                  <span v-if="result.japaneseName" class="block text-xs text-surface-600 dark:text-surface-300" v-bind="japaneseTextAttrs(result.japaneseName)">{{
                    result.japaneseName
                  }}</span>
                </span>
                <span v-if="result.lastModified" class="shrink-0 text-xs tabular-nums text-surface-600 dark:text-surface-300">{{ formatDayMonthTime(result.lastModified) }}</span>
              </button>
            </li>
          </ul>
        </template>
      </div>

      <Message v-if="previewError" severity="error" :closable="false" class="mb-4 max-w-2xl">{{ previewError }}</Message>
      <div v-else-if="jimakuId != null && !preview" class="text-sm text-surface-600 dark:text-surface-300">Reading entry {{ jimakuId }}…</div>

      <div v-if="preview" class="grid grid-cols-1 xl:grid-cols-[minmax(0,26rem)_minmax(0,1fr)] gap-6">
        <div>
          <div class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 p-4 mb-4">
            <Button label="Choose Another Entry" icon="pi pi-arrow-left" size="small" severity="secondary" text class="mb-2 -ml-2" @click="resetForm" />
            <h3 class="font-semibold">
              <a :href="`https://jimaku.cc/entry/${preview.entry.id}`" target="_blank" rel="noopener" class="hover:underline">
                {{ preview.entry.name }}
                <i class="pi pi-external-link text-xs align-middle" aria-hidden="true" />
              </a>
            </h3>
            <div v-if="preview.entry.japaneseName" class="text-sm" v-bind="japaneseTextAttrs(preview.entry.japaneseName)">{{ preview.entry.japaneseName }}</div>
            <div class="text-sm text-surface-600 dark:text-surface-300 mt-2">
              {{ preview.entry.mediaType }}, Jimaku {{ preview.entry.id }}.
              <template v-if="preview.airing?.total != null">
                {{ preview.entry.anilistId ? 'AniList' : 'TMDB' }} lists {{ preview.airing.total }} episodes<template v-if="preview.airing.finished"
                  >, finished airing</template
                ><template v-else-if="preview.airing.aired != null">, {{ preview.airing.aired }} aired</template>.
              </template>
              <template v-else>The episode count is unknown.</template>
            </div>
          </div>

          <Message v-if="blocker" severity="warn" :closable="false" class="mb-4">
            {{ blocker }}
            <NuxtLink v-if="preview.trackedDeckId != null" :to="`/dashboard/media/${preview.trackedDeckId}`" class="underline ml-1">Open deck {{ preview.trackedDeckId }}</NuxtLink>
          </Message>
          <Message v-else-if="preview.existingDeck && targetDeckId == null" severity="warn" :closable="false" class="mb-4">
            <span v-bind="japaneseTextAttrs(preview.existingDeck.originalTitle)">{{ preview.existingDeck.originalTitle }}</span> is already on Jiten as deck {{ preview.existingDeck.deckId }}.
            <Button label="Track That Deck" size="small" class="ml-2" @click="targetDeckId = preview.existingDeck.deckId" />
          </Message>

          <div class="mb-4">
            <label for="newPattern" class="block text-sm font-medium mb-1">File pattern</label>
            <InputText id="newPattern" v-model="pattern" class="w-full font-mono text-sm" placeholder="Show.S01E*.WEBRip.Netflix.ja[cc].srt" />
            <p class="text-xs text-surface-600 dark:text-surface-300 mt-1">
              * stands for any text. Upper and lower case are treated alike. Select a file in the list to build the pattern from its name.
            </p>
          </div>

          <div class="grid grid-cols-2 gap-3 mb-4">
            <div>
              <label for="targetDeck" class="block text-sm font-medium mb-1">Existing deck number</label>
              <InputNumber v-model="targetDeckId" input-id="targetDeck" :use-grouping="false" :min="1" placeholder="New deck" fluid />
            </div>
            <div>
              <label for="newExpected" class="block text-sm font-medium mb-1">Episode count</label>
              <InputNumber
                v-model="newExpected"
                input-id="newExpected"
                :min="1"
                :placeholder="preview.airing?.total != null ? String(preview.airing.total) : 'Unknown'"
                fluid
              />
            </div>
            <div v-if="!preview.entry.anilistId">
              <label for="newSeason" class="block text-sm font-medium mb-1">TMDB season</label>
              <InputNumber v-model="newSeason" input-id="newSeason" :min="1" placeholder="All" fluid />
            </div>
          </div>

          <p v-if="preview.targetDeck" class="text-sm mb-4">
            <span v-bind="japaneseTextAttrs(preview.targetDeck.originalTitle)">{{ preview.targetDeck.originalTitle }}</span> has {{ preview.targetDeck.episodeCount }} subdecks.
            <template v-if="preview.targetDeck.episodeCount > 0 && preview.targetDeck.namedEpisodeCount === 0">
              None carries a file name, so the first {{ alreadyInDeck }} matching files count as already ingested.
            </template>
          </p>

          <p v-if="preview.patternValid" class="text-sm mb-4" aria-live="polite">
            <span class="font-semibold tabular-nums">{{ matchedFiles.length }}</span> matching,
            <span class="font-semibold tabular-nums">{{ toIngest }}</span> to ingest<template v-if="alreadyInDeck"
              >, <span class="font-semibold tabular-nums">{{ alreadyInDeck }}</span> already in the deck</template
            ><template v-if="toHold">, <span class="font-semibold tabular-nums">{{ toHold }}</span> on hold</template>.
          </p>

          <Button
            :label="targetDeckId == null ? 'Create Deck & Track' : 'Start Tracking'"
            icon="pi pi-plus"
            :loading="submitting"
            :disabled="!canSubmit"
            @click="submit"
          />
        </div>

        <div class="min-w-0">
          <h3 class="font-semibold mb-2">Files in This Entry ({{ previewFiles.length }})</h3>
          <div v-if="previewFiles.length === 0" class="text-sm">This entry has no file yet. Tracking can start once the first episode is uploaded.</div>
          <ul
            v-else
            class="rounded-lg border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 divide-y divide-surface-200 dark:divide-surface-700 overflow-y-auto"
            style="max-height: min(70vh, 44rem)"
          >
            <li v-for="file in previewFiles" :key="file.name">
              <button
                type="button"
                class="w-full text-left flex flex-col sm:flex-row sm:items-center gap-x-3 gap-y-1 px-3 py-2 min-h-11 hover:bg-surface-100 dark:hover:bg-surface-800 focus-visible:outline focus-visible:outline-2 focus-visible:outline-primary-500 focus-visible:-outline-offset-2"
                :class="file.matched ? 'bg-primary-50 dark:bg-primary-950' : ''"
                :aria-label="`Build the pattern from ${file.name}`"
                @click="pattern = suggestPattern(file.name)"
              >
                <span class="min-w-0 flex-1 break-all text-sm" :class="file.matched ? 'font-medium' : ''">{{ file.name }}</span>
                <span class="flex items-center gap-2 shrink-0 text-xs">
                  <Tag v-if="file.inDeck" value="In deck" severity="secondary" />
                  <Tag v-else-if="file.position != null" :value="`Episode ${file.position}`" severity="success" />
                  <Tag v-else-if="file.status === 'Held'" :value="`Hold: ${splitReason(file.holdReason)?.label ?? ''}`" severity="warn" />
                  <span class="tabular-nums text-surface-600 dark:text-surface-300">{{ formatSize(file.size) }}</span>
                </span>
              </button>
            </li>
          </ul>
        </div>
      </div>
    </section>
  </div>
</template>
