<script setup lang="ts">
  import { DeckStatus, type MediaType } from '~/types';
  import { getDeckStatusText } from '~/utils/deckStatusMapper';
  import { getMediaTypeText } from '~/utils/mediaTypeMapper';
  import { coverUrl } from '~/utils/coverImage';
  import { useJitenStore } from '~/stores/jitenStore';

  const props = defineProps<{ provider: 'anilist' | 'vndb' | 'file' }>();
  const emit = defineEmits<{ changed: [] }>();

  const { $api } = useNuxtApp();
  const toast = useToast();

  interface PreviewRow {
    deckId: number;
    originalTitle: string;
    romajiTitle: string | null;
    englishTitle: string | null;
    coverName: string;
    mediaType: MediaType;
    externalStatus: string;
    mappedStatus: DeckStatus;
    finishedAt: string | null;
    progress: number | null;
    subdeckCount: number | null;
    currentStatus: DeckStatus | null;
    isIgnored: boolean;
    isFavourite?: boolean;
    currentFavourite?: boolean;
  }

  interface PreviewData {
    username?: string;
    fileName?: string;
    matched: PreviewRow[];
    unmatched: { title: string; url: string | null; externalStatus: string; mappedStatus: DeckStatus }[];
    counts: { total: number; matched: number; unmatched: number; conflicts: number };
  }

  type ViewKey = 'import' | 'all' | 'excluded' | 'conflicts' | 'ignored';
  type SortKey = 'title' | 'type' | 'source' | 'action' | 'finished';
  type SortDir = 'asc' | 'desc';
  type ActionKind = 'ignored' | 'excluded' | 'conflict' | 'favourite' | 'unchanged' | 'none';

  const store = useJitenStore();
  const localiseTitle = useLocaliseTitle();

  const isFile = computed(() => props.provider === 'file');

  const providerName = computed(() => (props.provider === 'anilist' ? 'AniList' : props.provider === 'vndb' ? 'VNDB' : 'Jiten export'));

  const sourceColumnLabel = computed(() => (isFile.value ? 'File' : providerName.value));

  const panelTitle = computed(() => (isFile.value ? 'Restore from a Jiten export' : `Import from ${providerName.value}`));

  const identifierHint = computed(() =>
    props.provider === 'anilist'
      ? 'Enter your AniList username or your profile URL.'
      : 'Enter your VNDB username, your user id (starting with u) or your profile URL.'
  );

  const identifierPlaceholder = computed(() => (props.provider === 'anilist' ? 'Username or profile URL' : 'Username, uXX id or URL'));

  const username = ref('');
  const fileInput = ref<HTMLInputElement | null>(null);
  const selectedFile = ref<File | null>(null);
  const cooldownSeconds = ref(0);
  const cooldownGeneric = ref(false);
  let cooldownTimer: ReturnType<typeof setInterval> | null = null;
  const isLoading = ref(false);
  const loadingMessage = ref('');
  const preview = ref<PreviewData | null>(null);

  const sourceLabel = computed(() => (isFile.value ? (preview.value?.fileName ?? 'Your export file') : preview.value?.username || username.value.trim()));

  const includeStatuses = ref<Record<number, boolean>>({});
  const includedTypes = ref<MediaType[]>([]);
  const cutoffEnabled = ref(false);
  const cutoffDate = ref<Date | null>(null);
  const importProgress = ref(false);
  const conflictMode = ref<'keep' | 'overwrite'>('keep');
  const rowResolutions = ref<Record<number, 'keep' | 'overwrite'>>({});
  const rowOverrides = ref<Record<number, boolean>>({});
  const unmatchedOpen = ref(false);

  const search = ref('');
  const activeView = ref<ViewKey>('import');
  const sortKey = ref<SortKey>('title');
  const sortDir = ref<SortDir>('asc');

  const applied = ref<{
    added: number;
    updated: number;
    unchanged: number;
    skippedIgnored: number;
    favourited: number;
    subdecksCompleted: number;
  } | null>(null);

  const statusOrder = [DeckStatus.Completed, DeckStatus.Ongoing, DeckStatus.Planning, DeckStatus.Dropped];

  const statusChipClass: Record<number, string> = {
    [DeckStatus.Planning]: 'bg-gray-100 text-gray-600 dark:bg-gray-700/40 dark:text-gray-300',
    [DeckStatus.Ongoing]: 'bg-amber-100 text-amber-700 dark:bg-amber-900/40 dark:text-amber-300',
    [DeckStatus.Completed]: 'bg-green-100 text-green-700 dark:bg-green-900/40 dark:text-green-300',
    [DeckStatus.Dropped]: 'bg-red-100 text-red-700 dark:bg-red-900/40 dark:text-red-300',
  };

  const externalChipClass = 'bg-surface-100 text-surface-600 dark:bg-surface-700/60 dark:text-surface-300';

  const sortOptions = computed<{ label: string; value: SortKey }[]>(() => [
    { label: 'Title', value: 'title' },
    { label: 'Type', value: 'type' },
    { label: 'Source status', value: 'source' },
    { label: 'Action', value: 'action' },
    ...(isFile.value ? [] : [{ label: 'Date', value: 'finished' as SortKey }]),
  ]);

  const defaultSortDir: Record<SortKey, SortDir> = { title: 'asc', type: 'asc', source: 'asc', action: 'asc', finished: 'desc' };

  function chooseSort(key: SortKey) {
    sortKey.value = key;
    sortDir.value = defaultSortDir[key];
  }

  function setSort(key: SortKey) {
    if (sortKey.value === key) sortDir.value = sortDir.value === 'asc' ? 'desc' : 'asc';
    else chooseSort(key);
  }

  const sortIcon = computed(() => (sortDir.value === 'asc' ? 'material-symbols:arrow-upward' : 'material-symbols:arrow-downward'));

  function sortLabel(key: SortKey, label: string): string {
    if (sortKey.value !== key) return `Sort by ${label}`;
    return sortDir.value === 'asc' ? `Sorted by ${label}, ascending. Click to reverse.` : `Sorted by ${label}, descending. Click to reverse.`;
  }

  const showDateColumn = computed(() => !isFile.value);

  function formatFinished(value: string | null): string {
    if (!value) return '';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString(undefined, { year: 'numeric', month: 'short' });
  }

  const allRows = computed<PreviewRow[]>(() => preview.value?.matched ?? []);

  const typeOptions = computed(() => {
    const counts = new Map<MediaType, number>();
    for (const row of allRows.value) counts.set(row.mediaType, (counts.get(row.mediaType) ?? 0) + 1);
    return [...counts.entries()]
      .map(([value, count]) => ({ value, count, label: getMediaTypeText(value) }))
      .sort((a, b) => b.count - a.count || a.label.localeCompare(b.label));
  });

  function parseRetryAfter(value: string | null): number | null {
    if (!value) return null;

    const seconds = Number(value);
    if (Number.isFinite(seconds)) return seconds > 0 ? Math.ceil(seconds) : null;

    const date = Date.parse(value);
    if (Number.isNaN(date)) return null;

    const delta = Math.ceil((date - Date.now()) / 1000);
    return delta > 0 ? delta : null;
  }

  function startCooldown(seconds: number | null) {
    if (cooldownTimer) clearInterval(cooldownTimer);

    if (seconds == null) {
      cooldownSeconds.value = 0;
      cooldownGeneric.value = true;
      return;
    }

    cooldownGeneric.value = false;
    cooldownSeconds.value = seconds;
    cooldownTimer = setInterval(() => {
      cooldownSeconds.value -= 1;
      if (cooldownSeconds.value <= 0 && cooldownTimer) {
        clearInterval(cooldownTimer);
        cooldownTimer = null;
      }
    }, 1000);
  }

  function formatWait(seconds: number): string {
    if (seconds < 60) return seconds === 1 ? '1 second' : `${seconds} seconds`;
    const minutes = Math.ceil(seconds / 60);
    return minutes === 1 ? '1 minute' : `${minutes} minutes`;
  }

  const cooldownMessage = computed(() => {
    if (cooldownSeconds.value > 0) return `Please wait ${formatWait(cooldownSeconds.value)} before fetching again.`;
    return cooldownGeneric.value ? 'Please wait a moment before fetching again.' : '';
  });

  onUnmounted(() => {
    if (cooldownTimer) clearInterval(cooldownTimer);
  });

  function startOver() {
    preview.value = null;
    selectedFile.value = null;
  }

  function onFilePicked(event: Event) {
    const input = event.target as HTMLInputElement;
    selectedFile.value = input.files?.[0] ?? null;
    if (selectedFile.value) fetchPreview();
    input.value = '';
  }

  async function fetchPreview() {
    const blocked = isFile.value ? !selectedFile.value : !username.value.trim() || cooldownSeconds.value > 0;
    if (blocked) return;

    cooldownGeneric.value = false;
    isLoading.value = true;
    loadingMessage.value = isFile.value ? 'Reading your file...' : `Fetching your ${providerName.value} list...`;
    applied.value = null;

    try {
      let data: PreviewData;

      if (isFile.value) {
        const form = new FormData();
        form.append('file', selectedFile.value as File);
        data = await $api<PreviewData>('user/media-list/import/file-preview', { method: 'POST', body: form });
      } else {
        data = await $api<PreviewData>('user/media-list/import/preview', {
          method: 'POST',
          body: { provider: props.provider, username: username.value.trim() },
        });
      }

      preview.value = data;
      includeStatuses.value = Object.fromEntries(statusOrder.map((s) => [s, isFile.value || s !== DeckStatus.Dropped]));
      includedTypes.value = typeOptions.value.map((o) => o.value);
      rowResolutions.value = {};
      rowOverrides.value = {};
      importProgress.value = true;
      cutoffEnabled.value = false;
      cutoffDate.value = null;
      unmatchedOpen.value = false;
      search.value = '';
      sortKey.value = 'title';
      sortDir.value = 'asc';
      activeView.value = 'import';

      if (data.counts.total === 0 && !isFile.value) {
        toast.add({
          severity: 'info',
          summary: 'Empty list',
          detail: `No entries found. If your ${providerName.value} list is private, make it public and try again.`,
          life: 8000,
        });
      }
    } catch (error: unknown) {
      const err = error as { status?: number; statusCode?: number; response?: Response; data?: { message?: string } } | null;

      if ((err?.status ?? err?.statusCode) === 429) {
        startCooldown(parseRetryAfter(err?.response?.headers?.get('Retry-After') ?? null));
        toast.add({ severity: 'warn', summary: 'Too many requests', detail: cooldownMessage.value, life: 8000 });
      } else if (isFile.value) {
        selectedFile.value = null;
        const message = err?.data?.message ?? 'Could not read that file.';
        toast.add({ severity: 'error', summary: 'Import failed', detail: message, life: 8000 });
      } else {
        const message = err?.data?.message ?? `Could not fetch your ${providerName.value} list.`;
        toast.add({ severity: 'error', summary: 'Fetch failed', detail: message, life: 8000 });
      }
    } finally {
      isLoading.value = false;
    }
  }

  const statusGroupCounts = computed(() => {
    const counts = new Map<number, number>();
    for (const row of allRows.value) counts.set(row.mappedStatus, (counts.get(row.mappedStatus) ?? 0) + 1);
    return counts;
  });

  function passesCutoff(row: PreviewRow): boolean {
    if (!cutoffEnabled.value || !cutoffDate.value || !row.finishedAt) return true;
    return new Date(row.finishedAt) >= cutoffDate.value;
  }

  const cutoffLabel = computed(() => {
    const date = cutoffDate.value;
    return date ? `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}` : '';
  });

  const cutoffExcludedCount = computed(() => {
    if (!cutoffEnabled.value || !cutoffDate.value) return 0;
    return allRows.value.filter((r) => !passesCutoff(r)).length;
  });

  function passesType(row: PreviewRow): boolean {
    return includedTypes.value.includes(row.mediaType);
  }

  function isRowIncluded(row: PreviewRow): boolean {
    const override = rowOverrides.value[row.deckId];
    if (override !== undefined) return override;
    return (includeStatuses.value[row.mappedStatus] ?? true) && passesType(row) && passesCutoff(row);
  }

  function toggleRow(row: PreviewRow) {
    rowOverrides.value[row.deckId] = !isRowIncluded(row);
  }

  function exclusionReason(row: PreviewRow): string {
    if (rowOverrides.value[row.deckId] === false) return 'Unticked';
    if (!(includeStatuses.value[row.mappedStatus] ?? true)) return 'Status off';
    if (!passesType(row)) return 'Type off';
    return `Before ${cutoffLabel.value}`;
  }

  function isConflict(row: PreviewRow): boolean {
    return !row.isIgnored && row.currentStatus != null && row.currentStatus !== row.mappedStatus;
  }

  function rowResolution(row: PreviewRow): 'keep' | 'overwrite' {
    return rowResolutions.value[row.deckId] ?? conflictMode.value;
  }

  function externalStatusDisplay(row: PreviewRow): string {
    const s = row.externalStatus;
    return s === s.toUpperCase() ? s.charAt(0) + s.slice(1).toLowerCase() : s;
  }

  function externalDiffers(row: PreviewRow): boolean {
    return externalStatusDisplay(row).toLowerCase() !== getDeckStatusText(row.mappedStatus).toLowerCase();
  }

  function needsFavourite(row: PreviewRow): boolean {
    return !!row.isFavourite && !row.currentFavourite;
  }

  function actionKind(row: PreviewRow): ActionKind {
    if (row.isIgnored) return 'ignored';
    if (!isRowIncluded(row)) return 'excluded';
    if (isConflict(row)) return 'conflict';
    if (row.currentStatus === row.mappedStatus) return needsFavourite(row) ? 'favourite' : 'unchanged';
    return 'none';
  }

  function actionRank(row: PreviewRow): number {
    if (row.isIgnored) return 4;
    if (row.currentStatus == null) return 0;
    if (row.currentStatus === row.mappedStatus) return 3;
    return rowResolution(row) === 'overwrite' ? 1 : 2;
  }

  function progressUnits(row: PreviewRow): number {
    if (row.mappedStatus === DeckStatus.Completed || !row.progress || !row.subdeckCount) return 0;
    return Math.min(row.progress, row.subdeckCount);
  }

  function showUnits(row: PreviewRow): boolean {
    return importProgress.value && progressUnits(row) > 0;
  }

  const progressRows = computed(() => allRows.value.filter((row) => progressUnits(row) > 0));
  const progressUnitTotal = computed(() => applyRows.value.reduce((sum, row) => sum + progressUnits(row), 0));

  const applyRows = computed(() =>
    allRows.value.filter((row) => {
      if (!isRowIncluded(row) || row.isIgnored) return false;
      if (row.currentStatus === row.mappedStatus) return needsFavourite(row);
      if (isConflict(row) && rowResolution(row) === 'keep') return false;
      return true;
    })
  );

  const excludedRows = computed(() => allRows.value.filter((row) => !row.isIgnored && !isRowIncluded(row)));
  const conflictRows = computed(() => allRows.value.filter((row) => isConflict(row)));
  const ignoredRows = computed(() => allRows.value.filter((row) => row.isIgnored));

  const newCount = computed(() => applyRows.value.filter((r) => r.currentStatus == null).length);
  const updateCount = computed(() => applyRows.value.filter((r) => r.currentStatus != null).length);

  const viewOptions = computed(() => [
    { label: 'Will import', value: 'import' as ViewKey, count: applyRows.value.length },
    { label: 'All', value: 'all' as ViewKey, count: allRows.value.length },
    { label: 'Excluded', value: 'excluded' as ViewKey, count: excludedRows.value.length },
    { label: 'Conflicts', value: 'conflicts' as ViewKey, count: conflictRows.value.length },
    { label: 'Ignored', value: 'ignored' as ViewKey, count: ignoredRows.value.length },
  ]);

  const viewSelectOptions = computed(() => viewOptions.value.map((o) => ({ ...o, label: `${o.label} (${o.count})` })));

  const viewRows = computed<PreviewRow[]>(() => {
    switch (activeView.value) {
      case 'import':
        return applyRows.value;
      case 'excluded':
        return excludedRows.value;
      case 'conflicts':
        return conflictRows.value;
      case 'ignored':
        return ignoredRows.value;
      default:
        return allRows.value;
    }
  });

  function rowTitle(row: PreviewRow): string {
    return localiseTitle(row) || `Deck ${row.deckId}`;
  }

  function rowAlternateTitles(row: PreviewRow): { text: string; ja: boolean }[] {
    const shown = rowTitle(row);
    const list: { text: string; ja: boolean }[] = [];
    const push = (text: string | null | undefined, ja: boolean) => {
      if (text && text !== shown && !list.some((e) => e.text === text)) list.push({ text, ja });
    };
    push(row.originalTitle, true);
    push(row.romajiTitle, false);
    push(row.englishTitle, false);
    return list;
  }

  function showAlternateTitles(row: PreviewRow): boolean {
    return !store.hideAlternativeTitles && rowAlternateTitles(row).length > 0;
  }

  const searchIndex = computed(() => {
    const index = new Map<number, string>();
    for (const row of allRows.value) {
      index.set(row.deckId, `${row.originalTitle ?? ''}\n${row.romajiTitle ?? ''}\n${row.englishTitle ?? ''}`.toLowerCase());
    }
    return index;
  });

  const visibleRows = computed(() => {
    const term = search.value.trim().toLowerCase();
    const rows = term ? viewRows.value.filter((r) => searchIndex.value.get(r.deckId)?.includes(term)) : [...viewRows.value];
    const byTitle = (a: PreviewRow, b: PreviewRow) => rowTitle(a).localeCompare(rowTitle(b));
    const dir = sortDir.value === 'desc' ? -1 : 1;

    const ascending = (a: PreviewRow, b: PreviewRow) => {
      switch (sortKey.value) {
        case 'type':
          return getMediaTypeText(a.mediaType).localeCompare(getMediaTypeText(b.mediaType));
        case 'source':
          return a.externalStatus.localeCompare(b.externalStatus);
        case 'action':
          return actionRank(a) - actionRank(b);
        default:
          return byTitle(a, b);
      }
    };

    if (sortKey.value === 'finished') {
      rows.sort((a, b) => {
        const left = a.finishedAt ? Date.parse(a.finishedAt) : Number.NaN;
        const right = b.finishedAt ? Date.parse(b.finishedAt) : Number.NaN;
        // Entries with no date stay at the bottom whichever way the column is sorted.
        if (Number.isNaN(left) && Number.isNaN(right)) return byTitle(a, b);
        if (Number.isNaN(left)) return 1;
        if (Number.isNaN(right)) return -1;
        return (left - right) * dir || byTitle(a, b);
      });
    } else {
      rows.sort((a, b) => ascending(a, b) * dir || byTitle(a, b));
    }

    return rows;
  });

  function summaryCardClass(view: ViewKey): string {
    return activeView.value === view
      ? 'border-surface-400 dark:border-surface-500'
      : 'border-surface-200 hover:border-surface-300 dark:border-surface-700 dark:hover:border-surface-600';
  }

  function onCoverError(event: Event) {
    const img = event.target as HTMLImageElement;
    if (img.dataset.fallback) return;
    img.dataset.fallback = '1';
    img.src = '/img/nocover.jpg';
  }

  async function applyImport() {
    if (applyRows.value.length === 0) return;

    isLoading.value = true;
    loadingMessage.value = `Importing ${applyRows.value.length} titles...`;

    try {
      const result = await $api<NonNullable<typeof applied.value>>('user/media-list/import/apply', {
        method: 'POST',
        body: {
          overwriteExisting: true,
          entries: applyRows.value.map((r) => {
            const units = importProgress.value ? progressUnits(r) : 0;
            const base = { deckId: r.deckId, status: r.mappedStatus, isFavourite: !!r.isFavourite };
            return units > 0 ? { ...base, progress: units, overwriteSubdecks: rowResolution(r) === 'overwrite' } : base;
          }),
        },
      });

      applied.value = result;
      preview.value = null;
      selectedFile.value = null;
      emit('changed');
      toast.add({
        severity: 'success',
        summary: 'Import complete',
        detail:
          `Added ${result.added}, updated ${result.updated}` +
          `${result.favourited > 0 ? `, ${result.favourited} favourited` : ''}` +
          `${result.subdecksCompleted > 0 ? `, ${result.subdecksCompleted} subdecks completed` : ''}.`,
        life: 6000,
      });
    } catch (error: unknown) {
      const message = (error as { data?: { message?: string } })?.data?.message ?? 'Import failed. Please try again.';
      toast.add({ severity: 'error', summary: 'Import failed', detail: message, life: 8000 });
    } finally {
      isLoading.value = false;
    }
  }
