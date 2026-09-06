<script setup lang="ts">
  import { ref, computed, onMounted } from 'vue';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import InputText from 'primevue/inputtext';
  import InputNumber from 'primevue/inputnumber';
  import Message from 'primevue/message';
  import Tag from 'primevue/tag';
  import ToggleSwitch from 'primevue/toggleswitch';
  import Select from 'primevue/select';
  import Fieldset from 'primevue/fieldset';
  import { useToast } from 'primevue/usetoast';
  import { useConfirm } from 'primevue/useconfirm';

  const props = defineProps<{
    deckId: number;
  }>();

  interface YouTubeLedgerVideo {
    videoId: string;
    childDeckId: number | null;
    status: string;
    title: string;
    uploadedAt: string | null;
    runtimeSeconds: number | null;
    playableInEmbed: boolean;
    skipReason: string | null;
    lastCheckedAt: string;
  }

  interface YouTubeSource {
    deckId: number;
    sourceKind: string;
    sourceId: string;
    channelName: string;
    channelId: string | null;
    url: string;
    titleFilterInclude: string | null;
    titleFilterExclude: string | null;
    minRuntimeSeconds: number | null;
    maxRuntimeSeconds: number | null;
    lastSourceUpdate: string | null;
    lastSyncedAt: string | null;
    nextCheckAt: string;
    syncEnabled: boolean;
    checkIntervalDays: number | null;
    consecutiveFailures: number;
    lastError: string | null;
    serverFetch: boolean;
    statusCounts: Record<string, number>;
    reasonCounts: { prefix: string; count: number }[];
    videos: YouTubeLedgerVideo[];
  }

  const STATUS_ORDER = ['Imported', 'Fetched', 'Pending', 'NoManualSubs', 'FilteredOut', 'Excluded', 'Dead'];
  const STATUS_SEVERITY: Record<string, string> = {
    Imported: 'success',
    Fetched: 'info',
    Pending: 'warn',
    NoManualSubs: 'secondary',
    FilteredOut: 'secondary',
    Excluded: 'contrast',
    Dead: 'danger',
  };

  const toast = useToast();
  const confirm = useConfirm();
  const { $api } = useNuxtApp();

  const source = ref<YouTubeSource | null>(null);
  const statusFilter = ref<string | null>(null);
  const busy = ref<string | null>(null);
  const filterInclude = ref('');
  const checkInterval = ref<number | null>(null);
  const intervalDirty = computed(() => !!source.value && checkInterval.value !== source.value.checkIntervalDays);
  const filterExclude = ref('');
  const minMinutes = ref<number | null>(null);
  const maxMinutes = ref<number | null>(null);
  const toMinutes = (seconds: number | null) => (seconds ? Math.round(seconds / 60) : null);
  const filtersDirty = computed(
    () =>
      !!source.value &&
      (filterInclude.value !== (source.value.titleFilterInclude ?? '') ||
        filterExclude.value !== (source.value.titleFilterExclude ?? '') ||
        minMinutes.value !== toMinutes(source.value.minRuntimeSeconds) ||
        maxMinutes.value !== toMinutes(source.value.maxRuntimeSeconds))
  );

  const statusOptions = computed(() => [
    { label: 'All statuses', value: null },
    ...STATUS_ORDER.filter((s) => (source.value?.statusCounts[s] ?? 0) > 0).map((s) => ({ label: `${s} (${source.value!.statusCounts[s]})`, value: s })),
  ]);

  const load = async () => {
    try {
      source.value = await $api<YouTubeSource>(`admin/youtube/${props.deckId}`, {
        query: { status: statusFilter.value ?? undefined, limit: 300 },
      });
      filterInclude.value = source.value.titleFilterInclude ?? '';
      checkInterval.value = source.value.checkIntervalDays;
      filterExclude.value = source.value.titleFilterExclude ?? '';
      minMinutes.value = toMinutes(source.value.minRuntimeSeconds);
      maxMinutes.value = toMinutes(source.value.maxRuntimeSeconds);
    } catch {
      // Not a tracked source: the panel stays hidden
      source.value = null;
    }
  };

  const run = async (key: string, path: string, body: unknown, successSummary: string, successDetail: string) => {
    busy.value = key;
    try {
      await $api(`admin/youtube/${props.deckId}/${path}`, { method: 'POST', body });
      toast.add({ severity: 'success', summary: successSummary, detail: successDetail, life: 4000 });
      await load();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'The request failed.'), life: 4000 });
    } finally {
      busy.value = null;
    }
  };

  const syncNow = () => run('sync', 'sync', undefined, 'Feed check queued', 'New uploads are added to the queue.');
  const importNow = () => run('import', 'import', undefined, 'Parse queued', 'Fetched subdecks are parsed.');
  const reorder = () => run('reorder', 'reorder', undefined, 'Reordered', 'Videos are numbered by upload date.');

  // Both call yt-dlp from the server's IP: one request per video, and a flagged IP stays flagged
  const drainNow = () =>
    confirm.require({
      header: 'Fetch from the server?',
      message: `Runs yt-dlp on the server for up to ${Math.min(source.value?.statusCounts.Pending ?? 0, 50)} queued videos. On a bot-checked IP this fails and the videos stay queued.`,
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Fetch',
      accept: () => run('drain', 'drain', undefined, 'Fetch queued', 'Queued videos are fetched from the server.'),
    });
  const bootstrap = () =>
    confirm.require({
      header: 'List every video?',
      message: 'Runs one yt-dlp listing on the server to find videos the feed never showed. Unseen videos are queued; nothing is fetched yet.',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'List videos',
      accept: () => run('bootstrap', 'bootstrap', undefined, 'Listing queued', 'Unseen videos are added to the queue.'),
    });
  const settingsDirty = computed(() => filtersDirty.value || intervalDirty.value);

  const saveSettings = async () => {
    busy.value = 'settings';
    try {
      if (filtersDirty.value) {
        await $api(`admin/youtube/${props.deckId}/filters`, {
          method: 'POST',
          body: {
            titleInclude: filterInclude.value || null,
            titleExclude: filterExclude.value || null,
            minRuntimeSeconds: minMinutes.value ? minMinutes.value * 60 : null,
            maxRuntimeSeconds: maxMinutes.value ? maxMinutes.value * 60 : null,
          },
        });
      }
      if (intervalDirty.value) {
        await $api(`admin/youtube/${props.deckId}/check-interval`, { method: 'POST', body: { days: checkInterval.value } });
      }
      toast.add({ severity: 'success', summary: 'Settings saved', detail: 'Filters apply to the next fetch.', life: 4000 });
      await load();
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not save the settings.'), life: 4000 });
    } finally {
      busy.value = null;
    }
  };
  const recheckPrefix = (prefix: string) => run(`recheck-${prefix}`, 'recheck', { prefix }, 'Re-check queued', `Every "${prefix}" video is pending again.`);
  const setVideoStatus = (videoId: string, status: 'Pending' | 'Excluded') =>
    run(`video-${videoId}`, `videos/${videoId}/status`, { status }, status === 'Pending' ? 'Re-check queued' : 'Excluded', videoId);

  const toggleSync = async (enabled: boolean) => {
    try {
      await $api(`admin/youtube/${props.deckId}/sync-enabled`, { method: 'POST', body: { enabled } });
    } catch (error) {
      if (source.value) source.value.syncEnabled = !enabled;
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not change the sync setting.'), life: 4000 });
    }
  };

  const drainCommand = computed(() => `dotnet run --project Jiten.Cli -- --yt-drain ${props.deckId}`);
  const copyCommand = async () => {
    try {
      await navigator.clipboard.writeText(drainCommand.value);
      toast.add({ severity: 'success', summary: 'Copied', life: 2000 });
    } catch {
      toast.add({ severity: 'warn', summary: 'Copy failed', detail: 'Select the command and copy it by hand.', life: 4000 });
    }
  };

  const formatDateTime = (value: string | null) => (value ? new Date(value).toLocaleString() : '—');
  const formatDate = (value: string | null) => (value ? new Date(value).toLocaleDateString() : '—');
  const formatRuntime = (seconds: number | null) => {
    if (!seconds) return '—';
    const m = Math.floor(seconds / 60);
    const s = seconds % 60;
    return m >= 60 ? `${Math.floor(m / 60)}h ${m % 60}m` : `${m}:${s.toString().padStart(2, '0')}`;
  };

  onMounted(load);
