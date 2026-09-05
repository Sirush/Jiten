<script setup lang="ts">
  import type { SiteUpdate } from '~/types';
  import { useJitenStore } from '~/stores/jitenStore';

  defineProps<{ compact?: boolean }>();

  const { $api } = useNuxtApp();
  const store = useJitenStore();

  const update = ref<SiteUpdate | null>(null);
  const loading = ref(true);

  onMounted(async () => {
    try {
      const response = await $api<{ data: SiteUpdate[] }>('updates', { query: { offset: 0, limit: 1 } });
      update.value = response?.data?.[0] ?? null;
    } catch {
      update.value = null;
    } finally {
      loading.value = false;
    }
  });

  const isUnread = computed(() => !!update.value && update.value.id > store.lastSeenUpdateId);

  const publishedLabel = computed(() => {
    if (!update.value) return '';
    return new Date(update.value.publishedAt).toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
  });

  function markSeen() {
    if (update.value) store.lastSeenUpdateId = update.value.id;
  }
</script>

<template>
  <div v-if="loading" class="flex items-start gap-3" :class="compact ? 'p-3' : 'p-4 rounded-xl border border-surface-200 dark:border-surface-700'">
    <Skeleton shape="circle" width="2rem" height="2rem" />
    <div class="flex-1 flex flex-col gap-2">
      <Skeleton width="5rem" height="0.7rem" />
      <Skeleton width="10rem" height="0.9rem" />
    </div>
  </div>

  <HomeStrip
    v-else-if="update"
    :compact="compact"
    label="What's new"
    icon="material-symbols-light:campaign"
    :to="`/updates#update-${update.id}`"
    :marked="isUnread"
    @click="markSeen"
  >
    {{ update.title }}
    <span class="text-surface-400 dark:text-surface-400">{{ publishedLabel }}</span>
  </HomeStrip>
</template>
