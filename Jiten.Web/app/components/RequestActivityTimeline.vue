<script setup lang="ts">
  import type { RequestActivityLogDto } from '~/types/types';
  import { getRequestActionText, getRequestActionIcon, getRequestActionSeverity } from '~/utils/requestActionMapper';

  const props = defineProps<{
    requestId: number;
  }>();

  const { fetchActivityLog } = useMediaRequests();

  const activityLog = ref<RequestActivityLogDto[]>([]);
  const isLoading = ref(false);
  const loaded = ref(false);

  async function load() {
    if (loaded.value) return;
    isLoading.value = true;
    activityLog.value = await fetchActivityLog(props.requestId);
    loaded.value = true;
    isLoading.value = false;
  }

  function formatTimeAgo(dateString: string): string {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now.getTime() - date.getTime();
    const diffMins = Math.floor(diffMs / 60000);
    if (diffMins < 1) return 'just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    const diffDays = Math.floor(diffHours / 24);
    if (diffDays < 30) return `${diffDays}d ago`;
    return date.toLocaleDateString();
  }

  defineExpose({ load });
</script>

<template>
  <div class="border-t border-surface-200 dark:border-surface-700 pt-3 mt-2">
    <div class="flex items-center justify-between mb-2">
      <h3 class="font-semibold text-sm">Activity Log</h3>
      <Button v-if="!loaded" label="Load" icon="pi pi-history" text size="small" :loading="isLoading" @click="load" />
    </div>

    <ProgressSpinner v-if="isLoading" class="w-6 h-6" />

    <div v-else-if="loaded && activityLog.length === 0" class="text-muted-color text-sm">No activity recorded.</div>

    <div v-else-if="loaded" class="flex flex-col text-sm">
      <div v-for="(item, index) in activityLog" :key="item.id" class="flex gap-2">
        <div class="flex flex-col items-center">
          <div class="w-5 h-5 flex items-center justify-center shrink-0">
            <i :class="getRequestActionIcon(item.action)" class="text-xs text-muted-color" />
          </div>
          <div v-if="index < activityLog.length - 1" class="w-px flex-1 bg-surface-200 dark:bg-surface-700 my-0.5" />
        </div>
        <div class="flex flex-col gap-0.5 pb-3 min-w-0">
          <div class="flex items-center gap-2 flex-wrap">
            <Tag :value="getRequestActionText(item.action)" :severity="getRequestActionSeverity(item.action)" class="text-xs" />
            <span class="text-muted-color text-xs font-mono" :title="item.userId">{{ item.userName || item.userId.slice(0, 8) }}</span>
            <span class="text-muted-color text-xs">{{ formatTimeAgo(item.createdAt) }}</span>
          </div>
          <span v-if="item.detail" class="text-muted-color text-xs truncate max-w-xs">{{ item.detail }}</span>
        </div>
      </div>
    </div>
  </div>
</template>
