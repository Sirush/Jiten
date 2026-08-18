<script setup lang="ts">
  import type { JourneyPoint } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';

  const props = defineProps<{
    deckId: number;
  }>();

  const auth = useAuthStore();
  const jitenStore = useJitenStore();
  const { journey, loading, failed, rateLimited, granted, statusReady, retry } = useCoverageJourney(() => props.deckId);

  const example = computed(() => buildExampleJourney());

  const trend = computed(() => (journey.value ? journeyWindow(journey.value.points, (p) => (p as JourneyPoint).coverage) : null));
  const deltaLabel = computed(() => (trend.value ? formatJourneyDelta(trend.value) : ''));
  const rangeStart = computed(() => {
    const j = journey.value;
    return j?.points.length ? formatBucketDated(j.points[0]!.date, j.granularity) : '';
  });

  const dismissed = computed(() => !granted.value && jitenStore.hideCoverageJourney);
  const showSection = computed(() => auth.isAuthenticated && !dismissed.value && (!failed.value || rateLimited.value));
  const showJourney = computed(() => granted.value && journey.value?.hasEnoughHistory);
</script>

<template>
  <Card v-if="showSection" class="mt-4">
    <template #content>
      <div class="flex items-center justify-between gap-2 mb-2">
        <h2 class="font-bold">Coverage over time</h2>
        <NuxtLink
          v-if="showJourney"
          :to="`/decks/media/${deckId}/stats#coverage-journey`"
          class="text-sm text-primary-600 dark:text-primary-400 hover:underline whitespace-nowrap"
        >
          See full journey
        </NuxtLink>
      </div>

      <div v-if="loading || !statusReady" class="h-[86px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />

      <div v-else-if="rateLimited" class="py-2 flex flex-wrap items-center justify-between gap-2">
        <span class="text-sm text-gray-500 dark:text-gray-400">Couldn't load your journey just now.</span>
        <Button label="Try again" icon="pi pi-refresh" severity="secondary" outlined size="small" @click="retry()" />
      </div>

      <div v-else-if="showJourney" class="flex flex-col sm:flex-row sm:items-center gap-3 sm:gap-5">
        <div class="shrink-0">
          <div class="flex items-baseline gap-1.5">
            <span class="text-2xl font-bold leading-none">{{ journey!.currentCoverage.toFixed(1) }}%</span>
            <span class="text-xs text-gray-500 dark:text-gray-400">coverage today</span>
          </div>
          <div
            v-if="deltaLabel"
            class="text-xs font-medium mt-1"
            :class="trend!.delta >= 0.05 ? 'text-primary-600 dark:text-primary-400' : 'text-gray-500 dark:text-gray-400'"
          >
            {{ deltaLabel }}
          </div>
        </div>
        <div class="flex-1 min-w-0">
          <CoverageJourneyChart
            :points="journey!.points"
            :granularity="journey!.granularity"
            compact
            height="64px"
            :separate-prior="jitenStore.separatePriorKnowledge"
          />
          <div class="flex justify-between text-[10px] text-gray-400 dark:text-gray-400 mt-0.5">
            <span>{{ rangeStart }}</span>
            <span>Today</span>
          </div>
        </div>
      </div>

      <div v-else-if="granted" class="py-2 text-sm text-gray-500 dark:text-gray-400">
        Not enough history yet. Your journey starts as you study words from this title.
      </div>

      <template v-else>
        <JitenPlusGate feature="coverage-journey" feature-label="Coverage journey">
          <div>
            <p class="text-sm text-gray-500 dark:text-gray-400 mb-2">See how your comprehension of this title grew over time. (example)</p>
            <CoverageJourneyChart :points="example.points" granularity="monthly" compact height="80px" />
          </div>
        </JitenPlusGate>
        <div class="flex justify-end pt-2">
          <Button label="Hide this" icon="pi pi-eye-slash" severity="secondary" text size="small" @click="jitenStore.hideCoverageJourney = true" />
        </div>
      </template>
    </template>
  </Card>
</template>
