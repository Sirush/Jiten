<script setup lang="ts">
  import { ref, onMounted } from 'vue';
  import Card from 'primevue/card';
  import Button from 'primevue/button';
  import Message from 'primevue/message';
  import Tag from 'primevue/tag';
  import ToggleSwitch from 'primevue/toggleswitch';
  import { useToast } from 'primevue/usetoast';

  const props = defineProps<{
    deckId: number;
  }>();

  interface WebNovelSubdeck {
    childDeckId: number;
    startEpisode: number;
    endEpisode: number;
    episodeCount: number;
    charCount: number;
  }

  interface WebNovelSource {
    deckId: number;
    provider: string;
    sourceId: string;
    url: string;
    lastEpisodeCount: number;
    lastSourceUpdate: string | null;
    lastSyncedAt: string | null;
    nextCheckAt: string;
    syncEnabled: boolean;
    completedAtSource: boolean;
    onHiatusAtSource: boolean;
    consecutiveFailures: number;
    lastError: string | null;
    chunkCharBudget: number | null;
    pendingRevisionCount: number;
    subdecks: WebNovelSubdeck[];
  }

  const toast = useToast();
  const { $api } = useNuxtApp();

  const source = ref<WebNovelSource | null>(null);
  const syncing = ref(false);
  const rebuilding = ref<number | null>(null);

  const load = async () => {
    try {
      source.value = await $api<WebNovelSource>(`admin/webnovel/${props.deckId}`);
    } catch {
      // Not a tracked webnovel — the panel just stays hidden
      source.value = null;
    }
  };

  const syncNow = async () => {
    syncing.value = true;
    try {
      await $api(`admin/webnovel/${props.deckId}/sync`, { method: 'POST' });
      toast.add({ severity: 'success', summary: 'Sync queued', detail: 'New chapters will appear once fetching finishes.', life: 4000 });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not queue the sync.'), life: 4000 });
    } finally {
      syncing.value = false;
    }
  };

  const rebuild = async (childDeckId: number) => {
    rebuilding.value = childDeckId;
    try {
      await $api(`admin/webnovel/${props.deckId}/rebuild/${childDeckId}`, { method: 'POST' });
      toast.add({ severity: 'success', summary: 'Rebuild queued', detail: 'The subdeck will be re-fetched and reparsed.', life: 4000 });
    } catch (error) {
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not queue the rebuild.'), life: 4000 });
    } finally {
      rebuilding.value = null;
    }
  };

  const toggleSync = async (enabled: boolean) => {
    try {
      await $api(`admin/webnovel/${props.deckId}/sync-enabled`, { method: 'POST', body: { enabled } });
    } catch (error) {
      if (source.value) source.value.syncEnabled = !enabled;
      toast.add({ severity: 'error', summary: 'Error', detail: extractApiError(error, 'Could not change the sync setting.'), life: 4000 });
    }
  };

  const formatDateTime = (value: string | null) => (value ? new Date(value).toLocaleString() : '—');
  const formatNumber = (value: number) => value.toLocaleString('en-US');

  onMounted(load);
</script>

<template>
  <Card v-if="source" class="mb-6">
    <template #title>
      <div class="flex flex-wrap items-center gap-2">
        <span>Webnovel Sync</span>
        <Tag v-if="source.completedAtSource" value="Completed" severity="success" />
        <Tag v-else value="Ongoing" severity="info" />
        <Tag v-if="source.onHiatusAtSource" value="On hiatus" severity="warn" />
      </div>
    </template>

    <template #content>
      <Message v-if="source.consecutiveFailures > 0" severity="error" :closable="false" class="mb-4">
        Failed {{ source.consecutiveFailures }} time{{ source.consecutiveFailures === 1 ? '' : 's' }} in a row.
        <span v-if="source.lastError" class="block text-xs mt-1 break-words">{{ source.lastError }}</span>
      </Message>

      <Message v-if="source.pendingRevisionCount > 0" severity="warn" :closable="false" class="mb-4">
        {{ source.pendingRevisionCount }} episode{{ source.pendingRevisionCount === 1 ? '' : 's' }} revised at the source. Rebuild the
        subdeck holding them to pick the changes up.
      </Message>

      <div class="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm mb-4">
        <div>
          <div class="text-surface-500 dark:text-surface-400">Source</div>
          <a :href="source.url" target="_blank" rel="noopener" class="font-medium hover:underline">
            {{ source.sourceId }}
            <i class="pi pi-external-link text-xs" />
          </a>
        </div>
        <div>
          <div class="text-surface-500 dark:text-surface-400">Episodes</div>
          <div class="font-medium tabular-nums">{{ formatNumber(source.lastEpisodeCount) }}</div>
        </div>
        <div>
          <div class="text-surface-500 dark:text-surface-400">Last synced</div>
          <div class="font-medium">{{ formatDateTime(source.lastSyncedAt) }}</div>
        </div>
        <div>
          <div class="text-surface-500 dark:text-surface-400">Next check</div>
          <div class="font-medium">{{ formatDateTime(source.nextCheckAt) }}</div>
        </div>
      </div>

      <div class="flex flex-wrap items-center gap-4 mb-4">
        <Button label="Sync now" icon="pi pi-refresh" size="small" :loading="syncing" @click="syncNow" />
        <div class="flex items-center gap-2">
          <ToggleSwitch v-model="source.syncEnabled" input-id="syncEnabled" @update:model-value="toggleSync" />
          <label for="syncEnabled" class="text-sm">Automatic sync</label>
        </div>
      </div>

      <div class="rounded-lg border border-surface-200 dark:border-surface-700 overflow-hidden">
        <div
          v-for="subdeck in source.subdecks"
          :key="subdeck.childDeckId"
          class="flex flex-wrap items-center gap-2 px-3 py-2 border-b border-surface-100 dark:border-surface-800 last:border-b-0"
        >
          <span class="font-medium text-sm">第{{ subdeck.startEpisode }}話〜第{{ subdeck.endEpisode }}話</span>
          <span class="text-xs text-surface-500 dark:text-surface-400 tabular-nums">
            {{ subdeck.episodeCount }} episodes · {{ formatNumber(subdeck.charCount) }} chars
          </span>
          <div class="ml-auto flex items-center gap-1">
            <Button
              v-tooltip.top="'Re-fetch every episode in this range, picking up revisions (改稿)'"
              label="Rebuild"
              icon="pi pi-replay"
              size="small"
              severity="secondary"
              text
              :loading="rebuilding === subdeck.childDeckId"
              @click="rebuild(subdeck.childDeckId)"
            />
          </div>
        </div>
      </div>
    </template>
  </Card>
</template>
