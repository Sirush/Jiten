<script setup lang="ts">
  import type { KnowledgeGrowth } from '~/types';

  const { $api } = useNuxtApp();

  const isLoading = ref(true);
  const growth = ref<KnowledgeGrowth | null>(null);

  const load = async () => {
    isLoading.value = true;
    try {
      growth.value = await $api<KnowledgeGrowth>('srs/knowledge-growth');
    } catch {
      growth.value = null;
    } finally {
      isLoading.value = false;
    }
  };

  onMounted(() => load());

  const hasData = computed(() => Boolean(growth.value?.hasEnoughHistory) && (growth.value?.points.length ?? 0) > 1);

  const points = computed(() => growth.value?.points ?? []);
  // The whole known set, so this headline matches the words-known total on the vocabulary card; the
  // chart's solid line is the mature subset of it.
  const learned = computed(() => points.value[points.value.length - 1]?.knownWordsCombined ?? 0);
  const recentGain = computed(() => growth.value?.recentGain ?? 0);
  const startLabel = computed(() => (points.value.length ? formatBucketDated(points.value[0]!.date, growth.value!.granularity) : ''));
</script>

<template>
  <Card v-if="isLoading || hasData">
    <template #title>
      <h2 class="text-base font-semibold">Words learned over time</h2>
    </template>
    <template #content>
      <div v-if="isLoading" class="flex flex-col gap-3">
        <Skeleton width="12rem" height="2.25rem" />
        <Skeleton width="100%" height="7rem" />
      </div>

      <div v-else class="flex flex-col gap-3">
        <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
          <span class="text-2xl font-bold tabular-nums text-primary-600 dark:text-primary-400">
            {{ learned.toLocaleString() }}
          </span>
          <span class="text-sm text-muted-color">{{ learned === 1 ? 'word learned' : 'words learned' }}</span>
          <span
            v-if="recentGain !== 0"
            class="text-sm font-semibold"
            :class="recentGain > 0 ? 'text-green-600 dark:text-green-400' : 'text-muted-color'"
          >
            {{ recentGain > 0 ? '+' : '' }}{{ recentGain.toLocaleString() }} in the last 30 days
          </span>
        </div>

        <CoverageJourneyChart :points="points" :granularity="growth!.granularity" mode="count" compact height="140px" rgb="168, 85, 247" />

        <div class="flex justify-between text-xs text-muted-color">
          <span>{{ startLabel }}</span>
          <span>Today</span>
        </div>
      </div>
    </template>
  </Card>
</template>
