<script setup lang="ts">
  import type { KnowledgeGrowth } from '~/types';
  import { useJitenStore } from '~/stores/jitenStore';

  const jitenStore = useJitenStore();
  const growth = ref<KnowledgeGrowth | null>(null);
  const loading = ref(true);
  const failed = ref(false);
  const rateLimited = ref(false);

  async function load() {
    loading.value = true;
    failed.value = false;
    rateLimited.value = false;
    try {
      const { $api } = useNuxtApp();
      growth.value = await $api<KnowledgeGrowth>('srs/knowledge-growth');
    } catch (err) {
      growth.value = null;
      failed.value = true;
      rateLimited.value = isRateLimited(err);
    } finally {
      loading.value = false;
    }
  }

  onMounted(load);

  const hasData = computed(() => growth.value?.hasEnoughHistory);
  const hasPrior = computed(() => !!growth.value && hasPriorKnowledge(growth.value.points));

  const baseline = computed(() => growth.value?.points[growth.value.points.length - 1]?.priorKnownWords ?? 0);
  const learnedHere = computed(() => (growth.value?.points[growth.value.points.length - 1]?.knownWordsCombined ?? 0) - baseline.value);
</script>

<template>
  <div v-if="!failed || rateLimited" class="rounded-xl border border-surface-200 dark:border-surface-700 bg-surface-0 dark:bg-surface-900 shadow-sm p-4">
    <div class="flex flex-wrap items-center justify-between gap-2 mb-1">
      <div class="font-semibold">Words learned over time</div>
      <KnowledgeGrowthShareButton v-if="hasData" :growth="growth!" />
    </div>
    <div class="text-xs text-gray-500 dark:text-gray-400 mb-3">
      <template v-if="hasData && hasPrior && jitenStore.separatePriorKnowledge">
        You started with {{ baseline.toLocaleString() }} words you marked known, and have learned {{ learnedHere.toLocaleString() }} since.
      </template>
      <template v-else>How many words you had learned over time.</template>
      <span v-if="hasData && growth!.recentGain !== 0" :class="growth!.recentGain > 0 ? 'text-green-600 dark:text-green-400 font-semibold' : 'font-semibold'">
        {{ growth!.recentGain > 0 ? '+' : '' }}{{ growth!.recentGain.toLocaleString() }} in the last 30 days.
      </span>
    </div>

    <div v-if="loading" class="h-[260px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />

    <div v-else-if="rateLimited" class="py-10 flex flex-col items-center gap-3 text-center">
      <span class="text-sm text-gray-500 dark:text-gray-400">Couldn't load this just now.</span>
      <Button label="Try again" icon="pi pi-refresh" severity="secondary" outlined size="small" @click="load()" />
    </div>

    <template v-else-if="hasData">
      <CoverageJourneyChart
        :points="growth!.points"
        :granularity="growth!.granularity"
        mode="count"
        height="260px"
        :separate-prior="jitenStore.separatePriorKnowledge"
      />
      <button
        v-if="hasPrior"
        class="mt-2 flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 transition-colors"
        @click="jitenStore.separatePriorKnowledge = !jitenStore.separatePriorKnowledge"
      >
        <i :class="jitenStore.separatePriorKnowledge ? 'pi pi-check-square' : 'pi pi-stop'" class="text-[11px]" />
        Show words I marked known in bulk as a starting point
      </button>
    </template>

    <div v-else class="py-10 text-center text-sm text-gray-400 dark:text-gray-400">Not enough history yet.</div>
  </div>
</template>