</script>

<template>
  <Card v-if="source" class="mb-6">
    <template #title>
      <div class="flex flex-wrap items-center gap-2">
        <span>YouTube Sync</span>
        <Tag :value="source.sourceKind" severity="info" />
        <Tag v-if="!source.serverFetch" value="Home CLI fetches" severity="secondary" />
        <div class="ml-auto flex items-center gap-2 text-sm font-normal">
          <ToggleSwitch v-model="source.syncEnabled" input-id="ytSyncEnabled" @update:model-value="toggleSync" />
          <label for="ytSyncEnabled">Automatic sync</label>
        </div>
      </div>
    </template>

    <template #content>
      <Message v-if="source.consecutiveFailures > 0" severity="error" :closable="false" class="mb-4">
        Failed {{ source.consecutiveFailures }} time{{ source.consecutiveFailures === 1 ? '' : 's' }} in a row.
        <span v-if="source.lastError" class="block text-xs mt-1 break-words">{{ source.lastError }}</span>
      </Message>

      <Message v-if="(source.statusCounts.Pending ?? 0) > 0 && !source.serverFetch" severity="warn" :closable="false" class="mb-4">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
          <span>{{ source.statusCounts.Pending }} video{{ source.statusCounts.Pending === 1 ? '' : 's' }} waiting for a fetch from the home CLI.</span>
          <code class="text-xs px-2 py-1 rounded bg-surface-0 dark:bg-surface-900">{{ drainCommand }}</code>
          <Button icon="pi pi-copy" size="small" severity="secondary" text rounded aria-label="Copy command" @click="copyCommand" />
        </div>
      </Message>

      <div class="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm mb-4">
        <div>
          <div class="text-surface-500 dark:text-surface-400">Source</div>
          <a :href="source.url" target="_blank" rel="noopener" class="font-medium hover:underline break-all">
            {{ source.channelName || source.sourceId }}
            <i class="pi pi-external-link text-xs" />
          </a>
        </div>
        <div>
          <div class="text-surface-500 dark:text-surface-400">Latest upload seen</div>
          <div class="font-medium">{{ formatDate(source.lastSourceUpdate) }}</div>
        </div>
        <div>
          <div class="text-surface-500 dark:text-surface-400">Last synced</div>
          <div class="font-medium">{{ formatDateTime(source.lastSyncedAt) }}</div>
        </div>
        <div>
          <div class="text-surface-500 dark:text-surface-400">Next feed check</div>
          <div class="font-medium">{{ formatDateTime(source.nextCheckAt) }}</div>
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-2 mb-5">
        <Tag
          v-for="status in STATUS_ORDER.filter((s) => (source!.statusCounts[s] ?? 0) > 0)"
          :key="status"
          :value="`${status} ${source.statusCounts[status]}`"
          :severity="STATUS_SEVERITY[status]"
        />
        <div class="ml-auto flex items-center gap-1">
          <Tooltip content="Reads the public feed for new uploads. No yt-dlp.">
            <Button label="Check feed" icon="pi pi-refresh" size="small" :loading="busy === 'sync'" @click="syncNow" />
          </Tooltip>
          <Tooltip content="Runs yt-dlp from the server IP, one call per queued video">
            <Button label="Fetch on server" icon="pi pi-download" size="small" severity="secondary" text :loading="busy === 'drain'" @click="drainNow" />
          </Tooltip>
          <Tooltip v-if="(source.statusCounts.Fetched ?? 0) > 0" content="Parses fetched subdecks now instead of waiting for the hourly pass">
            <Button label="Parse fetched" icon="pi pi-play" size="small" severity="secondary" text :loading="busy === 'import'" @click="importNow" />
          </Tooltip>
          <Tooltip content="Renumber the videos by upload date">
            <Button label="Reorder by date" icon="pi pi-sort-numeric-down" size="small" severity="secondary" text :loading="busy === 'reorder'" @click="reorder" />
          </Tooltip>
          <Tooltip content="One yt-dlp listing of the whole channel; the feed only shows the latest 15">
            <Button label="List all videos" icon="pi pi-list" size="small" severity="secondary" text :loading="busy === 'bootstrap'" @click="bootstrap" />
          </Tooltip>
        </div>
      </div>

      <Fieldset legend="Settings" toggleable collapsed class="mb-5" :pt="{ content: { class: 'pt-2' } }">
        <div class="grid grid-cols-1 md:grid-cols-2 gap-x-6 gap-y-4">
          <div>
            <label for="ytInclude" class="block text-sm mb-1">Only titles matching</label>
            <InputText id="ytInclude" v-model="filterInclude" size="small" fluid placeholder="regex, empty = all" />
          </div>
          <div>
            <label for="ytExclude" class="block text-sm mb-1">Skip titles matching</label>
            <InputText id="ytExclude" v-model="filterExclude" size="small" fluid placeholder="regex" />
          </div>
          <div>
            <label for="ytMinMinutes" class="block text-sm mb-1">Video length</label>
            <div class="flex items-center gap-2">
              <InputNumber v-model="minMinutes" input-id="ytMinMinutes" :min="0" suffix=" min" size="small" placeholder="no minimum" fluid class="flex-1" />
              <span class="text-surface-500 dark:text-surface-400 text-sm">to</span>
              <InputNumber v-model="maxMinutes" input-id="ytMaxMinutes" :min="0" suffix=" min" size="small" placeholder="no maximum" fluid class="flex-1" />
            </div>
            <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">Skips videos outside the range before anything is fetched.</p>
          </div>
          <div>
            <label for="ytInterval" class="block text-sm mb-1">Check the feed every</label>
            <InputNumber v-model="checkInterval" input-id="ytInterval" :min="1" :max="365" suffix=" days" size="small" placeholder="auto" fluid show-buttons />
            <p class="text-xs text-surface-500 dark:text-surface-400 mt-1">Auto = weekly, monthly once the channel has been quiet for three months.</p>
          </div>
        </div>
        <div class="flex justify-end mt-4">
          <Button label="Save settings" size="small" :disabled="!settingsDirty" :loading="busy === 'settings'" @click="saveSettings" />
        </div>
      </Fieldset>

      <div v-if="source.reasonCounts.length" class="flex flex-wrap items-center gap-2 mb-3 text-sm">
        <span class="text-surface-500 dark:text-surface-400">Skipped because:</span>
        <span v-for="reason in source.reasonCounts" :key="reason.prefix" class="inline-flex items-center gap-1">
          <span class="font-mono text-xs px-1.5 py-0.5 rounded bg-surface-100 dark:bg-surface-800">{{ reason.prefix }}</span>
          <span class="tabular-nums">{{ reason.count }}</span>
          <Tooltip :content="`Re-check every ${reason.prefix} video`">
            <Button
              icon="pi pi-replay"
              size="small"
              severity="secondary"
              text
              rounded
              :aria-label="`Re-check ${reason.prefix}`"
              :loading="busy === `recheck-${reason.prefix}`"
              @click="recheckPrefix(reason.prefix)"
            />
          </Tooltip>
        </span>
      </div>

      <div class="flex items-center gap-2 mb-2">
        <Select v-model="statusFilter" :options="statusOptions" option-label="label" option-value="value" size="small" class="w-56" @change="load" />
        <span class="text-xs text-surface-500 dark:text-surface-400">{{ source.videos.length }} shown</span>
      </div>

      <div class="rounded-lg border border-surface-200 dark:border-surface-700 overflow-x-auto max-h-[32rem] overflow-y-auto">
        <table class="w-full text-sm">
          <thead class="bg-surface-50 dark:bg-surface-800 text-left text-xs text-surface-500 dark:text-surface-400 sticky top-0">
            <tr>
              <th class="px-3 py-2 font-medium">Video</th>
              <th class="px-3 py-2 font-medium">Uploaded</th>
              <th class="px-3 py-2 font-medium">Length</th>
              <th class="px-3 py-2 font-medium">Status</th>
              <th class="px-3 py-2 font-medium">Reason</th>
              <th class="px-3 py-2 font-medium" />
            </tr>
          </thead>
          <tbody>
            <tr v-for="video in source.videos" :key="video.videoId" class="border-t border-surface-100 dark:border-surface-800">
              <td class="px-3 py-1.5 max-w-[24rem]">
                <a :href="`https://www.youtube.com/watch?v=${video.videoId}`" target="_blank" rel="noopener" class="hover:underline line-clamp-1">
                  {{ video.title }}
                </a>
                <NuxtLink v-if="video.childDeckId" :to="`/dashboard/media/${video.childDeckId}`" class="text-xs text-primary-500 hover:underline">
                  deck {{ video.childDeckId }}
                </NuxtLink>
              </td>
              <td class="px-3 py-1.5 whitespace-nowrap tabular-nums">{{ formatDate(video.uploadedAt) }}</td>
              <td class="px-3 py-1.5 whitespace-nowrap tabular-nums">{{ formatRuntime(video.runtimeSeconds) }}</td>
              <td class="px-3 py-1.5"><Tag :value="video.status" :severity="STATUS_SEVERITY[video.status]" class="text-xs" /></td>
              <td class="px-3 py-1.5 text-xs text-surface-500 dark:text-surface-400 max-w-[16rem] truncate" :title="video.skipReason ?? ''">
                {{ video.skipReason ?? '' }}
              </td>
              <td class="px-3 py-1.5 whitespace-nowrap text-right">
                <template v-if="!video.childDeckId">
                  <Tooltip v-if="video.status !== 'Pending'" content="Fetch again on the next drain">
                    <Button
                      icon="pi pi-replay"
                      size="small"
                      severity="secondary"
                      text
                      rounded
                      aria-label="Re-check"
                      :loading="busy === `video-${video.videoId}`"
                      @click="setVideoStatus(video.videoId, 'Pending')"
                    />
                  </Tooltip>
                  <Tooltip v-if="video.status !== 'Excluded'" content="Never import this video">
                    <Button
                      icon="pi pi-ban"
                      size="small"
                      severity="danger"
                      text
                      rounded
                      aria-label="Exclude"
                      :loading="busy === `video-${video.videoId}`"
                      @click="setVideoStatus(video.videoId, 'Excluded')"
                    />
                  </Tooltip>
                </template>
              </td>
            </tr>
            <tr v-if="source.videos.length === 0">
              <td colspan="6" class="px-3 py-4 text-center text-surface-500 dark:text-surface-400">No videos in this state.</td>
            </tr>
          </tbody>
        </table>
      </div>
    </template>
  </Card>
</template>