</script>

<template>
  <Card>
    <template #title>
      <h3 class="text-lg font-semibold">{{ panelTitle }}</h3>
    </template>
    <template #content>
      <div class="flex flex-col gap-4">
        <!-- Step 1: source -->
        <div v-if="!preview">
          <template v-if="isFile">
            <p class="mb-3 text-sm text-gray-600 dark:text-gray-400">
              Pick the CSV or JSON file you downloaded from the export tab. You will be able to preview and filter what you want to import.
            </p>
            <div class="flex flex-wrap items-center gap-2">
              <input ref="fileInput" type="file" accept=".csv,.json,text/csv,application/json" class="hidden" @change="onFilePicked" />
              <Button label="Choose file" icon="pi pi-file-import" :disabled="isLoading" @click="fileInput?.click()" />
              <span v-if="selectedFile" class="max-w-full truncate text-sm text-gray-500 dark:text-gray-400">{{ selectedFile.name }}</span>
            </div>
          </template>
          <template v-else>
            <p class="mb-3 text-sm text-gray-600 dark:text-gray-400">
              {{ identifierHint }} Your list must be public and you will be able to preview and filter the titles you want to import.
            </p>
            <div class="flex flex-wrap items-center gap-2">
              <InputText v-model="username" :placeholder="identifierPlaceholder" class="w-full sm:w-96" :disabled="isLoading" @keyup.enter="fetchPreview" />
              <Button label="Fetch list" icon="pi pi-cloud-download" :disabled="!username.trim() || isLoading || cooldownSeconds > 0" @click="fetchPreview" />
            </div>
            <p v-if="cooldownMessage" class="mt-2 text-sm font-medium text-amber-600 dark:text-amber-400">{{ cooldownMessage }}</p>
          </template>

          <Message v-if="applied" severity="success" class="mt-4">
            Imported: {{ applied.added }} added, {{ applied.updated }} updated<template v-if="applied.favourited > 0"
              >, {{ applied.favourited }} favourited</template
            ><template v-if="applied.subdecksCompleted > 0">, {{ applied.subdecksCompleted }} subdecks completed</template
            ><template v-if="applied.skippedIgnored > 0">, {{ applied.skippedIgnored }} skipped (ignored)</template>.
            <NuxtLink to="/profile" class="ml-1">View your media list</NuxtLink>
          </Message>
        </div>

        <!-- Step 2: preview -->
        <div v-else class="flex flex-col gap-4">
          <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <span class="font-semibold">{{ providerName }} · {{ sourceLabel }}</span>
            <span class="text-sm text-gray-500 dark:text-gray-400">{{ isFile ? 'read' : 'fetched' }} {{ preview.counts.total }} entries</span>
            <Button label="Start over" severity="secondary" text size="small" class="ml-auto" @click="startOver" />
          </div>

          <!-- Counts -->
          <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
            <div class="rounded border border-surface-200 p-3 dark:border-surface-700">
              <div class="text-xl font-bold tabular-nums">{{ preview.counts.matched }}</div>
              <div class="text-xs font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">Matched</div>
            </div>
            <button type="button" class="rounded border p-3 text-left" :class="summaryCardClass('import')" @click="activeView = 'import'">
              <div class="text-xl font-bold tabular-nums">{{ applyRows.length }}</div>
              <div class="text-xs font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">To import</div>
            </button>
            <button type="button" class="rounded border p-3 text-left" :class="summaryCardClass('conflicts')" @click="activeView = 'conflicts'">
              <div class="text-xl font-bold tabular-nums text-amber-600 dark:text-amber-400">{{ conflictRows.length }}</div>
              <div class="text-xs font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">Conflicts</div>
            </button>
            <div class="rounded border border-surface-200 p-3 dark:border-surface-700">
              <div class="text-xl font-bold tabular-nums text-gray-400 dark:text-gray-500">{{ preview.counts.unmatched }}</div>
              <div class="text-xs font-semibold uppercase tracking-wider text-gray-500 dark:text-gray-400">Not on Jiten</div>
            </div>
          </div>

          <!-- Filters -->
          <div class="flex flex-col gap-3 rounded border border-surface-200 p-3 dark:border-surface-700">
            <div class="flex flex-wrap items-center gap-x-5 gap-y-2">
              <span class="text-xs font-bold uppercase tracking-widest text-gray-500 dark:text-gray-400">Include</span>
              <label v-for="status in statusOrder" :key="status" class="flex items-center gap-2 text-sm">
                <Checkbox v-model="includeStatuses[status]" :binary="true" :disabled="!statusGroupCounts.get(status)" />
                {{ getDeckStatusText(status) }}
                <span class="text-xs tabular-nums text-gray-400 dark:text-gray-500">{{ statusGroupCounts.get(status) ?? 0 }}</span>
              </label>
              <div v-if="typeOptions.length > 1" class="max-w-full overflow-x-auto">
                <SelectButton v-model="includedTypes" :options="typeOptions" option-label="label" option-value="value" multiple size="small">
                  <template #option="{ option }">
                    <span>{{ option.label }}</span>
                    <span class="ml-1.5 text-xs tabular-nums opacity-60">{{ option.count }}</span>
                  </template>
                </SelectButton>
              </div>
            </div>

            <div v-if="!isFile" class="flex flex-wrap items-center gap-2">
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="cutoffEnabled" :binary="true" />
                Only entries finished or started after
              </label>
              <DatePicker v-model="cutoffDate" view="month" date-format="yy-mm" show-icon :disabled="!cutoffEnabled" class="w-40" size="small" />
              <span v-if="cutoffEnabled && cutoffDate" class="text-xs text-gray-500 dark:text-gray-400">
                {{ cutoffExcludedCount }} entries older than your set date were filtered out.
              </span>
            </div>

            <div v-if="progressRows.length > 0" class="flex flex-wrap items-center gap-2">
              <label class="flex items-center gap-2 text-sm">
                <Checkbox v-model="importProgress" :binary="true" />
                Mark watched episodes / read volumes as completed subdecks
              </label>
              <span class="text-xs text-gray-500 dark:text-gray-400">
                {{ progressRows.length }} titles have progress<template v-if="importProgress && progressUnitTotal > 0"
                  >; {{ progressUnitTotal }} volumes/episodes will be marked as completed</template
                >.
              </span>
            </div>

            <div class="flex flex-wrap items-center gap-3 border-t border-surface-200 pt-3 dark:border-surface-700">
              <span class="text-xs font-bold text-gray-500 dark:text-gray-400">In case of conflict</span>
              <SelectButton
                v-model="conflictMode"
                :options="[
                  { label: 'Keep mine', value: 'keep' },
                  { label: 'Overwrite', value: 'overwrite' },
                ]"
                option-label="label"
                option-value="value"
                size="small"
                :allow-empty="false"
              />
              <span class="text-xs text-gray-500 dark:text-gray-400">You can override each row individually in the Conflicts tab.</span>
            </div>
          </div>

          <div class="flex flex-wrap items-center gap-2">
            <SelectButton
              v-model="activeView"
              class="!hidden sm:!inline-flex"
              :options="viewOptions"
              option-label="label"
              option-value="value"
              size="small"
              :allow-empty="false"
            >
              <template #option="{ option }">
                <span>{{ option.label }}</span>
                <span class="ml-1.5 text-xs tabular-nums opacity-60">{{ option.count }}</span>
              </template>
            </SelectButton>
            <Select v-model="activeView" :options="viewSelectOptions" option-label="label" option-value="value" size="small" class="min-w-0 flex-1 sm:!hidden" />
            <Select
              :model-value="sortKey"
              :options="sortOptions"
              option-label="label"
              option-value="value"
              size="small"
              class="w-36 lg:!hidden"
              @update:model-value="chooseSort($event)"
            />
            <IconField class="w-full sm:ml-auto sm:w-64">
              <InputIcon>
                <Icon name="material-symbols:search-rounded" />
              </InputIcon>
              <InputText v-model="search" type="text" placeholder="Search titles..." class="w-full" size="small" />
              <InputIcon v-if="search" class="cursor-pointer" @click="search = ''">
                <Icon name="material-symbols:close" />
              </InputIcon>
            </IconField>
          </div>

          <!-- Matched rows -->
          <div class="rounded border border-surface-200 dark:border-surface-700">
            <div
              class="import-row h-9 border-b border-surface-200 text-xs font-semibold uppercase tracking-wider text-gray-500 dark:border-surface-700 dark:text-gray-400"
              :class="{ 'with-date': showDateColumn }"
            >
              <span />
              <button
                type="button"
                class="sort-header flex min-w-0 items-center gap-1"
                :class="{ 'sort-header-active': sortKey === 'title' }"
                :aria-label="sortLabel('title', 'Title')"
                @click="setSort('title')"
              >
                <span>Title</span>
                <Icon v-if="sortKey === 'title'" :name="sortIcon" />
              </button>
              <button
                type="button"
                class="sort-header hidden min-w-0 items-center gap-1 sm:flex"
                :class="{ 'sort-header-active': sortKey === 'type' }"
                :aria-label="sortLabel('type', 'Type')"
                @click="setSort('type')"
              >
                <span class="truncate">Type</span>
                <Icon v-if="sortKey === 'type'" :name="sortIcon" />
              </button>
              <button
                type="button"
                class="sort-header hidden min-w-0 items-center gap-1 sm:flex"
                :class="{ 'sort-header-active': sortKey === 'source' }"
                :aria-label="sortLabel('source', sourceColumnLabel)"
                @click="setSort('source')"
              >
                <span class="truncate">{{ sourceColumnLabel }}</span>
                <Icon v-if="sortKey === 'source'" :name="sortIcon" />
              </button>
              <span class="hidden sm:block" />
              <span class="hidden sm:block">Jiten</span>
              <span class="hidden lg:block">Current</span>
              <button
                v-if="showDateColumn"
                type="button"
                class="sort-header hidden min-w-0 items-center gap-1 lg:flex"
                :class="{ 'sort-header-active': sortKey === 'finished' }"
                :aria-label="sortLabel('finished', 'Date')"
                @click="setSort('finished')"
              >
                <span class="truncate">Date</span>
                <Icon v-if="sortKey === 'finished'" :name="sortIcon" />
              </button>
              <button
                type="button"
                class="sort-header flex min-w-0 items-center justify-end gap-1"
                :class="{ 'sort-header-active': sortKey === 'action' }"
                :aria-label="sortLabel('action', 'Action')"
                @click="setSort('action')"
              >
                <span class="truncate">Action</span>
                <Icon v-if="sortKey === 'action'" :name="sortIcon" />
              </button>
            </div>

            <VirtualScroller v-if="visibleRows.length > 0" :items="visibleRows" :item-size="56" scroll-height="30rem" class="w-full">
              <template #item="{ item }">
                <div
                  :key="item.deckId"
                  class="import-row h-14 border-b border-surface-100 dark:border-surface-800"
                  :class="{ 'bg-amber-50 dark:bg-amber-900/10': isConflict(item), 'with-date': showDateColumn }"
                >
                  <Checkbox :model-value="isRowIncluded(item)" :binary="true" :disabled="item.isIgnored" @update:model-value="toggleRow(item)" />
                  <div class="flex min-w-0 items-center gap-2.5">
                    <img
                      :src="coverUrl(item.coverName)"
                      alt=""
                      class="h-10 w-7 flex-none rounded-xs bg-surface-200 object-cover dark:bg-surface-700"
                      loading="lazy"
                      @error="onCoverError"
                    />
                    <div class="min-w-0">
                      <div class="flex min-w-0 items-center gap-1">
                        <NuxtLink :to="`/decks/media/${item.deckId}/detail`" target="_blank" class="truncate font-medium">
                          {{ rowTitle(item) }}
                        </NuxtLink>
                        <i v-if="item.isFavourite" class="pi pi-star-fill flex-none text-[10px] text-amber-500" title="Favourite in the file" />
                      </div>
                      <div
                        v-if="showAlternateTitles(item) || showUnits(item)"
                        class="hidden items-center gap-1.5 text-xs text-gray-400 sm:flex dark:text-gray-500"
                      >
                        <span
                          v-if="showUnits(item)"
                          :title="`${progressUnits(item)} of ${item.subdeckCount} subdecks will be marked as completed`"
                          class="flex-none rounded-sm bg-surface-100 px-1 font-semibold tabular-nums text-surface-600 dark:bg-surface-700/60 dark:text-surface-300"
                          >{{ progressUnits(item) }} / {{ item.subdeckCount }}</span
                        >
                        <span v-if="showAlternateTitles(item)" class="min-w-0 truncate">
                          <template v-for="(t, i) in rowAlternateTitles(item)" :key="i">
                            <span v-if="i > 0" class="mx-1">·</span>
                            <span :lang="t.ja ? 'ja' : undefined">{{ t.text }}</span>
                          </template>
                        </span>
                      </div>
                      <div class="flex items-center gap-1 text-xs sm:hidden">
                        <span
                          v-if="showUnits(item)"
                          class="flex-none rounded-sm bg-surface-100 px-1 font-semibold tabular-nums text-surface-600 dark:bg-surface-700/60 dark:text-surface-300"
                          >{{ progressUnits(item) }} / {{ item.subdeckCount }}</span
                        >
                        <template v-if="externalDiffers(item)">
                          <span class="truncate rounded-full px-1.5 py-px font-semibold" :class="externalChipClass">{{ externalStatusDisplay(item) }}</span>
                          <span class="flex-none text-gray-400">→</span>
                        </template>
                        <span class="flex-none rounded-full px-1.5 py-px font-semibold" :class="statusChipClass[item.mappedStatus]">{{
                          getDeckStatusText(item.mappedStatus)
                        }}</span>
                        <span
                          v-if="isConflict(item)"
                          class="min-w-0 truncate text-gray-500 dark:text-gray-400"
                          :title="`Currently ${getDeckStatusText(item.currentStatus!)} on Jiten`"
                          >· now {{ getDeckStatusText(item.currentStatus!) }}</span
                        >
                      </div>
                    </div>
                  </div>
                  <span class="hidden truncate text-sm sm:block">{{ getMediaTypeText(item.mediaType) }}</span>
                  <span class="hidden sm:block">
                    <span class="inline-block max-w-full truncate rounded-full px-2 py-0.5 text-xs font-semibold" :class="externalChipClass">{{
                      externalStatusDisplay(item)
                    }}</span>
                  </span>
                  <span class="hidden text-center text-gray-400 sm:block">→</span>
                  <span class="hidden sm:block">
                    <span class="inline-block max-w-full truncate rounded-full px-2 py-0.5 text-xs font-semibold" :class="statusChipClass[item.mappedStatus]">{{
                      getDeckStatusText(item.mappedStatus)
                    }}</span>
                  </span>
                  <span class="hidden lg:block">
                    <span
                      v-if="item.currentStatus != null"
                      class="inline-block max-w-full truncate rounded-full px-2 py-0.5 text-xs font-semibold"
                      :class="statusChipClass[item.currentStatus]"
                      >{{ getDeckStatusText(item.currentStatus) }}</span
                    >
                    <span v-else class="text-gray-400 dark:text-gray-500">—</span>
                  </span>
                  <span v-if="showDateColumn" class="hidden truncate text-xs text-gray-500 lg:block dark:text-gray-400">{{
                    formatFinished(item.finishedAt) || '—'
                  }}</span>
                  <div class="min-w-0 justify-self-end text-right">
                    <span v-if="actionKind(item) === 'ignored'" class="text-xs text-gray-400 dark:text-gray-500">Ignored</span>
                    <span
                      v-else-if="actionKind(item) === 'excluded'"
                      class="inline-block max-w-full truncate rounded-full bg-surface-100 px-2 py-0.5 text-xs text-surface-500 dark:bg-surface-700/60 dark:text-surface-300"
                      :title="exclusionReason(item)"
                      >{{ exclusionReason(item) }}</span
                    >
                    <template v-else-if="actionKind(item) === 'conflict'">
                      <SelectButton
                        class="!hidden lg:!inline-flex"
                        :model-value="rowResolution(item)"
                        :options="[
                          { label: 'Keep', value: 'keep' },
                          { label: 'Overwrite', value: 'overwrite' },
                        ]"
                        option-label="label"
                        option-value="value"
                        size="small"
                        :allow-empty="false"
                        @update:model-value="rowResolutions[item.deckId] = $event"
                      />
                      <Button
                        class="lg:!hidden"
                        severity="secondary"
                        size="small"
                        :label="rowResolution(item) === 'keep' ? 'Keep' : 'Overwrite'"
                        @click="rowResolutions[item.deckId] = rowResolution(item) === 'keep' ? 'overwrite' : 'keep'"
                      />
                    </template>
                    <span
                      v-else-if="actionKind(item) === 'favourite'"
                      class="inline-block max-w-full truncate rounded-full bg-amber-100 px-2 py-0.5 text-xs font-semibold text-amber-700 dark:bg-amber-900/40 dark:text-amber-300"
                      >Favourite</span
                    >
                    <span v-else-if="actionKind(item) === 'unchanged'" class="text-xs text-gray-400 dark:text-gray-500">Unchanged</span>
                    <span v-else class="text-xs font-semibold text-green-600 sm:hidden dark:text-green-400">New</span>
                  </div>
                </div>
              </template>
            </VirtualScroller>

            <div v-else class="p-6 text-center text-sm text-gray-500 dark:text-gray-400">Nothing here.</div>
          </div>

          <!-- Unmatched -->
          <div v-if="preview.unmatched.length > 0" class="rounded border border-surface-200 dark:border-surface-700">
            <button type="button" class="flex w-full items-center gap-2 p-3 text-left text-sm font-semibold" @click="unmatchedOpen = !unmatchedOpen">
              <i :class="unmatchedOpen ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" class="text-xs text-gray-400" />
              Not on Jiten
              <span class="font-normal tabular-nums text-gray-500 dark:text-gray-400">{{ preview.unmatched.length }}</span>
              <NuxtLink to="/requests" class="ml-auto text-xs font-normal" @click.stop>Request media</NuxtLink>
            </button>
            <ul v-if="unmatchedOpen" class="max-h-64 overflow-y-auto border-t border-surface-200 p-3 text-sm dark:border-surface-700">
              <li v-for="(entry, index) in preview.unmatched" :key="entry.url ?? `${entry.title}-${index}`" class="flex items-center gap-2 py-0.5">
                <a v-if="entry.url" :href="entry.url" target="_blank" rel="noopener nofollow" class="truncate">{{ entry.title }}</a>
                <span v-else class="truncate">{{ entry.title }}</span>
                <span class="text-xs whitespace-nowrap text-gray-400 dark:text-gray-500">{{ entry.externalStatus }}</span>
              </li>
            </ul>
          </div>

          <!-- Apply bar -->
          <div class="flex flex-wrap items-center gap-3">
            <div class="text-sm">
              <b class="tabular-nums">{{ newCount }}</b> new · <b class="tabular-nums">{{ updateCount }}</b> updates
              <span v-if="ignoredRows.length > 0" class="text-gray-500 dark:text-gray-400"> · {{ ignoredRows.length }} skipped (ignored)</span>
            </div>
            <Button
              :label="`Import ${applyRows.length} titles`"
              icon="pi pi-check"
              class="ml-auto"
              :disabled="applyRows.length === 0 || isLoading"
              @click="applyImport"
            />
          </div>
        </div>
      </div>
    </template>
  </Card>

  <LoadingOverlay :visible="isLoading">
    <p>{{ loadingMessage }}</p>
  </LoadingOverlay>
</template>

<style scoped>
  .import-row {
    display: grid;
    align-items: center;
    column-gap: 0.5rem;
    padding: 0 0.5rem;
    /* fit-content keeps short actions ("New") from reserving the full Keep/Overwrite width. */
    grid-template-columns: 1.75rem minmax(0, 1fr) fit-content(6rem);
  }

  @media (min-width: 640px) {
    .import-row {
      grid-template-columns: 1.75rem minmax(0, 1fr) 5rem 5.5rem 1rem 5.5rem 6rem;
    }
  }

  @media (min-width: 1024px) {
    .import-row {
      grid-template-columns: 1.75rem minmax(0, 1fr) 5.5rem 6.5rem 1rem 6.5rem 6.5rem 9rem;
    }

    .import-row.with-date {
      grid-template-columns: 1.75rem minmax(0, 1fr) 5.5rem 6.5rem 1rem 6.5rem 6.5rem 5rem 9rem;
    }
  }

  .sort-header {
    cursor: pointer;
    font: inherit;
    letter-spacing: inherit;
    text-transform: inherit;
    color: inherit;
  }

  .sort-header:hover {
    color: var(--p-primary-color);
  }

  .sort-header-active {
    color: var(--p-primary-color);
  }
</style>
