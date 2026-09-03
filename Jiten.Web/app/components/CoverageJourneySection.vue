<script setup lang="ts">
  import type { Deck, JourneyPoint } from '~/types';
  import { useAuthStore } from '~/stores/authStore';
  import { useJitenStore } from '~/stores/jitenStore';
  import type { CoverageScale } from '~/utils/coverageAxis';

  const props = defineProps<{
    deck: Deck;
    title: string;
  }>();

  const auth = useAuthStore();
  const jitenStore = useJitenStore();
  const { journey, loading, failed, rateLimited, granted, statusReady, retry } = useCoverageJourney(() => props.deck.deckId);

  const metric = ref<'total' | 'unique'>('total');
  const metricOptions: { key: 'total' | 'unique'; label: string }[] = [
    { key: 'total', label: 'Total' },
    { key: 'unique', label: 'Unique' },
  ];

  const scaleOptions: { key: CoverageScale; label: string }[] = [
    { key: 'fit', label: 'Fit' },
    { key: 'log', label: 'Log' },
    { key: 'full', label: 'Full' },
  ];

  const example = computed(() => buildExampleJourney());

  const currentValue = computed(() => (metric.value === 'unique' ? journey.value?.currentUniqueCoverage : journey.value?.currentCoverage));

  const pointValue = (point: JourneyPoint) => (metric.value === 'unique' ? point.uniqueCoverage : point.coverage);
  const trend = computed(() => (journey.value ? journeyWindow(journey.value.points, (p) => pointValue(p as JourneyPoint)) : null));
  const deltaLabel = computed(() => (trend.value ? formatJourneyDelta(trend.value) : ''));
  const rangeLabel = computed(() => {
    const j = journey.value;
    return j?.points.length ? `${formatBucketDated(j.points[0]!.date, j.granularity)} to today` : '';
  });

  const asOfLabel = computed(() => (journey.value?.asOf ? formatDateShort(journey.value.asOf) : ''));

  const hasPrior = computed(() => !!journey.value && hasPriorKnowledge(journey.value.points));

  const methodTooltip = 'Show your historical coverage of this media over time.';

  // Dismissal only applies while locked; the section returns the moment the tier does.
  const dismissed = computed(() => !granted.value && jitenStore.hideCoverageJourney);
  // A broken chart never belongs on a deck page, but a rate limit is worth a retry rather than silence.
  const showSection = computed(() => auth.isAuthenticated && !dismissed.value && (!failed.value || rateLimited.value));
  const showJourney = computed(() => granted.value && journey.value?.hasEnoughHistory);
  const showThin = computed(() => granted.value && journey.value && !journey.value.hasEnoughHistory);
</script>

<template>
  <Card v-if="showSection">
    <template #header>
      <div class="flex flex-wrap items-center justify-between gap-2 px-4 pt-4">
        <div class="flex items-center gap-2">
          <h2 class="text-xl font-bold">Coverage over time</h2>
          <Tooltip :content="methodTooltip">
            <i class="pi pi-info-circle text-gray-400 cursor-help" />
          </Tooltip>
        </div>
        <div v-if="showJourney" class="flex items-center gap-2">
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in metricOptions"
              :key="opt.key"
              class="px-2.5 py-1 rounded-md transition-colors"
              :class="
                metric === opt.key
                  ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                  : 'text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300'
              "
              @click="metric = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
          <div class="flex rounded-lg bg-surface-100 dark:bg-surface-800 p-0.5 text-xs">
            <button
              v-for="opt in scaleOptions"
              :key="opt.key"
              class="px-2.5 py-1 rounded-md transition-colors"
              :class="
                jitenStore.coverageJourneyScale === opt.key
                  ? 'bg-surface-0 dark:bg-surface-700 shadow-sm font-medium text-gray-800 dark:text-gray-100'
                  : 'text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-300'
              "
              @click="jitenStore.coverageJourneyScale = opt.key"
            >
              {{ opt.label }}
            </button>
          </div>
          <CoverageJourneyShareButton :deck="deck" :title="title" :journey="journey!" :metric="metric" :scale="jitenStore.coverageJourneyScale" />
        </div>
      </div>
    </template>
    <template #content>
      <!-- Waiting on the tier status too, so a subscriber never sees the locked state flash first. -->
      <div v-if="loading || !statusReady" class="h-[260px] rounded bg-surface-100 dark:bg-surface-800 animate-pulse" />

      <div v-else-if="rateLimited" class="py-8 flex flex-col items-center gap-3 text-center">
        <p class="text-gray-500 dark:text-gray-400">Couldn't load your journey just now. You've opened a lot of pages in a short time.</p>
        <Button label="Try again" icon="pi pi-refresh" severity="secondary" outlined size="small" @click="retry()" />
      </div>

      <template v-else-if="showJourney">
        <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1 mb-3">
          <span class="text-3xl font-bold leading-none">{{ currentValue!.toFixed(1) }}%</span>
          <span class="text-sm text-gray-500 dark:text-gray-400">coverage today</span>
          <span
            v-if="deltaLabel"
            class="text-sm font-medium"
            :class="trend!.delta >= 0.05 ? 'text-primary-600 dark:text-primary-400' : 'text-gray-500 dark:text-gray-400'"
          >
            {{ deltaLabel }}
          </span>
        </div>
        <CoverageJourneyChart
          :points="journey!.points"
          :granularity="journey!.granularity"
          :milestones="journey!.milestones"
          :metric="metric"
          height="300px"
          :separate-prior="jitenStore.separatePriorKnowledge"
          :scale="jitenStore.coverageJourneyScale"
        />
        <button
          v-if="hasPrior"
          class="mt-2 flex items-center gap-1.5 text-xs text-gray-500 dark:text-gray-400 hover:text-gray-700 dark:hover:text-gray-200 transition-colors"
          @click="jitenStore.separatePriorKnowledge = !jitenStore.separatePriorKnowledge"
        >
          <i :class="jitenStore.separatePriorKnowledge ? 'pi pi-check-square' : 'pi pi-stop'" class="text-[11px]" />
          Show words marked known in bulk as a starting point
        </button>
        <h3 class="text-lg font-bold pt-6 pb-2">Milestones</h3>
        <CoverageJourneyMilestones :milestones="journey!.milestones" :metric="metric" :granularity="journey!.granularity" />
        <p class="text-xs text-gray-500 dark:text-gray-400 pt-4">
          <span v-if="rangeLabel">{{ rangeLabel }}</span>
          <span v-if="rangeLabel && asOfLabel"> · </span>
          <span v-if="asOfLabel">As of your last coverage refresh: {{ asOfLabel }}</span>
        </p>
      </template>

      <div v-else-if="showThin" class="py-8 text-center">
        <p class="text-gray-500 dark:text-gray-400">Not enough history yet. Your journey starts as you study words from this title.</p>
        <p v-if="currentValue != null" class="text-2xl font-bold pt-2">{{ currentValue.toFixed(1) }}% today</p>
      </div>

      <template v-else>
        <JitenPlusGate feature="coverage-journey" feature-label="Coverage journey">
          <div>
            <p class="text-gray-500 dark:text-gray-400 mb-3">See how your comprehension of this title grew over time. (example)</p>
            <CoverageJourneyChart :points="example.points" :granularity="example.granularity" :milestones="example.milestones" height="300px" />
          </div>
        </JitenPlusGate>
        <div class="flex justify-end pt-2">
          <Button label="Hide this" icon="pi pi-eye-slash" severity="secondary" text size="small" @click="jitenStore.hideCoverageJourney = true" />
        </div>
      </template>
    </template>
  </Card>
</template>
